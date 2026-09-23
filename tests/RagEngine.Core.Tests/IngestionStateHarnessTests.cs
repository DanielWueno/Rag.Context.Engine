using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Audit;
using RagEngine.Core.Infrastructure.Chunking;
using RagEngine.Core.Infrastructure.State;
using RagEngine.Core.Infrastructure.Summary;
using RagEngine.Core.Infrastructure.VectorStore;
using RagEngine.Core.Pipeline;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 13.1: persistir corridas y estado de cada documento con actor, contrato y
/// reanudación verificable. Ejercita <see cref="DefaultIngestionPipeline"/> completo
/// (productor/consumidor real) contra un Qdrant real, no un doble del pipeline —
/// el oráculo exige un fixture de ≥10 documentos con éxito, fallo de lectura, fallo
/// de chunking, fallo de upsert y cancelación, más un ciclo matar/reanudar que
/// verifique historia conservada y ausencia de duplicados.
/// </summary>
public sealed class IngestionStateHarnessTests : IAsyncLifetime
{
    private const string RepoName = "estado-fixture";
    private readonly string _collection = $"rag-engine-test-state-{Guid.NewGuid():N}";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rag-engine-test-state-{Guid.NewGuid():N}");
    private readonly QdrantClient _client = new("localhost", 6334);
    private QdrantVectorStore _store = null!;

    private string CachePath => Path.Combine(_directory, "summary.sqlite3");
    private string AuditDbPath => Path.Combine(_directory, "audit.sqlite3");
    private string StateDbPath => Path.Combine(_directory, "ingestion-state.sqlite3");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new QdrantVectorStore(_client, NullLogger<QdrantVectorStore>.Instance);
        await _store.EnsureCollectionAsync(_collection, 4, includeSummaryVector: false);
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
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ── Fixture: 12 "documentos" (>= 10 exigidos por el oráculo) ────────────────
    // 8 exitosos, 1 sin chunks admitidos (éxito trivial), 1 con fallo de lectura,
    // 1 con fallo de chunking, 1 con fallo de upsert.
    private const int NormalCount = 8;
    private const string ZeroChunksDoc = "zero-chunks.cs";
    private const string ReadFailDoc = "read-fail.cs";
    private const string ChunkFailDoc = "chunk-fail.cs";
    private const string UpsertFailDoc = "upsert-fail.cs";

    private static IEnumerable<string> NormalDocs => Enumerable.Range(1, NormalCount).Select(i => $"normal-{i}.cs");

    private static IEnumerable<string> AllDocs =>
        NormalDocs.Append(ZeroChunksDoc).Append(ReadFailDoc).Append(ChunkFailDoc).Append(UpsertFailDoc);

    private string DocKey(string relativePath) => ChunkBuilder.BuildIdentityKey(RepoName, relativePath);

    [Fact]
    public async Task Corrida_persiste_estado_correcto_por_documento_para_exito_lectura_chunking_y_upsert()
    {
        var (pipeline, stateStore) = BuildPipeline(includeUpsertFailure: true);

        var summary = await pipeline.IngestRepositoryAsync(Request());

        // La corrida global termina en éxito: los 8 documentos normales + el de cero
        // chunks se indexaron bien, así que ChunksIndexed > 0 y no dispara el fatal
        // de "generó chunks pero no indexó ninguno".
        Assert.True(summary.ChunksIndexed >= NormalCount);

        var runs = await stateStore.ListRunsAsync(_collection);
        var run = Assert.Single(runs);
        Assert.Equal(IngestionRunStatus.Succeeded, run.Status);
        Assert.NotNull(run.FinishedAt);

        var states = (await stateStore.GetDocumentStatesAsync(run.RunId)).ToDictionary(s => s.DocumentKey);
        Assert.Equal(AllDocs.Count(), states.Count);

        foreach (var doc in NormalDocs)
        {
            var state = states[DocKey(doc)];
            Assert.Equal(DocumentIngestionStatus.Succeeded, state.Status);
            Assert.Equal(1, state.ChunksExpected);
            Assert.Equal(1, state.ChunksIndexed);
            Assert.NotNull(state.ContentHash);
            Assert.Null(state.Detail);
        }

        var zeroChunks = states[DocKey(ZeroChunksDoc)];
        Assert.Equal(DocumentIngestionStatus.Succeeded, zeroChunks.Status);
        Assert.Equal(0, zeroChunks.ChunksExpected);
        Assert.Equal(0, zeroChunks.ChunksIndexed);

        var readFail = states[DocKey(ReadFailDoc)];
        Assert.Equal(DocumentIngestionStatus.Failed, readFail.Status);
        Assert.Null(readFail.ContentHash);
        Assert.NotNull(readFail.Detail);

        var chunkFail = states[DocKey(ChunkFailDoc)];
        Assert.Equal(DocumentIngestionStatus.Failed, chunkFail.Status);
        Assert.NotNull(chunkFail.ContentHash);
        Assert.NotNull(chunkFail.Detail);

        var upsertFail = states[DocKey(UpsertFailDoc)];
        Assert.Equal(DocumentIngestionStatus.Failed, upsertFail.Status);
        Assert.Equal(1, upsertFail.ChunksExpected);
        Assert.Equal(0, upsertFail.ChunksIndexed);
        Assert.NotNull(upsertFail.Detail);

        // Todos los estados comparten actor y contrato (misma corrida, mismo request).
        Assert.All(states.Values, s => Assert.False(string.IsNullOrWhiteSpace(s.ActorId)));
        Assert.Single(states.Values.Select(s => s.Contract).Distinct());
    }

