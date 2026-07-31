using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Chunking;
using RagEngine.Core.Infrastructure.VectorStore;
using RagEngine.Core.Diagnostics;
using RagEngine.Core.Services.Summary;

namespace RagEngine.Core.Pipeline;

/// <summary>
/// Orchestrates the full ingestion pipeline:
///   Scanner → Chunker → Channel&lt;CodeChunk&gt; → ONNX Batch → Qdrant Upsert
///
/// Uses a bounded Channel as a backpressure buffer between the producer
/// (scanner + chunker) and consumer (vectorizer + indexer), preventing OOM
/// on massive repositories.
/// </summary>
public sealed class DefaultIngestionPipeline : IIngestionPipeline
{
    private const int ChannelCapacity = 512;

    /// <summary>
    /// Número de consumidores concurrentes (vectorización + upsert).
    /// Con un solo consumidor, la latencia del upsert a Qdrant entra íntegra a la
    /// ruta crítica entre lote y lote; con varios, el upsert del lote N se solapa
    /// con la inferencia ONNX del lote N+1. Se acota para no saturar la sesión
    /// ONNX (que ya paraleliza internamente) ni el gRPC local de Qdrant.
    /// </summary>
    private static readonly int ConsumerCount = Math.Clamp(Environment.ProcessorCount / 4, 2, 4);

    /// <summary>
    /// Longitud mínima (en caracteres) del contenido de un chunk para ser indexado.
    /// Los micro-chunks (constructores boilerplate de una línea, interfaces
    /// marcador vacías, cáscaras "public static class X") no contienen información
    /// respondible, pero su EnrichedContent —casi puro encabezado con el nombre de
    /// la clase— produce embeddings artificialmente cercanos a cualquier consulta
    /// que mencione esa entidad, ensuciando el ranking de ambas ramas híbridas.
    /// </summary>
    private const int MinIndexableContentChars = 60;

    /// <summary>Umbral de fallos de conexión CONSECUTIVOS con Ollama antes de abortar la Fase 2 (decisión 3b).</summary>
    private const int ResumenConnectionFailureThreshold = 10;

    private readonly IIngestionScanner _scanner;
    private readonly ChunkingStrategyRouter _chunkRouter;
    private readonly IVectorizationBrain _brain;
    private readonly ISparseTokenizer _sparseTokenizer;
    private readonly QdrantVectorStore _vectorStore;
    private readonly IBusinessSummaryGenerator _summaryGenerator;
    private readonly SummaryCache _summaryCache;
    private readonly IOptions<IngestionOptions> _ingestionOptions;
    private readonly ILogger<DefaultIngestionPipeline> _logger;

