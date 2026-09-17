using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Extensions;
using RagEngine.Core.Infrastructure.Chunking;
using RagEngine.Core.Infrastructure.VectorStore;
using RagEngine.Core.Pipeline;
using RagEngine.Core.Services.Summary;
using Xunit;

namespace RagEngine.Core.Tests;

public sealed class VectorStoreResumeTests : IAsyncLifetime
{
    private readonly string _collection = $"rag-engine-test-resume-{Guid.NewGuid():N}";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rag-engine-test-resume-{Guid.NewGuid():N}");
    private readonly QdrantClient _client = new("localhost", 6334);
    private readonly FixtureBrain _brain = new();
    private QdrantVectorStore _store = null!;
    private string SourcePath => Path.Combine(_directory, "Fixture.cs");
    private string CachePath => Path.Combine(_directory, "summary.sqlite3");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SourcePath, "Fixture local de reanudacion: el chunker determinista produce los puntos del micro-repo.");
        _store = new QdrantVectorStore(_client, NullLogger<QdrantVectorStore>.Instance);
        await _store.EnsureCollectionAsync(_collection, 4, includeSummaryVector: true);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _client.DeleteCollectionAsync(_collection);
        }
        finally
        {
            _client.Dispose();
            // Solo el pool de esta cache; no se interrumpe el de otros tests/consumidores.
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = CachePath }.ToString());
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Pipeline_interrumpido_reanuda_tres_paginas_sin_reprocesar_ni_perder_vectores()
    {
        var chunks = Enumerable.Range(1, 207).Select(Chunk).ToArray();
        await SeedAsync(chunks);
        var initial = await ReadPointsAsync();
        Assert.Equal(207, initial.Count);
        Assert.Equal(205UL, await _store.CountResumenPendingAsync(_collection));
        Assert.Equal(new[] { 0f, 0f, 1f, 0f }, SummaryVector(initial[chunks[0].Id]));
        Assert.Null(SummaryVector(initial[chunks[1].Id]));

        using var interruption = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var firstGenerator = new FixtureGenerator();
        var progress = new InlineProgress(value =>
        {
            if (value.Stage == IngestionStage.GeneratingResumenes && value.ResumenesCompleted == 103)
                interruption.Cancel();
        });
        var interrupted = await Pipeline(chunks, firstGenerator).IngestRepositoryAsync(
            Request(), progress, interruption.Token);
        Assert.True(interruption.IsCancellationRequested);
        Assert.Equal(103, interrupted.ResumenesCompleted);
        Assert.Equal(102, interrupted.ResumenesPending);
        Assert.Equal(103, firstGenerator.Calls.Count);

        var beforeResume = await ReadPointsAsync();
        var completed = beforeResume.Values
            .Where(point => !point.Payload["resumen_pending"].BoolValue)
            .Select(point => Guid.Parse(point.Id.Uuid)).ToHashSet();
        Assert.Equal(105, completed.Count);
        Assert.Equal(SummaryVector(initial[chunks[0].Id]), SummaryVector(beforeResume[chunks[0].Id]));

        // Nueva instancia de pipeline y de cache: no depende del enumerador ni de un offset anterior.
        var secondGenerator = new FixtureGenerator();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var resumed = await Pipeline(chunks, secondGenerator).IngestRepositoryAsync(Request(), cancellationToken: deadline.Token);
        Assert.Equal(102, resumed.ResumenesCompleted);
        Assert.Equal(0, resumed.ResumenesPending);
        Assert.Equal(102, secondGenerator.Calls.Count);
        Assert.Empty(secondGenerator.Calls.Intersect(completed));
        var allCalls = firstGenerator.Calls.Concat(secondGenerator.Calls).ToArray();
        Assert.Equal(205, allCalls.Distinct().Count());
        Assert.Equal(chunks.Skip(2).Select(chunk => chunk.Id).Order(), allCalls.Order());

        var afterResume = await ReadPointsAsync();
        Assert.Equal(initial.Keys.Order(), afterResume.Keys.Order());
        Assert.All(afterResume.Values, point => Assert.False(point.Payload["resumen_pending"].BoolValue));
        foreach (var id in completed)
            Assert.Equal(SummaryVector(beforeResume[id]), SummaryVector(afterResume[id]));
        Assert.All(chunks.Skip(2), chunk =>
            Assert.Equal(new[] { 0f, 1f, 0f, 0f }, SummaryVector(afterResume[chunk.Id])));

        // Una tercera ingesta fuerza otro upsert de Fase 1 cuando TODOS los resumenes estan terminados.
        var thirdGenerator = new FixtureGenerator();
        var repeated = await Pipeline(chunks, thirdGenerator).IngestRepositoryAsync(Request(), cancellationToken: deadline.Token);
        Assert.Equal(207, repeated.ChunksIndexed);
        Assert.Equal(0, repeated.ResumenesCompleted);
        Assert.Equal(0, repeated.ResumenesPending);
        Assert.Empty(thirdGenerator.Calls);
        var afterUpsert = await ReadPointsAsync();
        foreach (var id in afterResume.Keys)
        {
            Assert.False(afterUpsert[id].Payload["resumen_pending"].BoolValue);
            Assert.Equal(SummaryVector(afterResume[id]), SummaryVector(afterUpsert[id]));
        }
        var pending = new List<PendingResumenPoint>();
        await foreach (var point in ((IVectorStoreWriter)_store).StreamPendingResumenAsync(_collection, pageSize: 2))
            pending.Add(point);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task Upsert_sin_estado_previo_borra_resumen_control_adversarial_del_oraculo()
    {
        var chunk = Chunk(1);
        await SeedAsync([chunk]);
        Assert.NotNull(SummaryVector((await ReadPointsAsync())[chunk.Id]));
        IVectorStoreWriter writer = _store;
        await writer.UpsertBatchAsync(_collection,
            [new VectorStoreBatchItem(chunk, [1f, 0f, 0f, 0f], [], null)], markResumenPending: true);
        var replaced = (await ReadPointsAsync())[chunk.Id];
        Assert.Null(SummaryVector(replaced));
        Assert.True(replaced.Payload["resumen_pending"].BoolValue);
    }

    [Fact]
    public void Puertos_y_pipeline_no_exponen_tipos_del_adaptador()
    {
        foreach (var port in new[] { typeof(IVectorStoreWriter), typeof(IVectorStoreAdmin) })
        {
            foreach (var method in port.GetMethods())
            {
                AssertPortable(method.ReturnType);
                foreach (var parameter in method.GetParameters())
                    AssertPortable(parameter.ParameterType);
            }
        }
        var parameters = Assert.Single(typeof(DefaultIngestionPipeline).GetConstructors()).GetParameters();
        Assert.Contains(parameters, p => p.ParameterType == typeof(IVectorStoreWriter));
        Assert.Contains(parameters, p => p.ParameterType == typeof(IVectorStoreAdmin));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(QdrantVectorStore));
        Assert.All(parameters, p => AssertPortable(p.ParameterType));
        Assert.Equal(typeof(IAsyncEnumerable<PendingResumenPoint>),
            typeof(IVectorStoreWriter).GetMethod(nameof(IVectorStoreWriter.StreamPendingResumenAsync))!.ReturnType);
        Assert.Equal(typeof(Guid), typeof(PendingResumenPoint).GetProperty(nameof(PendingResumenPoint.PointId))!.PropertyType);
    }

    private static void AssertPortable(Type type)
    {
        Assert.False(type.Namespace?.StartsWith("Qdrant", StringComparison.Ordinal) == true, type.FullName);
        foreach (var argument in type.GetGenericArguments())
            AssertPortable(argument);
        if (type.HasElementType)
            AssertPortable(type.GetElementType()!);
    }

    private CodeChunk Chunk(int number) => new()
    {
        Id = Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
        Content = $"public class Fixture{number} {{ public string Operacion() => \"contenido de negocio estable del fixture {number}\"; }}",
        EnrichedContent = $"fixture numero {number}",
        ContentHash = $"resume-fixture-{number}",
        Type = ChunkType.Class,
        Metadata = new CodeChunkMetadata(
            FilePath: "resume-fixture/Fixture.cs", RelativeFilePath: "Fixture.cs",
            Language: SourceLanguage.CSharp, Namespace: "Fixture", ClassName: $"Fixture{number}",
            MethodName: null, StartLine: number, EndLine: number,
            LastModified: DateTimeOffset.UnixEpoch, RepositoryName: "resume-fixture")
    };

    private Task<int> SeedAsync(IReadOnlyList<CodeChunk> chunks) =>
        ((IVectorStoreWriter)_store).UpsertBatchAsync(_collection, chunks.Select((chunk, index) =>
            new VectorStoreBatchItem(chunk, [1f, 0f, 0f, 0f], [],
                index == 0 ? new ExistingResumenState(false, [0f, 0f, 1f, 0f])
                : index == 1 ? new ExistingResumenState(false, null) : null)).ToArray(),
            markResumenPending: true);

    private async Task<Dictionary<Guid, RetrievedPoint>> ReadPointsAsync()
    {
        var points = new Dictionary<Guid, RetrievedPoint>();
        PointId? offset = null;
        do
        {
            var page = await _client.ScrollAsync(_collection, limit: 37, offset: offset,
                payloadSelector: new WithPayloadSelector { Enable = true },
                vectorsSelector: new WithVectorsSelector { Enable = true });
            foreach (var point in page.Result)
                Assert.True(points.TryAdd(Guid.Parse(point.Id.Uuid), point), "ID duplicado entre paginas");
            offset = page.NextPageOffset;
        } while (offset is not null && offset.PointIdOptionsCase != PointId.PointIdOptionsOneofCase.None);
        return points;
    }

    private static float[]? SummaryVector(RetrievedPoint point)
    {
        if (!point.Vectors.Vectors.Vectors.TryGetValue(QdrantVectorStore.SummaryVectorName, out var vector))
            return null;
        return vector.VectorCase == VectorOutput.VectorOneofCase.Dense ? vector.Dense.Data.ToArray() : vector.Data.ToArray();
    }

    private IngestionRequest Request() => new(_directory, _collection, ScanProfile.CSharpOnly,
        new ChunkingOptions { RepositoryName = "resume-fixture", BatchSize = 32 }, EnableResumenLlm: true);

    private DefaultIngestionPipeline Pipeline(IReadOnlyList<CodeChunk> chunks, FixtureGenerator generator) => new(
        new FixtureScanner(SourcePath),
        new ChunkingStrategyRouter([new FixtureChunker(chunks)], new FallbackChunkingStrategy(),
            NullLogger<ChunkingStrategyRouter>.Instance),
        _brain, new FixtureSparseTokenizer(), _store, _store, generator,
        SummaryCache.Open(CachePath, "9.1-resume-fixture"),
        Options.Create(new IngestionOptions { MaxConcurrentResumenCalls = 1 }),
        Options.Create(new OllamaOptions()), NullLogger<DefaultIngestionPipeline>.Instance);

    private sealed class InlineProgress(Action<IngestionProgress> report) : IProgress<IngestionProgress>
    {
        public void Report(IngestionProgress value) => report(value);
    }

    private sealed class FixtureScanner(string path) : IIngestionScanner
    {
        public async IAsyncEnumerable<RawArtifact> ScanAsync(string rootPath, ScanProfile profile,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new RawArtifact(path, "Fixture.cs", SourceLanguage.CSharp, DateTimeOffset.UnixEpoch, new FileInfo(path).Length);
        }
    }

    private sealed class FixtureChunker(IReadOnlyList<CodeChunk> chunks) : IChunkingStrategy
    {
        public SourceLanguage TargetLanguage => SourceLanguage.CSharp;
        public async IAsyncEnumerable<CodeChunk> ChunkAsync(RawArtifact artifact, string fileContent, ChunkingOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
            }
        }
    }

    private sealed class FixtureGenerator : IBusinessSummaryGenerator
    {
        public List<Guid> Calls { get; } = [];
        public Task<BusinessSummaryResult?> GenerateAsync(CodeChunk chunk, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(chunk.Id);
            return Task.FromResult<BusinessSummaryResult?>(new BusinessSummaryResult($"resumen {chunk.Id}", false));
        }
        public Task<BusinessSummaryResult?> GenerateForGroupAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("El fixture de aceptacion usa PerChunk.");
    }

    private sealed class FixtureSparseTokenizer : ISparseTokenizer
    {
        public IReadOnlyList<SparseEntry> Tokenize(string text) => [];
    }

    private sealed class FixtureBrain : IVectorizationBrain
    {
        public int EmbeddingDimensions => 4;
        public int MaxSequenceLength => 256;
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { 0f, 1f, 0f, 0f });
        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new[] { 1f, 0f, 0f, 0f }).ToArray());
        public async Task<VectorizationBatchResult> GenerateBatchEmbeddingsWithStatsAsync(
            IEnumerable<string> texts, CancellationToken cancellationToken = default)
        {
            var embeddings = await GenerateBatchEmbeddingsAsync(texts, cancellationToken);
            return new VectorizationBatchResult(embeddings,
                embeddings.Select(_ => new TokenizationStats(10, 254, 0, false)).ToArray());
        }
    }
}
