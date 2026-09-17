using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.VectorStore;
using RagEngine.Core.Infrastructure.Reranking;
using RagEngine.Core.Diagnostics;
using Xunit;

namespace RagEngine.Core.Tests;

public sealed class DeterministicRankingTests
{
    private const int CandidateCount = 257; // Exceeds both 40 and the reranked prefetch limit of 120.

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Ties_crossing_prefetch_choose_canonical_ids(bool summary, int insertionOrder)
    {
        using var client = new QdrantClient("localhost", 6334);
        var store = new QdrantVectorStore(client, NullLogger<QdrantVectorStore>.Instance);
        var collection = $"rag-engine-test-ranking-{Guid.NewGuid():N}";
        await store.EnsureCollectionAsync(collection, 4, includeSummaryVector: summary);
        try
        {
            var ids = Enumerable.Range(1, CandidateCount);
            ids = insertionOrder switch
            {
                1 => ids.Reverse(),
                2 => ids.OrderBy(i => (i * 113) % CandidateCount),
                _ => ids
            };
            var batch = ids.Select(i => new VectorStoreBatchItem(
                Chunk(i), [1f, 0f, 0f, 0f], [new SparseEntry(17, 1f)],
                new ExistingResumenState(false, summary ? [1f, 0f, 0f, 0f] : null))).ToArray();
            await store.UpsertBatchAsync(collection, batch, waitForCommit: true, markResumenPending: summary,
                tenant: "allowed");

            for (var replica = 0; replica < 3; replica++)
            {
                using var provider = Services(client, store).BuildServiceProvider();
                var retriever = ActivatorUtilities.CreateInstance<QdrantSemanticRetriever>(provider);
                foreach (var k in new[] { 1, 5, 10 })
                {
                    foreach (var stage in new[] { "fusion", "two-hop", "reranker" })
                    {
                        using var stageProvider = Services(client, store, stage == "two-hop").BuildServiceProvider();
                        retriever = ActivatorUtilities.CreateInstance<QdrantSemanticRetriever>(stageProvider);
                        using var diagnostics = new RankingDiagnostics();
                        var results = await retriever.SearchAsync("tie", new RetrievalOptions
                        {
                            Context = RetrievalContext.Local, CollectionName = collection, TopK = k,
                            UseReRanking = stage == "reranker"
                        });
                        Assert.Equal(Enumerable.Range(1, k).Select(Id), results.Select(ChunkId));
                        Assert.All(results, r => Assert.Equal(Guid.Parse(r.ChunkId).ToString("D"), r.ChunkId));
                        Assert.True(diagnostics.Queries > (summary ? 3 : 2));
                        Assert.True(diagnostics.CandidatesReturned >= CandidateCount);
                    }

                    foreach (var vector in summary
                        ? new[] { QdrantVectorStore.DenseVectorName, QdrantVectorStore.SparseVectorName, QdrantVectorStore.SummaryVectorName }
                        : new[] { QdrantVectorStore.DenseVectorName, QdrantVectorStore.SparseVectorName })
                    {
                        Query query = vector == QdrantVectorStore.SparseVectorName
                            ? (new[] { 1f }, new uint[] { 17 }) : new[] { 1f, 0f, 0f, 0f };
                        var points = await DeterministicVectorQuery.SearchAsync(
                            client, collection, query, vector, null, (ulong)k,
                            new WithPayloadSelector { Enable = true },
                            NullLogger.Instance, CancellationToken.None);
                        Assert.Equal(Enumerable.Range(1, k).Select(Id), points.Select(p => p.Id.Uuid));
                    }
                }
            }

            // Mutation control: even sorting Qdrant's truncated prefix loses canonical IDs.
            var truncated = await client.QueryAsync(collection, query: new[] { 1f, 0f, 0f, 0f },
                usingVector: QdrantVectorStore.DenseVectorName, limit: 40,
                searchParams: new SearchParams { Exact = true });
            Assert.NotEqual(Enumerable.Range(1, 10).Select(Id),
                truncated.OrderBy(p => p.Id.Uuid, StringComparer.Ordinal).Take(10).Select(p => p.Id.Uuid));

            // Both forbidden points sort before every allowed ID and can seed/enter a hop.
            await store.UpsertBatchAsync(collection,
                [new VectorStoreBatchItem(Chunk(0), [1f, 0f, 0f, 0f], [new SparseEntry(17, 1f)],
                    new ExistingResumenState(false, summary ? [1f, 0f, 0f, 0f] : null))],
                markResumenPending: summary, tenant: "denied");
            var foreignModule = Chunk(0) with
            {
                Id = Guid.Parse("00000000-2222-4222-8222-222222222222"),
                Metadata = Chunk(0).Metadata with { RelativeFilePath = "beta/0.cs", Namespace = "beta" }
            };
            await store.UpsertBatchAsync(collection,
                [new VectorStoreBatchItem(foreignModule, [1f, 0f, 0f, 0f], [new SparseEntry(17, 1f)],
                    new ExistingResumenState(false, summary ? [1f, 0f, 0f, 0f] : null))],
                markResumenPending: summary, tenant: "allowed");
            for (var replica = 0; replica < 3; replica++)
            {
                foreach (var stage in new[] { "fusion", "two-hop", "reranker" })
                {
                    using var provider = Services(client, store, stage == "two-hop").BuildServiceProvider();
                    var retriever = ActivatorUtilities.CreateInstance<QdrantSemanticRetriever>(provider);
                    foreach (var k in new[] { 1, 5, 10 })
                    {
                        var results = await retriever.SearchAsync("authorized tie", new RetrievalOptions
                        {
                            Context = RetrievalContext.ForAuthorized("allowed", "alpha"),
                            CollectionName = collection, TopK = k, UseReRanking = stage == "reranker"
                        });
                        Assert.Equal(Enumerable.Range(1, k).Select(Id), results.Select(r => r.ChunkId));
                    }
                }
            }
        }
        finally
        {
            await client.DeleteCollectionAsync(collection);
        }
    }