    public DefaultIngestionPipeline(
        IIngestionScanner scanner,
        ChunkingStrategyRouter chunkRouter,
        IVectorizationBrain brain,
        ISparseTokenizer sparseTokenizer,
        QdrantVectorStore vectorStore,
        IBusinessSummaryGenerator summaryGenerator,
        SummaryCache summaryCache,
        IOptions<IngestionOptions> ingestionOptions,
        ILogger<DefaultIngestionPipeline> logger)
    {
        _scanner = scanner;
        _chunkRouter = chunkRouter;
        _brain = brain;
        _sparseTokenizer = sparseTokenizer;
        _vectorStore = vectorStore;
        _summaryGenerator = summaryGenerator;
        _summaryCache = summaryCache;
        _ingestionOptions = ingestionOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IngestionSummary> IngestRepositoryAsync(
        IngestionRequest request,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var stats = new PipelineStats();

        _logger.LogInformation(
            "Starting ingestion: {Path} → collection '{Collection}' | ForceReindex: {Force} | ConResumen: {Resumen}",
            request.RepositoryPath, request.CollectionName, request.ForceReindex, request.EnableResumenLlm);

        // ── Decisión 1: si se pide --con-resumen sin --force sobre una colección que ya
        // existe SIN el tercer vector, hace falta --force para recrearla con 3 vectores
        // (Qdrant no permite agregar un named vector a una colección ya creada). Si YA
        // lo tiene, la Fase 1 corre normalmente más abajo — no hace falta saltarla: el
        // upsert preserva el resumen ya generado de los chunks sin cambios (ver
        // GetExistingResumenStateAsync/UpsertBatchAsync), así que re-ingestar el mismo
        // path (con archivos nuevos, modificados, o sin cambios) siempre es seguro.
        if (request.EnableResumenLlm && !request.ForceReindex)
        {
            var exists = await _vectorStore.CollectionExistsAsync(request.CollectionName, cancellationToken);
            if (exists && !await _vectorStore.HasSummaryVectorAsync(request.CollectionName, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"La colección '{request.CollectionName}' ya existe sin el vector de resumen. " +
                    "Usa --force para recrearla con el tercer vector (esto reindexa todo desde cero).");
            }
        }

        {
            // ── Step 1: Prepare collection ──────────────────────────────────────
            if (request.ForceReindex)
                await _vectorStore.RecreateCollectionAsync(
                    request.CollectionName, _brain.EmbeddingDimensions, request.EnableResumenLlm, cancellationToken);
            else
                await _vectorStore.EnsureCollectionAsync(
                    request.CollectionName, _brain.EmbeddingDimensions, request.EnableResumenLlm, cancellationToken);

            // ── Step 2: Producer/Consumer via bounded Channel ───────────────────
            var channel = Channel.CreateBounded<CodeChunk>(new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });

            var producerTask = ProduceChunksAsync(
                request, channel.Writer, progress, stats, cancellationToken);

            // Varios consumidores compiten por el mismo Channel (SingleReader = false):
            // mientras uno espera el upsert de Qdrant, otro vectoriza el siguiente lote.
            var consumerTasks = Enumerable.Range(0, ConsumerCount)
                .Select(_ => ConsumeAndIndexAsync(
                    request.CollectionName, channel.Reader,
                    request.Options.BatchSize, request.EnableResumenLlm, progress, stats, cancellationToken))
                .ToArray();

            // Run producer and consumers concurrently; propagate any exception
            await Task.WhenAll(consumerTasks.Append(producerTask));
        }

        // ── Step 3: Fase 2 — resumen de negocio (opt-in, desacoplada del throughput
        // de Fase 1). Corre igual tanto si Fase 1 acaba de correr como si se saltó
        // por reanudación: siempre opera sobre los puntos marcados resumen_pending=true
        // en Qdrant, nunca sobre la lista de chunks en memoria.
        ResumenPhaseStats? resumenStats = null;
        if (request.EnableResumenLlm)
        {
            resumenStats = await RunResumenPhaseAsync(request.CollectionName, stats, progress, cancellationToken);
        }

        sw.Stop();

        var summary = new IngestionSummary(
            FilesScanned: stats.FilesScanned,
            ChunksGenerated: stats.ChunksGenerated,
            ChunksIndexed: stats.ChunksIndexed,
            FilesSkipped: stats.FilesSkipped,
            TotalDuration: sw.Elapsed,
            EstimatedMemoryPeakBytes: GC.GetTotalMemory(false),
            ResumenesCompleted: resumenStats?.Completed ?? 0,
            ResumenesPending: resumenStats?.Pending ?? 0,
            ResumenesSinNegocio: resumenStats?.SinNegocio ?? 0);

        _logger.LogInformation(
            "Ingestion complete. Files: {Files}, Chunks: {Chunks}, Indexed: {Indexed}, Duration: {Elapsed}",
            summary.FilesScanned, summary.ChunksGenerated, summary.ChunksIndexed, summary.TotalDuration);

        return summary;
    }