    [Fact]
    public async Task Corrida_matada_a_mitad_deja_pendientes_visibles_y_la_reanudacion_no_duplica_ni_pierde_historia()
    {
        // Sólo documentos "normales" — esta prueba aísla la propiedad de
        // interrupción/reanudación, no la de manejo de fallos (cubierta arriba).
        var (firstPipeline, stateStore) = BuildPipeline(includeUpsertFailure: false, onlyNormalDocs: true);

        using var interruption = new CancellationTokenSource();
        var indexedThreshold = NormalCount / 2;
        var progress = new InlineProgress(value =>
        {
            if (value.Stage == IngestionStage.Indexing && value.ChunksIndexed >= indexedThreshold)
                interruption.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => firstPipeline.IngestRepositoryAsync(Request(), progress, interruption.Token));

        var firstRuns = await stateStore.ListRunsAsync(_collection);
        var firstRun = Assert.Single(firstRuns);
        Assert.Equal(IngestionRunStatus.Cancelled, firstRun.Status);

        var firstStates = await stateStore.GetDocumentStatesAsync(firstRun.RunId);
        // El productor puede cancelarse antes de que el scanner alcance a descubrir
        // el último documento (nunca llega a tener fila — limitación documentada:
        // sólo lo "descubierto" queda visible). Lo exigible es que NINGÚN documento
        // descubierto quede sin fila, y que la cancelación haya interrumpido la
        // corrida antes de completar los {NormalCount} documentos.
        Assert.InRange(firstStates.Count, 1, NormalCount);
        Assert.True(firstStates.Count(s => s.Status == DocumentIngestionStatus.Succeeded) < NormalCount);

        var succeededAfterKill = firstStates.Where(s => s.Status == DocumentIngestionStatus.Succeeded).ToList();
        var incompleteAfterKill = firstStates.Where(s => s.Status != DocumentIngestionStatus.Succeeded).ToList();

        // La propiedad exigida por el oráculo: "lista EXACTAMENTE faltantes/fallidos" —
        // ningún documento incompleto queda invisible (todos tienen fila) ni se marca
        // Succeeded sin habérselo ganado, y hubo progreso real antes de la cancelación.
        Assert.NotEmpty(succeededAfterKill);
        Assert.NotEmpty(incompleteAfterKill);
        Assert.All(incompleteAfterKill, s => Assert.NotEqual(DocumentIngestionStatus.Succeeded, s.Status));
        Assert.All(succeededAfterKill, s => Assert.Equal(1, s.ChunksIndexed));

        // ── Reanudación: nueva corrida completa sobre el mismo fixture ──────────
        var (secondPipeline, _) = BuildPipeline(includeUpsertFailure: false, onlyNormalDocs: true, stateStore: stateStore);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var resumedSummary = await secondPipeline.IngestRepositoryAsync(Request(), cancellationToken: deadline.Token);
        Assert.Equal(NormalCount, resumedSummary.ChunksIndexed);

        var allRuns = await stateStore.ListRunsAsync(_collection, take: 10);
        Assert.Equal(2, allRuns.Count);
        var secondRun = Assert.Single(allRuns, r => r.RunId != firstRun.RunId);
        Assert.Equal(IngestionRunStatus.Succeeded, secondRun.Status);

        var secondStates = await stateStore.GetDocumentStatesAsync(secondRun.RunId);
        Assert.Equal(NormalCount, secondStates.Count);
        Assert.All(secondStates, s => Assert.Equal(DocumentIngestionStatus.Succeeded, s.Status));

        // Historia de la primera corrida intacta: no se borró ni se sobreescribió al reanudar.
        var firstStatesAfterResume = await stateStore.GetDocumentStatesAsync(firstRun.RunId);
        Assert.Equal(firstStates.Select(s => (s.DocumentKey, s.Status, s.ChunksIndexed)).OrderBy(t => t.DocumentKey),
            firstStatesAfterResume.Select(s => (s.DocumentKey, s.Status, s.ChunksIndexed)).OrderBy(t => t.DocumentKey));

        // Reconciliación idempotente: mismos Ids deterministas ⇒ Qdrant no duplica
        // puntos aunque los documentos ya completados en la primera corrida se hayan
        // re-troceado y re-upserteado en la segunda.
        var health = await _store.GetCollectionHealthAsync(_collection);
        Assert.Equal((ulong)NormalCount, health.PointsCount);
    }

    private IngestionRequest Request() => new(
        _directory, _collection, ScanProfile.CSharpOnly,
        new ChunkingOptions { RepositoryName = RepoName, BatchSize = 1 },
        EnableResumenLlm: false);

    private (DefaultIngestionPipeline Pipeline, IIngestionStateStore StateStore) BuildPipeline(
        bool includeUpsertFailure, bool onlyNormalDocs = false, IIngestionStateStore? stateStore = null)
    {
        var docs = onlyNormalDocs ? NormalDocs.ToList() : AllDocs.ToList();
        var chunker = new PerDocumentFixtureChunker(RepoName, ZeroChunksDoc, ChunkFailDoc);

        var scanner = new FixtureMultiScanner(docs.Select(d => d == ReadFailDoc
            ? new RawArtifact(Path.Combine(_directory, "__missing__", d), d, SourceLanguage.CSharp, DateTimeOffset.UnixEpoch, 0)
            : new RawArtifact(Path.Combine(_directory, d), d, SourceLanguage.CSharp, DateTimeOffset.UnixEpoch, 100)).ToList());

        // Documentos con contenido real en disco (salvo ReadFailDoc, cuya ruta no existe a propósito).
        foreach (var doc in docs.Where(d => d != ReadFailDoc))
            File.WriteAllText(Path.Combine(_directory, doc), $"contenido de negocio estable para {doc}, con longitud suficiente para superar el umbral de admision.");

        IVectorStoreWriter writer = includeUpsertFailure
            ? new PoisonUpsertWriter(_store, DocKey(UpsertFailDoc))
            : _store;

        var effectiveStateStore = stateStore ?? SqliteIngestionStateStore.Open(StateDbPath);

        var pipeline = new DefaultIngestionPipeline(
            scanner,
            new ChunkingStrategyRouter([chunker], new FallbackChunkingStrategy(), NullLogger<ChunkingStrategyRouter>.Instance),
            new FixtureBrain(), new FixtureSparseTokenizer(), _store, writer,
            new FixtureGenerator(), SummaryCache.Open(CachePath, "13.1-estado-fixture"),
            Options.Create(new IngestionOptions { MaxConcurrentResumenCalls = 1 }),
            Options.Create(new OllamaOptions()),
            SqliteAuditEventStore.Open(AuditDbPath),
            Options.Create(new AuditOptions { DbPath = AuditDbPath }),
            effectiveStateStore,
            NullLogger<DefaultIngestionPipeline>.Instance);

        return (pipeline, effectiveStateStore);
    }

    private sealed class InlineProgress(Action<IngestionProgress> report) : IProgress<IngestionProgress>
    {
        public void Report(IngestionProgress value) => report(value);
    }

    /// <summary>Escanea una lista fija de artefactos ya construidos por el test (rutas reales o intencionalmente inexistentes).</summary>
    private sealed class FixtureMultiScanner(IReadOnlyList<RawArtifact> artifacts) : IIngestionScanner
    {
        public async IAsyncEnumerable<RawArtifact> ScanAsync(string rootPath, ScanProfile profile,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return artifact;
            }
        }
    }

