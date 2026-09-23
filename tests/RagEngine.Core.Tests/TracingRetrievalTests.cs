using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Diagnostics;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 13.4: contrato de los spans "rag.retrieval"/"rag.rerank" producidos por
/// <see cref="QdrantSemanticRetriever"/>. Fixture con un <see cref="ActivityListener"/>
/// directo (sin exportador OTel completo — es exactamente el "exporter en memoria"
/// que exige la ficha) contra un Qdrant real y una colección aislada por test
/// (<c>Guid.NewGuid()</c>), nunca contra datos servidos.
/// </summary>
public sealed class TracingRetrievalTests
{
    [Fact]
    public async Task Rerank_activo_produce_span_anidado_bajo_retrieval_y_bajo_un_padre_externo()
    {
        using var client = new QdrantClient("localhost", 6334);
        var store = new QdrantVectorStore(client, NullLogger<QdrantVectorStore>.Instance);
        var collection = $"rag-engine-test-tracing-{Guid.NewGuid():N}";
        await store.EnsureCollectionAsync(collection, 4);
        try
        {
            var batch = Enumerable.Range(1, 5).Select(i =>
                new VectorStoreBatchItem(Chunk(i), [1f, 0f, 0f, 0f],
                    [new SparseEntry(17, 1f)], new ExistingResumenState(false, null))).ToArray();
            await store.UpsertBatchAsync(collection, batch, waitForCommit: true);

            var captured = new List<Activity>();
            using (Listen(captured))
            {
                // Un span externo simula el "rag.turn" que abre Program.cs — Activity.Current
                // debe propagarse hacia adentro de QdrantSemanticRetriever sin que este método
                // conozca a su llamador.
                using var parent = RagEngineTracing.ActivitySource.StartActivity(RagEngineTracing.Steps.Turn);
                using var provider = Services(client, store).BuildServiceProvider();
                var retriever = ActivatorUtilities.CreateInstance<QdrantSemanticRetriever>(provider);
                await retriever.SearchAsync("tracing query", new RetrievalOptions
                {
                    Context = RetrievalContext.Local, CollectionName = collection, TopK = 3, UseReRanking = true
                });

                var retrieval = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Retrieval
                    && Tag(a, "rag.collection") == collection);
                var rerank = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Rerank
                    && a.ParentId == retrieval.Id);
                Assert.Equal(parent!.Id, retrieval.ParentId);
                Assert.Equal("3", Tag(retrieval, "rag.top_k"));
                Assert.Equal("True", Tag(retrieval, "rag.use_reranking"));
                Assert.NotNull(Tag(rerank, "rag.candidates"));
                Assert.Empty(rerank.Events);
                Assert.Equal(ActivityStatusCode.Unset, retrieval.Status);
            }
        }
        finally
        {
            await client.DeleteCollectionAsync(collection);
        }
    }

    [Fact]
    public async Task Rerank_apagado_declara_el_paso_omitido_sin_crear_un_span()
    {
        using var client = new QdrantClient("localhost", 6334);
        var store = new QdrantVectorStore(client, NullLogger<QdrantVectorStore>.Instance);
        var collection = $"rag-engine-test-tracing-{Guid.NewGuid():N}";
        await store.EnsureCollectionAsync(collection, 4);
        try
        {
            var batch = Enumerable.Range(1, 3).Select(i =>
                new VectorStoreBatchItem(Chunk(i), [1f, 0f, 0f, 0f],
                    [new SparseEntry(17, 1f)], new ExistingResumenState(false, null))).ToArray();
            await store.UpsertBatchAsync(collection, batch, waitForCommit: true);

            var captured = new List<Activity>();
            using (Listen(captured))
            {
                using var provider = Services(client, store).BuildServiceProvider();
                var retriever = ActivatorUtilities.CreateInstance<QdrantSemanticRetriever>(provider);
                await retriever.SearchAsync("tracing query no rerank", new RetrievalOptions
                {
                    Context = RetrievalContext.Local, CollectionName = collection, TopK = 3, UseReRanking = false
                });

                var retrieval = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Retrieval
                    && Tag(a, "rag.collection") == collection);
                Assert.DoesNotContain(captured, a => a.OperationName == RagEngineTracing.Steps.Rerank);
                var skipEvent = Assert.Single(retrieval.Events, e => e.Name == "rag.rerank.skipped");
                Assert.Equal("disabled", skipEvent.Tags.First(t => t.Key == "reason").Value);
            }
        }
        finally
        {
            await client.DeleteCollectionAsync(collection);
        }
    }

    [Fact]
    public async Task Fallo_de_retrieval_cierra_el_span_con_estado_de_error()
    {
        using var client = new QdrantClient("localhost", 6334);
        var store = new QdrantVectorStore(client, NullLogger<QdrantVectorStore>.Instance);
        var collection = $"rag-engine-test-tracing-{Guid.NewGuid():N}";

        var captured = new List<Activity>();
        using (Listen(captured))
        {
            using var provider = Services(client, store).BuildServiceProvider();
            var retriever = ActivatorUtilities.CreateInstance<QdrantSemanticRetriever>(provider);
            await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => retriever.SearchAsync("missing", new RetrievalOptions
            {
                Context = RetrievalContext.Local, CollectionName = collection + "-absent", TopK = 3
            }));

            var retrieval = Assert.Single(captured, a => a.OperationName == RagEngineTracing.Steps.Retrieval);
            Assert.Equal(ActivityStatusCode.Error, retrieval.Status);
        }
    }

    private static string? Tag(Activity activity, string key) =>
        activity.TagObjects.FirstOrDefault(t => t.Key == key).Value?.ToString();

    private static IDisposable Listen(List<Activity> captured)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RagEngineTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (captured) captured.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static CodeChunk Chunk(int i) => new()
    {
        Id = Guid.Parse($"{i:x8}-1111-4111-8111-111111111111"),
        Content = "identical content",
        EnrichedContent = "identical content",
        ContentHash = $"tracing-{i}",
        Type = ChunkType.Class,
        Metadata = new CodeChunkMetadata(
            $"/repo/alpha/{i}.cs", $"alpha/{i}.cs", SourceLanguage.CSharp,
            "alpha", null, null, 1, 2, DateTimeOffset.UnixEpoch, "tracing-fixture"),
        ConsumedSymbols = ["Shared"],
        DefinedSymbols = ["Shared"]
    };

    private static ServiceCollection Services(QdrantClient client, QdrantVectorStore store)
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
        services.AddSingleton(Options.Create(new TwoHopOptions()));
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
            Task.FromResult<IReadOnlyList<RetrievalResult>>(candidates.Take(topK).ToArray());
    }

    private sealed class Profiles : IRetrievalProfileResolver
    {
        public RetrievalProfile? Resolve(CollectionManifest? manifest) => null;
        public Task<RetrievalProfile?> ResolveAsync(string collectionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<RetrievalProfile?>(null);
    }
}