    [Fact]
    public async Task Non_tied_scores_preserve_native_rrf_and_empty_is_not_failure()
    {
        using var client = new QdrantClient("localhost", 6334);
        var store = new QdrantVectorStore(client, NullLogger<QdrantVectorStore>.Instance);
        var collection = $"rag-engine-test-ranking-{Guid.NewGuid():N}";
        await store.EnsureCollectionAsync(collection, 4);
        try
        {
            var batch = Enumerable.Range(1, 20).Select(i =>
                new VectorStoreBatchItem(Chunk(i), [i, 21 - i, 0f, 0f],
                    [new SparseEntry(17, i)], new ExistingResumenState(false, null))).ToArray();
            await store.UpsertBatchAsync(collection, batch, waitForCommit: true);
            using var provider = Services(client, store).BuildServiceProvider();
            var retriever = ActivatorUtilities.CreateInstance<QdrantSemanticRetriever>(provider);
            var options = new RetrievalOptions
            {
                Context = RetrievalContext.Local, CollectionName = collection, TopK = 10
            };
            var native = await client.QueryAsync(collection,
                query: new Query { Fusion = Fusion.Rrf },
                prefetch:
                [
                    new PrefetchQuery { Query = new[] { 1f, 0f, 0f, 0f }, Using = QdrantVectorStore.DenseVectorName,
                        Limit = 40, ScoreThreshold = .1f },
                    new PrefetchQuery { Query = (new[] { 1f }, new uint[] { 17 }), Using = QdrantVectorStore.SparseVectorName,
                        Limit = 40 }
                ], limit: 10);
            for (var replica = 0; replica < 3; replica++)
            {
                var results = await retriever.SearchAsync("distinct", options);
                Assert.Equal(native.Select(p => p.Id.Uuid), results.Select(r => r.ChunkId));
                Assert.Equal(native.Select(p => p.Score), results.Select(r => r.SimilarityScore));
                Assert.Equal(Enumerable.Range(11, 10).Reverse().Select(Id), results.Select(r => r.ChunkId));
            }
            Assert.Empty(await retriever.SearchAsync("empty", options with { FilterByTenant = "absent" }));
            await Assert.ThrowsAsync<Grpc.Core.RpcException>(() =>
                retriever.SearchAsync("missing", options with { CollectionName = collection + "-absent" }));
        }
        finally
        {
            await client.DeleteCollectionAsync(collection);
        }
    }

