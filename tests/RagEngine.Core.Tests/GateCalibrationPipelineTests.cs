using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;
using static RagEngine.Core.Tests.GateCalibrationTests;

namespace RagEngine.Core.Tests;

public sealed class GateCalibrationPipelineTests
{
    [Theory]
    [InlineData("/api/ask")]
    [InlineData("/api/ask/stream")]
    public async Task Incompatible_calibration_fails_before_sources_or_generation(string endpoint)
    {
        await using var factory = new GenerationHttpHarnessTests.GenerationFactory();
        factory.Retriever.Results = [Winner() with
        {
            GateCalibration = GateCalibrationTests.Calibration,
            CrossEncoder = Identity with { ModelSha256 = new string('c', 64) }
        }];
        using var http = factory.CreateClient();
        using var response = await http.PostAsJsonAsync(endpoint, new { query = "high", collection = "fixture" });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Empty(factory.Chat.Prompts);
        Assert.Equal(1, factory.Retriever.Calls);
        if (endpoint == "/api/ask")
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        else
        {
            Assert.Contains("event: error\n", body);
            foreach (var forbidden in new[] { "sources", "token", "done" })
                Assert.DoesNotContain($"event: {forbidden}\n", body);
        }
    }

    [Fact]
    public async Task Shared_generation_uses_profile_snapshot_and_preserves_unprofiled_control()
    {
        await using var factory = new GenerationHttpHarnessTests.GenerationFactory();
        using var scope = factory.Services.CreateScope();
        var generation = scope.ServiceProvider.GetRequiredService<IRagGenerationService>();
        foreach (var calibration in new GateCalibration?[] { null, GateCalibrationTests.Calibration, null })
        {
            factory.Retriever.Results = GateCalibration.Apply([Winner()], calibration);
            await using var stream = generation.AskStreamingAsync("q", "fixture", RetrievalContext.Local,
                useReRanking: true).GetAsyncEnumerator();
            Assert.True(await stream.MoveNextAsync());
            var ready = Assert.IsType<GenerationEvent.ContextReady>(stream.Current);
            Assert.Equal(calibration is null ? GroundingVerdict.High : GroundingVerdict.Medium, ready.Grounding);
            Assert.Single(ready.Sources);
        }
    }

    [Fact]
    public async Task Retrieval_reads_manifest_profile_and_rejects_mismatch_without_changing_nonrerank()
    {
        using var client = new QdrantClient("localhost", 6334);
        var store = new QdrantVectorStore(client, NullLogger<QdrantVectorStore>.Instance);
        var collection = $"rag-engine-test-gate-{Guid.NewGuid():N}";
        await store.EnsureCollectionAsync(collection, 4);
        try
        {
            var chunk = new CodeChunk
            {
                Id = Guid.NewGuid(), Content = "fixture", ContentHash = "fixture",
                Metadata = Winner().Metadata, EnrichedContent = "fixture", Type = ChunkType.Method
            };
            await store.UpsertBatchAsync(collection,
                [new VectorStoreBatchItem(chunk, [1f, 0f, 0f, 0f], [new SparseEntry(17, 1f)],
                    new ExistingResumenState(false, null))], waitForCommit: true);
            var manifest = new CollectionManifest
            {
                CollectionName = collection, ModelName = "fixture", ModelOnnxSha256 = "fixture",
                EmbeddingDimension = 4, Profile = "calibrated"
            };
            var catalog = new RetrievalProfileCatalogOptions
            {
                Profiles = new() { ["calibrated"] = new RetrievalProfile { GateCalibration = GateCalibrationTests.Calibration } }
            };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(client);
            services.AddSingleton<IVectorStoreAdmin>(store);
            services.AddSingleton<IVectorizationBrain, Brain>();
            services.AddSingleton<ISparseTokenizer, Sparse>();
            services.AddSingleton<IReRanker, Reranker>();
            services.AddSingleton<IRetrievalProfileResolver>(new RetrievalProfileResolver(store, Options.Create(catalog)));
            services.AddSingleton(Options.Create(new RetrievalFusionOptions()));
            services.AddSingleton(Options.Create(new TwoHopOptions()));
            services.AddResiliencePipeline("qdrant", _ => { });
            using var provider = services.BuildServiceProvider();
            var retriever = ActivatorUtilities.CreateInstance<QdrantSemanticRetriever>(provider);
            var options = new RetrievalOptions
            {
                Context = RetrievalContext.Local, CollectionName = collection, TopK = 1, UseReRanking = true
            };
            var control = await retriever.SearchAsync("q", options);
            Assert.Null(Assert.Single(control).GateCalibration);
            var nonrerank = await retriever.SearchAsync("q", options with { UseReRanking = false });
            await store.UpsertManifestAsync(collection, manifest);
            var profiled = Assert.Single(await retriever.SearchAsync("q", options));
            Assert.Equal(GateCalibrationTests.Calibration, profiled.GateCalibration);
            Assert.Equal(control[0].SimilarityScore, profiled.SimilarityScore);
            Assert.Equal(control[0].ChunkId, profiled.ChunkId);
            Assert.Equal(GroundingVerdict.Medium, Gate().Assess([profiled], .1f, "q").Verdict);

            catalog.Profiles["calibrated"] = new RetrievalProfile
            {
                GateCalibration = GateCalibrationTests.Calibration with { CrossEncoder = Identity with { ModelSha256 = new string('c', 64) } }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => retriever.SearchAsync("q", options));
            Assert.Equal(nonrerank, await retriever.SearchAsync("q", options with { UseReRanking = false }));
        }
        finally
        {
            await client.DeleteCollectionAsync(collection);
        }
    }

    private sealed class Brain : IVectorizationBrain
    {
        public int EmbeddingDimensions => 4;
        public int MaxSequenceLength => 128;
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { 1f, 0f, 0f, 0f });
        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<VectorizationBatchResult> GenerateBatchEmbeddingsWithStatsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Sparse : ISparseTokenizer
    {
        public IReadOnlyList<SparseEntry> Tokenize(string text) => [new(17, 1f)];
    }

    private sealed class Reranker : IReRanker
    {
        public Task<IReadOnlyList<RetrievalResult>> ReRankAsync(string query, IReadOnlyList<RetrievalResult> candidates,
            int topK, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RetrievalResult>>(candidates.Take(topK).Select(c => c with
            {
                SimilarityScore = .7f, ScoreScale = RetrievalScoreScale.CrossEncoderStable, CrossEncoder = Identity
            }).ToArray());
    }
}