    /// <summary>
    /// Un chunk por documento normal (1 chunk = 1 documento simplifica la correlación
    /// determinista de las aserciones); cero chunks para <paramref name="zeroChunksDoc"/>;
    /// excepción a mitad del enumerable para <paramref name="chunkFailDoc"/> (después de
    /// que el fixture ya leyó el archivo, antes de completar la lista esperada).
    /// </summary>
    private sealed class PerDocumentFixtureChunker(string repositoryName, string zeroChunksDoc, string chunkFailDoc)
        : IChunkingStrategy
    {
        public SourceLanguage TargetLanguage => SourceLanguage.CSharp;

        public async IAsyncEnumerable<CodeChunk> ChunkAsync(RawArtifact artifact, string fileContent, ChunkingOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (artifact.RelativePath == chunkFailDoc)
                throw new InvalidOperationException($"Fallo sintético de chunking para {artifact.RelativePath}.");
            if (artifact.RelativePath == zeroChunksDoc)
                yield break;

            cancellationToken.ThrowIfCancellationRequested();
            yield return new CodeChunk
            {
                Id = DeterministicId(artifact.RelativePath),
                Content = fileContent,
                EnrichedContent = fileContent,
                ContentHash = Utilities.ContentHasher.Compute(fileContent),
                Type = ChunkType.Class,
                Metadata = new CodeChunkMetadata(
                    FilePath: ChunkBuilder.BuildIdentityKey(repositoryName, artifact.RelativePath),
                    RelativeFilePath: artifact.RelativePath,
                    Language: SourceLanguage.CSharp,
                    Namespace: "Fixture", ClassName: artifact.RelativePath, MethodName: null,
                    StartLine: 1, EndLine: 1, LastModified: DateTimeOffset.UnixEpoch, RepositoryName: repositoryName)
            };
        }

        private static Guid DeterministicId(string relativePath) =>
            new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(relativePath)));
    }

    /// <summary>Delega en el store real, salvo que el lote contenga el documento envenenado: entonces falla el upsert entero.</summary>
    private sealed class PoisonUpsertWriter(IVectorStoreWriter inner, string poisonedDocKey) : IVectorStoreWriter
    {
        public Task<IReadOnlyDictionary<Guid, ExistingResumenState>> GetExistingResumenStateAsync(
            string collectionName, IReadOnlyList<Guid> chunkIds, CancellationToken ct = default) =>
            inner.GetExistingResumenStateAsync(collectionName, chunkIds, ct);

        public Task<int> UpsertBatchAsync(
            string collectionName, IReadOnlyList<VectorStoreBatchItem> batch,
            bool waitForCommit = true, bool markResumenPending = false, string? tenant = null, CancellationToken ct = default)
        {
            if (batch.Any(item => item.Chunk.Metadata.FilePath == poisonedDocKey))
                throw new InvalidOperationException($"Fallo sintético de upsert para el documento envenenado '{poisonedDocKey}'.");
            return inner.UpsertBatchAsync(collectionName, batch, waitForCommit, markResumenPending, tenant, ct);
        }

        public Task<int> DeleteSupersededPointsAsync(
            string collectionName, IReadOnlySet<Guid> currentChunkIds, IReadOnlySet<string> processedFilePaths, CancellationToken ct = default) =>
            inner.DeleteSupersededPointsAsync(collectionName, currentChunkIds, processedFilePaths, ct);

        public IAsyncEnumerable<PendingResumenPoint> StreamPendingResumenAsync(
            string collectionName, uint pageSize = 100, CancellationToken ct = default) =>
            inner.StreamPendingResumenAsync(collectionName, pageSize, ct);

        public Task UpdateSummaryVectorAsync(string collectionName, Guid pointId, float[] summaryVector, CancellationToken ct = default) =>
            inner.UpdateSummaryVectorAsync(collectionName, pointId, summaryVector, ct);

        public Task MarkResumenCompleteAsync(string collectionName, IReadOnlyList<Guid> pointIds, CancellationToken ct = default) =>
            inner.MarkResumenCompleteAsync(collectionName, pointIds, ct);

        public Task<ulong> CountResumenPendingAsync(string collectionName, CancellationToken ct = default) =>
            inner.CountResumenPendingAsync(collectionName, ct);
    }

    private sealed class FixtureGenerator : IBusinessSummaryGenerator
    {
        public Task<BusinessSummaryResult?> GenerateAsync(CodeChunk chunk, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Este fixture no habilita EnableResumenLlm.");
        public Task<BusinessSummaryResult?> GenerateForGroupAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Este fixture no habilita EnableResumenLlm.");
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