    [Fact]
    public async Task Candidate_bound_and_provider_errors_fail_instead_of_returning_a_partial_prefix()
    {
        var requests = new List<ulong>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeterministicVectorQuery.CompletePrefixAsync(40, count =>
            {
                requests.Add(count);
                return Task.FromResult<IReadOnlyList<ScoredPoint>>(Enumerable.Range(1, (int)count)
                    .Select(i => new ScoredPoint { Id = new PointId { Uuid = Id(i) }, Score = 1f }).ToArray());
            }));
        Assert.Contains("Unresolved ranking tie", error.Message);
        Assert.Equal(DeterministicVectorQuery.MaxCandidates, requests[^1]);
        Assert.Equal(11, requests.Count);
        Assert.True(requests.Sum(x => (long)x) < (long)DeterministicVectorQuery.MaxCandidates * 3);
        await Assert.ThrowsAsync<IOException>(() =>
            DeterministicVectorQuery.CompletePrefixAsync(40, _ => throw new IOException("fixture outage")));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DeterministicVectorQuery.CompletePrefixAsync(40, _ => throw new OperationCanceledException()));
        Assert.Empty(await DeterministicVectorQuery.CompletePrefixAsync(40,
            _ => Task.FromResult<IReadOnlyList<ScoredPoint>>([])));
    }

    [Fact]
    public void Ranking_uses_exact_scores_then_ordinal_id_not_gate_score()
    {
        var expected = Enumerable.Range(1, 10).Select(Id).ToArray();
        for (var order = 0; order < 3; order++)
        {
            var ids = Enumerable.Range(1, 25).OrderBy(i => order switch
            {
                0 => i, 1 => -i, _ => (i * 13) % 25
            }).ToArray();
            for (var replica = 0; replica < 3; replica++)
            {
                var candidates = ids.Select(i => new RetrievalResult(Id(i), "", .9f,
                    RetrievalScoreScale.RankFusionNative, Chunk(i).Metadata, "")).ToArray();
                foreach (var k in new[] { 1, 5, 10 })
                {
                    var ranked = OnnxCrossEncoderReRanker.RankScoredCandidates(
                        candidates, ids.Select(_ => .5f).ToArray(), k);
                    Assert.Equal(expected.Take(k), ranked.Select(r => r.ChunkId));
                    ranked[0] = ranked[0] with { SimilarityScore = .01f, ScoreScale = RetrievalScoreScale.CrossEncoderStable };
                    Assert.Equal(.5, ranked[0].RankingScore);
                    Assert.Equal(expected.Take(k), ranked.Select(r => r.ChunkId));
                }
                var distinct = OnnxCrossEncoderReRanker.RankScoredCandidates(candidates,
                    ids.Select(i => i == 25 ? MathF.BitIncrement(.5f) : .5f).ToArray(), 10);
                Assert.Equal(Id(25), distinct[0].ChunkId);
                var ranks = ids.ToDictionary(Id, _ => 1);
                Assert.Equal(expected, QdrantSemanticRetriever.MergeSymbolExpansionRanks(ranks, ranks, .8, 10, 10));
                ranks[Id(25)] = 0;
                Assert.Equal(Id(25), QdrantSemanticRetriever.MergeSymbolExpansionRanks(ranks, ranks, .8, 10, 1)[0]);
                if (order != 0)
                    Assert.NotEqual(expected, candidates.OrderByDescending(r => r.SimilarityScore)
                        .Take(10).Select(r => r.ChunkId).ToArray());
            }
        }
    }

    private static string Id(int i) => $"{i:x8}-1111-4111-8111-111111111111";

    // The pre-9.1.1 adapter exposes protobuf JSON; compare the UUID, not that formatting defect.
    private static string ChunkId(RetrievalResult result) => result.ChunkId.StartsWith('{')
        ? JsonDocument.Parse(result.ChunkId).RootElement.GetProperty("uuid").GetString()!
        : result.ChunkId;

    private static CodeChunk Chunk(int i) => new()
    {
        Id = Guid.Parse(Id(i)),
        Content = "identical content",
        EnrichedContent = "identical content",
        ContentHash = $"ranking-{i}",
        Type = ChunkType.Class,
        Metadata = new CodeChunkMetadata(
            $"/repo/alpha/{i}.cs", $"alpha/{i}.cs", SourceLanguage.CSharp,
            "alpha", null, null, 1, 2, DateTimeOffset.UnixEpoch, "ranking-fixture"),
        ConsumedSymbols = ["Shared"],
        DefinedSymbols = ["Shared"]
    };

    private static ServiceCollection Services(QdrantClient client, QdrantVectorStore store, bool twoHop = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(client);
        services.AddSingleton<IVectorStoreAdmin>(store);
        services.AddSingleton<IVectorizationBrain, Brain>();
        services.AddSingleton<ISparseTokenizer, Sparse>();
        services.AddSingleton<IReRanker, Reranker>();
        services.AddSingleton<IRetrievalProfileResolver, Profiles>();
        services.AddSingleton(Options.Create(new RetrievalFusionOptions()));
        services.AddSingleton(Options.Create(new TwoHopOptions
        {
            Enabled = twoHop, SeedResults = 1, MaxExpansionResults = 15, Weight = .8
        }));
        services.AddResiliencePipeline("qdrant", _ => { });
        return services;
    }

    private sealed class Brain : IVectorizationBrain
    {
        public int EmbeddingDimensions => 4;
        public int MaxSequenceLength => 128;
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { 1f, 0f, 0f, 0f });
        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
            IEnumerable<string> texts, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("No ingestion in this fixture");
        public Task<VectorizationBatchResult> GenerateBatchEmbeddingsWithStatsAsync(
            IEnumerable<string> texts, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("No ingestion in this fixture");
    }

    private sealed class Sparse : ISparseTokenizer
    {
        public IReadOnlyList<SparseEntry> Tokenize(string text) => [new(17, 1f)];
    }

    private sealed class Reranker : IReRanker
    {
        public Task<IReadOnlyList<RetrievalResult>> ReRankAsync(
            string query, IReadOnlyList<RetrievalResult> candidates, int topK,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RetrievalResult>>(OnnxCrossEncoderReRanker.RankScoredCandidates(
                candidates, candidates.Select(_ => .5f).ToArray(), topK));
    }

    private sealed class Profiles : IRetrievalProfileResolver
    {
        public RetrievalProfile? Resolve(CollectionManifest? manifest) => null;
        public Task<RetrievalProfile?> ResolveAsync(string collectionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<RetrievalProfile?>(null);
    }
}