    // ── Producer: Scan → Read → Chunk → Write to Channel ───────────────────────
    private async Task ProduceChunksAsync(
        IngestionRequest request,
        ChannelWriter<CodeChunk> writer,
        IProgress<IngestionProgress>? progress,
        PipelineStats stats,
        CancellationToken ct)
    {
        try
        {
            await foreach (var artifact in _scanner.ScanAsync(
                request.RepositoryPath, request.Profile, ct))
            {
                progress?.Report(new IngestionProgress(
                    FilesProcessed: stats.FilesScanned,
                    TotalFilesDiscovered: 0,
                    ChunksProduced: stats.ChunksGenerated,
                    ChunksIndexed: stats.ChunksIndexed,
                    CurrentFile: artifact.RelativePath,
                    Stage: IngestionStage.Scanning));

                string content;
                try
                {
                    content = await File.ReadAllTextAsync(artifact.AbsolutePath, ct);
                }
                catch (Exception ex)
                {
                    RagEngineMetrics.IngestionErrorsTotal.Add(1, new KeyValuePair<string, object?>("stage", "read_file"));
                    _logger.LogWarning(ex, "Failed to read {File}", artifact.AbsolutePath);
                    Interlocked.Increment(ref stats.FilesSkipped);
                    continue;
                }

                Interlocked.Increment(ref stats.FilesScanned);

                var strategy = _chunkRouter.GetStrategy(artifact);

                try
                {
                    await foreach (var chunk in strategy.ChunkAsync(
                        artifact, content, request.Options, ct))
                    {
                        if (chunk.Content.AsSpan().Trim().Length < MinIndexableContentChars)
                            continue;

                        await writer.WriteAsync(chunk, ct);
                        Interlocked.Increment(ref stats.ChunksGenerated);

                        progress?.Report(new IngestionProgress(
                            FilesProcessed: stats.FilesScanned,
                            TotalFilesDiscovered: 0,
                            ChunksProduced: stats.ChunksGenerated,
                            ChunksIndexed: stats.ChunksIndexed,
                            CurrentFile: artifact.RelativePath,
                            Stage: IngestionStage.Chunking));
                    }
                }
                catch (Exception ex)
                {
                    RagEngineMetrics.IngestionErrorsTotal.Add(1, new KeyValuePair<string, object?>("stage", "chunking"));
                    _logger.LogWarning(ex, "Failed to chunk {File}", artifact.AbsolutePath);
                    Interlocked.Increment(ref stats.FilesSkipped);
                }

                // Explicit GC hint after processing large C# files with Roslyn
                if (artifact.SizeBytes > 100_000)
                    GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    // ── Consumer: Read from Channel → Batch → ONNX → Qdrant Upsert ────────────
    private async Task ConsumeAndIndexAsync(
        string collectionName,
        ChannelReader<CodeChunk> reader,
        int batchSize,
        bool markResumenPending,
        IProgress<IngestionProgress>? progress,
        PipelineStats stats,
        CancellationToken ct)
    {
        var batch = new List<CodeChunk>(batchSize);

        await foreach (var chunk in reader.ReadAllAsync(ct))
        {
            batch.Add(chunk);

            if (batch.Count >= batchSize)
            {
                await ProcessBatchAsync(collectionName, batch, markResumenPending, progress, stats, ct);
                batch.Clear();
            }
        }

        // Flush remaining chunks
        if (batch.Count > 0)
            await ProcessBatchAsync(collectionName, batch, markResumenPending, progress, stats, ct);
    }

    private async Task ProcessBatchAsync(
        string collectionName,
        List<CodeChunk> batch,
        bool markResumenPending,
        IProgress<IngestionProgress>? progress,
        PipelineStats stats,
        CancellationToken ct)
    {
        var texts = batch.Select(c => c.EnrichedContent).ToList();

        // 1. Iniciar Vectorización Densa y Dispersa en paralelo. Si la colección tiene
        // resumen habilitado, en paralelo también se busca el estado de resumen que estos
        // mismos chunk IDs ya tuvieran de una corrida anterior (decisión: re-ingestar nunca
        // debe destruir un resumen ya generado — ver GetExistingResumenStateAsync).
        var denseTask = _brain.GenerateBatchEmbeddingsAsync(texts, ct);
        var sparseTask = Task.Run(() => _sparseTokenizer.TokenizeBatch(texts), ct);
        var existingResumenTask = markResumenPending
            ? _vectorStore.GetExistingResumenStateAsync(collectionName, batch.Select(c => c.Id).ToList(), ct)
            : Task.FromResult<IReadOnlyDictionary<Guid, QdrantVectorStore.ExistingResumenState>>(
                new Dictionary<Guid, QdrantVectorStore.ExistingResumenState>());

        float[][] denseVectors;
        IReadOnlyList<IReadOnlyList<SparseEntry>> sparseVectors;
        IReadOnlyDictionary<Guid, QdrantVectorStore.ExistingResumenState> existingResumenStates;

        try
        {
            await Task.WhenAll(denseTask, sparseTask, existingResumenTask);

            denseVectors = (await denseTask).ToArray();
            sparseVectors = await sparseTask;
            existingResumenStates = await existingResumenTask;
        }
        catch (Exception)
        {
            if (denseTask.IsFaulted)
            {
                RagEngineMetrics.IngestionErrorsTotal.Add(batch.Count, new KeyValuePair<string, object?>("stage", "onnx_embedding"));
                _logger.LogError(denseTask.Exception?.InnerException ?? denseTask.Exception, "ONNX batch embedding failed for {Count} chunks.", batch.Count);
            }
            if (sparseTask.IsFaulted)
            {
                RagEngineMetrics.IngestionErrorsTotal.Add(batch.Count, new KeyValuePair<string, object?>("stage", "sparse_tokenization"));
                _logger.LogError(sparseTask.Exception?.InnerException ?? sparseTask.Exception, "Sparse tokenization failed for {Count} chunks.", batch.Count);
            }
            if (existingResumenTask.IsFaulted)
            {
                RagEngineMetrics.IngestionErrorsTotal.Add(batch.Count, new KeyValuePair<string, object?>("stage", "resumen_state_lookup"));
                _logger.LogError(existingResumenTask.Exception?.InnerException ?? existingResumenTask.Exception,
                    "Fallo consultando el estado de resumen previo para {Count} chunks.", batch.Count);
            }
            return;
        }

        // 3. Zip and Upsert
        var triples = batch
            .Select((chunk, i) => (
                Chunk: chunk,
                DenseVector: denseVectors[i],
                SparseVector: sparseVectors[i],
                ExistingResumen: existingResumenStates.GetValueOrDefault(chunk.Id)
            ))
            .ToList();

        try
        {
            // waitForCommit: false — el WAL de Qdrant garantiza durabilidad; diferir
            // la aplicación de los índices saca ~300 ms/lote de la ruta crítica.
            await _vectorStore.UpsertBatchAsync(
                collectionName, triples, waitForCommit: false, markResumenPending: markResumenPending, ct: ct);
            Interlocked.Add(ref stats.ChunksIndexed, batch.Count);
            RagEngineMetrics.ChunksIndexedTotal.Add(batch.Count, new KeyValuePair<string, object?>("collection", collectionName));

            progress?.Report(new IngestionProgress(
                FilesProcessed: stats.FilesScanned,
                TotalFilesDiscovered: 0,
                ChunksProduced: stats.ChunksGenerated,
                ChunksIndexed: stats.ChunksIndexed,
                CurrentFile: string.Empty,
                Stage: IngestionStage.Indexing));
        }
        catch (Exception ex)
        {
            RagEngineMetrics.IngestionErrorsTotal.Add(batch.Count, new KeyValuePair<string, object?>("stage", "qdrant_upsert"));
            _logger.LogError(ex, "Qdrant upsert failed for batch of {Count} chunks.", batch.Count);
        }
    }

    // ── Fase 2: resumen de negocio (opt-in) ─────────────────────────────────────
    //
    // Desacoplada del throughput de Fase 1 (decisión 3): un LLM local es órdenes de
    // magnitud más lento que ONNX, así que corre en su propio Channel + pool de
    // workers acotado por MaxConcurrentResumenCalls, DESPUÉS de que Fase 1 (si corrió)
    // ya dejó los puntos buscables por dense+sparse.
    //
    // Siempre opera vía scroll sobre Qdrant (resumen_pending=true), nunca sobre la
    // lista de chunks en memoria — así una corrida que se cortó a mitad de la Fase 2
    // se reanuda automáticamente (decisión 3a) sin distinguir código entre "recién
    // generado" y "pendiente de una corrida anterior".
    private async Task<ResumenPhaseStats> RunResumenPhaseAsync(
        string collectionName,
        PipelineStats phase1Stats,
        IProgress<IngestionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stats = new ResumenPhaseStats();
        var totalPending = (int)await _vectorStore.CountResumenPendingAsync(collectionName, cancellationToken);

        if (totalPending == 0)
        {
            _logger.LogInformation("Fase 2 (resumen de negocio): no hay puntos pendientes en '{Collection}'.", collectionName);
            return stats;
        }

        _logger.LogInformation(
            "Fase 2 (resumen de negocio): {Total} puntos pendientes en '{Collection}'.", totalPending, collectionName);

        // Circuit breaker (decisión 3b): fallos de CONEXIÓN consecutivos (no de
        // contenido/sentinel) cancelan este token compartido para abortar la fase
        // ordenadamente en vez de degradarse chunk a chunk durante horas.
        using var breakerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var consecutiveConnectionFailures = 0;
        var failureLock = new object();

        var channel = Channel.CreateBounded<QdrantVectorStore.PendingResumenPoint>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });

        var producerTask = Task.Run(async () =>
        {
            try
            {
                PointId? offset = null;
                do
                {
                    var (points, next) = await _vectorStore.ScrollPendingResumenAsync(
                        collectionName, offset, limit: 100, breakerCts.Token);

                    foreach (var point in points)
                        await channel.Writer.WriteAsync(point, breakerCts.Token);

                    offset = next;
                } while (offset is not null);
            }
            catch (OperationCanceledException)
            {
                // Cancelación real, o el circuit breaker de 3b abortó la fase.
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        var concurrency = Math.Max(1, _ingestionOptions.Value.MaxConcurrentResumenCalls);
        var consumerTasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            try
            {
                await foreach (var point in channel.Reader.ReadAllAsync(breakerCts.Token))
                {
                    try
                    {
                        await ProcessResumenPointAsync(collectionName, point, stats, breakerCts.Token);
                        lock (failureLock) consecutiveConnectionFailures = 0;
                    }
                    catch (BusinessSummaryConnectionException ex)
                    {
                        int failures;
                        lock (failureLock) failures = ++consecutiveConnectionFailures;

                        RagEngineMetrics.IngestionErrorsTotal.Add(1, new KeyValuePair<string, object?>("stage", "resumen_connection"));
                        _logger.LogWarning(ex, "Fallo de conexión con Ollama ({Failures}/{Threshold} consecutivos).",
                            failures, ResumenConnectionFailureThreshold);

                        if (failures >= ResumenConnectionFailureThreshold)
                        {
                            _logger.LogError(
                                "Ollama inalcanzable tras {Failures} fallos consecutivos — abortando Fase 2. " +
                                "Los puntos ya procesados quedan válidos; una futura corrida con --con-resumen reanuda automáticamente.",
                                failures);
                            breakerCts.Cancel();
                        }
                    }
                    catch (Exception ex)
                    {
                        RagEngineMetrics.IngestionErrorsTotal.Add(1, new KeyValuePair<string, object?>("stage", "resumen_generation"));
                        _logger.LogWarning(ex, "Fallo aislado generando resumen para el punto {PointId} ({File}).",
                            point.PointId, point.Chunk.Metadata.RelativeFilePath);
                        // Queda resumen_pending=true — se reintenta en una futura reanudación.
                    }

                    progress?.Report(new IngestionProgress(
                        FilesProcessed: phase1Stats.FilesScanned,
                        TotalFilesDiscovered: 0,
                        ChunksProduced: phase1Stats.ChunksGenerated,
                        ChunksIndexed: phase1Stats.ChunksIndexed,
                        CurrentFile: point.Chunk.Metadata.RelativeFilePath,
                        Stage: IngestionStage.GeneratingResumenes,
                        ResumenesCompleted: stats.Completed + stats.SinNegocio,
                        ResumenesTotal: totalPending));
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelación real, o el circuit breaker de 3b.
            }
        }, cancellationToken)).ToArray();

        await Task.WhenAll(consumerTasks.Append(producerTask));

        stats.Pending = (int)await _vectorStore.CountResumenPendingAsync(collectionName, CancellationToken.None);
        return stats;
    }

    private async Task ProcessResumenPointAsync(
        string collectionName,
        QdrantVectorStore.PendingResumenPoint point,
        ResumenPhaseStats stats,
        CancellationToken ct)
    {
        var (found, cachedSummary) = await _summaryCache.TryGetAsync(point.Chunk.ContentHash, ct);

        string? summaryText;
        bool sinNegocio;

        if (found)
        {
            summaryText = cachedSummary;
            sinNegocio = cachedSummary is null;
        }
        else
        {
            // Puede lanzar BusinessSummaryConnectionException — se propaga tal cual
            // para que el circuit breaker de la fase la cuente.
            var result = await _summaryGenerator.GenerateAsync(point.Chunk, ct);
            if (result is null)
                throw new InvalidOperationException("Fallo aislado al generar el resumen (no es un fallo de conexión).");

            sinNegocio = result.SinContenidoDeNegocio;
            summaryText = sinNegocio ? null : result.Text;
            await _summaryCache.SetAsync(point.Chunk.ContentHash, summaryText, ct);
        }

        if (sinNegocio)
        {
            // Sin significado de negocio: no recibe vector, pero deja de estar "pendiente".
            await _vectorStore.MarkResumenCompleteAsync(collectionName, [point.PointId], ct);
            Interlocked.Increment(ref stats.SinNegocio);
            return;
        }

        var vector = await _brain.GenerateEmbeddingAsync(summaryText!, ct);
        await _vectorStore.UpdateSummaryVectorAsync(collectionName, point.PointId, vector, ct);
        await _vectorStore.MarkResumenCompleteAsync(collectionName, [point.PointId], ct);
        Interlocked.Increment(ref stats.Completed);
    }

    private sealed class ResumenPhaseStats
    {
        public int Completed;
        public int SinNegocio;
        public int Pending;
    }

    // Mutable stats class shared between producer and consumer via Interlocked
    private sealed class PipelineStats
    {
        public int FilesScanned;
        public int ChunksGenerated;
        public int ChunksIndexed;
        public int FilesSkipped;
    }
}
