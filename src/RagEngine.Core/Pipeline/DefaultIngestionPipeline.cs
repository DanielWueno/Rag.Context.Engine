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
using RagEngine.Core.Extensions;
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
    /// Longitud mínima del contenido crudo, salvo declaraciones de tipos cuando
    /// se habilita el experimento: su nombre es una respuesta válida aunque sea corto.
    /// Los demás micro-chunks siguen fuera para que un encabezado largo no
    /// convierta boilerplate en un candidato artificialmente cercano.
    /// </summary>
    private const int MinIndexableContentChars = 60;

    internal static bool IsIndexable(CodeChunk chunk, bool indexShortTypeDeclarations)
    {
        var contentLength = chunk.Content.AsSpan().Trim().Length;
        if (contentLength >= MinIndexableContentChars)
            return true;

        // Class también etiqueta grupos de campos; sólo se exime el chunk que
        // declara el propio tipo, no cualquiera que pertenezca a él.
        var typeName = chunk.Metadata.ClassName;
        return indexShortTypeDeclarations
            && contentLength > 0
            && chunk.Type is ChunkType.Class or ChunkType.Interface
            && !string.IsNullOrWhiteSpace(typeName)
            && chunk.DefinedSymbols.Contains(typeName, StringComparer.Ordinal);
    }

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
    private readonly string _groupPromptVersion;
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
        IOptions<OllamaOptions> ollamaOptions,
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
        // Ítem 5.b: namespace de caché propio del modo por archivo/tipo, calculado una
        // sola vez aquí (no en DI) porque sólo se usa cuando SummaryGranularity=PerFile.
        _groupPromptVersion = OllamaBusinessSummaryGenerator.ComputeGroupPromptVersion(ollamaOptions.Value.ModelId);
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
            "Starting ingestion: {Path} → collection '{Collection}' | ForceReindex: {Force} | ConResumen: {Resumen} | IndexShortTypeDeclarations: {ShortTypes}",
            request.RepositoryPath, request.CollectionName, request.ForceReindex, request.EnableResumenLlm,
            _ingestionOptions.Value.IndexShortTypeDeclarations);

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

        // Ids vigentes y archivos efectivamente procesados: alimentan la limpieza de
        // puntos obsoletos al cerrar la Fase 1. El productor es una sola tarea secuencial,
        // así que no hacen falta colecciones concurrentes.
        var generatedIds = new HashSet<Guid>();
        var processedFiles = new HashSet<string>(StringComparer.Ordinal);

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
                request, channel.Writer, progress, stats, generatedIds, processedFiles, cancellationToken);

            // Varios consumidores compiten por el mismo Channel (SingleReader = false):
            // mientras uno espera el upsert de Qdrant, otro vectoriza el siguiente lote.
            var consumerTasks = Enumerable.Range(0, ConsumerCount)
                .Select(_ => ConsumeAndIndexAsync(
                    request.CollectionName, channel.Reader,
                    request.Options.BatchSize, request.EnableResumenLlm, request.Tenant, progress, stats, cancellationToken))
                .ToArray();

            // Run producer and consumers concurrently; propagate any exception
            await Task.WhenAll(consumerTasks.Append(producerTask));
        }

        // Fase 1 produjo chunks pero Qdrant no aceptó ninguno: antes esto se
        // registraba como "Ingestion complete. Indexed: 0" y el proceso salía con
        // éxito, dejando la colección silenciosamente sin actualizar. Es fatal.
        if (stats.ChunksGenerated > 0 && stats.ChunksIndexed == 0)
            throw new InvalidOperationException(
                $"La ingesta generó {stats.ChunksGenerated} chunks pero Qdrant no indexó ninguno " +
                $"en la colección '{request.CollectionName}'. Revisa los errores de upsert en el log; " +
                "la colección quedó sin cambios.");

        // Un chunk cuyo archivo cambió (o al que el chunker reagrupó) entra con un Id
        // nuevo y deja el viejo indexado para siempre. Con --force no aplica: la
        // colección se acaba de recrear y no hay nada obsoleto que barrer.
        if (!request.ForceReindex)
        {
            stats.PointsDeleted = await _vectorStore.DeleteSupersededPointsAsync(
                request.CollectionName, generatedIds, processedFiles, cancellationToken);
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

        var (tokensN, tokensP50, tokensP95) = stats.ComputeTokenPercentiles();

        var summary = new IngestionSummary(
            FilesScanned: stats.FilesScanned,
            ChunksGenerated: stats.ChunksGenerated,
            ChunksIndexed: stats.ChunksIndexed,
            FilesSkipped: stats.FilesSkipped,
            TotalDuration: sw.Elapsed,
            EstimatedMemoryPeakBytes: GC.GetTotalMemory(false),
            ResumenesCompleted: resumenStats?.Completed ?? 0,
            ResumenesPending: resumenStats?.Pending ?? 0,
            ResumenesSinNegocio: resumenStats?.SinNegocio ?? 0,
            TokensObserved: tokensN,
            TokensP50: tokensP50,
            TokensP95: tokensP95,
            TokensMaxUsable: stats.TokensMaxUsable,
            ChunksTruncatedTotal: stats.ChunksTruncatedTotal,
            TokensDiscardedTotal: stats.TokensDiscardedTotal,
            ResumenGranularity: _ingestionOptions.Value.SummaryGranularity.ToString(),
            ResumenGroups: resumenStats?.Groups ?? 0,
            ResumenLlmCalls: resumenStats?.LlmCalls ?? 0,
            ResumenCacheHits: resumenStats?.CacheHits ?? 0,
            ResumenCacheMisses: resumenStats?.CacheMisses ?? 0,
            ResumenElapsedMs: resumenStats?.ElapsedMs ?? 0);

        _logger.LogInformation(
            "Ingestion complete. Files: {Files}, Chunks: {Chunks}, Indexed: {Indexed}, Obsoletos borrados: {Deleted}, Duration: {Elapsed}",
            summary.FilesScanned, summary.ChunksGenerated, summary.ChunksIndexed, stats.PointsDeleted, summary.TotalDuration);

        return summary;
    }

    // ── Producer: Scan → Read → Chunk → Write to Channel ───────────────────────
    private async Task ProduceChunksAsync(
        IngestionRequest request,
        ChannelWriter<CodeChunk> writer,
        IProgress<IngestionProgress>? progress,
        PipelineStats stats,
        HashSet<Guid> generatedIds,
        HashSet<string> processedFiles,
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
                        if (!IsIndexable(chunk, _ingestionOptions.Value.IndexShortTypeDeclarations))
                        {
                            _logger.LogDebug(
                                "Skipping chunk {ChunkId} ({ChunkType}) in {File}: below the admission threshold",
                                chunk.Id, chunk.Type, artifact.RelativePath);
                            continue;
                        }

                        generatedIds.Add(chunk.Id);
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
                    continue;
                }

                // Misma clave que ChunkBuilder.BuildIdentityKey (ítem 8.f): el barrido
                // de obsoletos compara contra "file_path" en el payload, que ya no es
                // la ruta absoluta, así que la comparación debe usar la misma clave.
                processedFiles.Add(ChunkBuilder.BuildIdentityKey(
                    request.Options.RepositoryName, artifact.RelativePath));

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
        string? tenant,
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
                await ProcessBatchAsync(collectionName, batch, markResumenPending, tenant, progress, stats, ct);
                batch.Clear();
            }
        }

        // Flush remaining chunks
        if (batch.Count > 0)
            await ProcessBatchAsync(collectionName, batch, markResumenPending, tenant, progress, stats, ct);
    }

    private async Task ProcessBatchAsync(
        string collectionName,
        List<CodeChunk> batch,
        bool markResumenPending,
        string? tenant,
        IProgress<IngestionProgress>? progress,
        PipelineStats stats,
        CancellationToken ct)
    {
        var texts = batch.Select(c => c.EnrichedContent).ToList();

        // 1. Iniciar Vectorización Densa y Dispersa en paralelo. Si la colección tiene
        // resumen habilitado, en paralelo también se busca el estado de resumen que estos
        // mismos chunk IDs ya tuvieran de una corrida anterior (decisión: re-ingestar nunca
        // debe destruir un resumen ya generado — ver GetExistingResumenStateAsync).
        var denseTask = _brain.GenerateBatchEmbeddingsWithStatsAsync(texts, ct);
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

            var denseResult = await denseTask;
            denseVectors = denseResult.Embeddings.ToArray();
            sparseVectors = await sparseTask;
            existingResumenStates = await existingResumenTask;

            // 11.1: contar T/descartados/truncados de la fase de embedding de
            // chunks ADMITIDOS, independientemente de si el upsert a Qdrant
            // más abajo termina en éxito o error — el costo de tokenización ya
            // se pagó y es lo que hay que medir.
            stats.RecordTokenizationStats(denseResult.Stats);
            long batchTruncated = denseResult.Stats.Count(s => s.Truncated);
            long batchDiscarded = denseResult.Stats.Sum(s => (long)s.Discarded);
            if (batchTruncated > 0)
                RagEngineMetrics.ChunksTruncatedTotal.Add(batchTruncated, new KeyValuePair<string, object?>("collection", collectionName));
            if (batchDiscarded > 0)
                RagEngineMetrics.TokensDiscardedTotal.Add(batchDiscarded, new KeyValuePair<string, object?>("collection", collectionName));
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
                collectionName, triples, waitForCommit: false, markResumenPending: markResumenPending, tenant: tenant, ct: ct);
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
        var phaseSw = Stopwatch.StartNew();
        var totalPending = (int)await _vectorStore.CountResumenPendingAsync(collectionName, cancellationToken);

        if (totalPending == 0)
        {
            _logger.LogInformation("Fase 2 (resumen de negocio): no hay puntos pendientes en '{Collection}'.", collectionName);
            return stats;
        }

        _logger.LogInformation(
            "Fase 2 (resumen de negocio): {Total} puntos pendientes en '{Collection}'.", totalPending, collectionName);

        // Ítem 5.b (experimental, opt-in): agrupa por (archivo, tipo) en vez de llamar al
        // LLM por chunk. Requiere cargar TODOS los puntos pendientes en memoria para
        // agruparlos (no hay agrupación posible en streaming puro sobre el scroll) — límite
        // aceptable para los corpus de evaluación de este experimento, documentado en la
        // ficha; no pensado (todavía) para una reingesta de producción de gran escala.
        if (_ingestionOptions.Value.SummaryGranularity == SummaryGranularity.PerFile)
            return await RunResumenPhasePerFileAsync(collectionName, phase1Stats, totalPending, progress, cancellationToken);

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
        _logger.LogInformation(
            "Fase 2 (resumen de negocio, modo PerChunk) terminada en {WallMs} ms: {LlmCalls} llamadas LLM, {Hits} hits/{Misses} misses de caché.",
            phaseSw.ElapsedMilliseconds, stats.LlmCalls, stats.CacheHits, stats.CacheMisses);
        return stats;
    }

    private async Task ProcessResumenPointAsync(
        string collectionName,
        QdrantVectorStore.PendingResumenPoint point,
        ResumenPhaseStats stats,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (found, cachedSummary) = await _summaryCache.TryGetAsync(point.Chunk.ContentHash, ct);

        string? summaryText;
        bool sinNegocio;

        if (found)
        {
            summaryText = cachedSummary;
            sinNegocio = cachedSummary is null;
            Interlocked.Increment(ref stats.CacheHits);
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
            Interlocked.Increment(ref stats.CacheMisses);
            Interlocked.Increment(ref stats.LlmCalls);
        }

        Interlocked.Increment(ref stats.Groups);

        if (sinNegocio)
        {
            // Sin significado de negocio: no recibe vector, pero deja de estar "pendiente".
            await _vectorStore.MarkResumenCompleteAsync(collectionName, [point.PointId], ct);
            Interlocked.Increment(ref stats.SinNegocio);
            Interlocked.Add(ref stats.ElapsedMs, sw.ElapsedMilliseconds);
            return;
        }

        var vector = await _brain.GenerateEmbeddingAsync(summaryText!, ct);
        await _vectorStore.UpdateSummaryVectorAsync(collectionName, point.PointId, vector, ct);
        await _vectorStore.MarkResumenCompleteAsync(collectionName, [point.PointId], ct);
        Interlocked.Increment(ref stats.Completed);
        Interlocked.Add(ref stats.ElapsedMs, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Ítem 5.b (experimental, opt-in): variante de la Fase 2 que agrupa los puntos
    /// pendientes por (RelativeFilePath, ClassName) y hace UNA llamada al LLM por grupo,
    /// reutilizando el resultado para todos los chunks del grupo. Instrumenta llamadas
    /// LLM, hits/misses de caché y tiempo total — el comparador A/B de la ficha exige
    /// esta evidencia, no sólo el recall.
    /// </summary>
    private async Task<ResumenPhaseStats> RunResumenPhasePerFileAsync(
        string collectionName,
        PipelineStats phase1Stats,
        int totalPending,
        IProgress<IngestionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stats = new ResumenPhaseStats();
        var sw = Stopwatch.StartNew();

        // Carga completa: la agrupación por archivo/tipo no es expresable como streaming
        // puro sobre el scroll (un grupo puede completarse en cualquier punto del corpus).
        var allPoints = new List<QdrantVectorStore.PendingResumenPoint>(totalPending);
        PointId? offset = null;
        do
        {
            var (points, next) = await _vectorStore.ScrollPendingResumenAsync(collectionName, offset, limit: 200, cancellationToken);
            allPoints.AddRange(points);
            offset = next;
        } while (offset is not null);

        var groups = allPoints
            .GroupBy(p => (p.Chunk.Metadata.RelativeFilePath, Type: p.Chunk.Metadata.ClassName ?? string.Empty))
            .ToList();

        _logger.LogInformation(
            "Fase 2 (resumen de negocio, modo PerFile): {Groups} grupos archivo/tipo para {Total} chunks pendientes en '{Collection}'.",
            groups.Count, allPoints.Count, collectionName);

        var completedCount = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunks = group.Select(p => p.Chunk).ToList();

            // Hash de grupo: sobre los content_hash ordenados de sus chunks — determinista
            // e independiente del orden de scroll, y jamás colisiona con un hash de chunk
            // individual porque vive bajo su propio prompt_version (_groupPromptVersion).
            var groupHash = ComputeGroupContentHash(chunks);

            var (found, cachedSummary) = await _summaryCache.TryGetAsync(groupHash, cancellationToken, _groupPromptVersion);
            string? summaryText;
            bool sinNegocio;

            if (found)
            {
                summaryText = cachedSummary;
                sinNegocio = cachedSummary is null;
                Interlocked.Increment(ref stats.CacheHits);
            }
            else
            {
                var result = await _summaryGenerator.GenerateForGroupAsync(chunks, cancellationToken);
                if (result is null)
                    throw new InvalidOperationException(
                        $"Fallo aislado al generar el resumen de grupo para {group.Key.RelativeFilePath} (no es un fallo de conexión).");

                sinNegocio = result.SinContenidoDeNegocio;
                summaryText = sinNegocio ? null : result.Text;
                await _summaryCache.SetAsync(groupHash, summaryText, cancellationToken, _groupPromptVersion);
                Interlocked.Increment(ref stats.CacheMisses);
                Interlocked.Increment(ref stats.LlmCalls);
            }

            Interlocked.Increment(ref stats.Groups);

            var pointIds = group.Select(p => p.PointId).ToList();
            if (sinNegocio)
            {
                await _vectorStore.MarkResumenCompleteAsync(collectionName, pointIds, cancellationToken);
                stats.SinNegocio += pointIds.Count;
            }
            else
            {
                var vector = await _brain.GenerateEmbeddingAsync(summaryText!, cancellationToken);
                foreach (var pointId in pointIds)
                    await _vectorStore.UpdateSummaryVectorAsync(collectionName, pointId, vector, cancellationToken);
                await _vectorStore.MarkResumenCompleteAsync(collectionName, pointIds, cancellationToken);
                stats.Completed += pointIds.Count;
            }

            completedCount += pointIds.Count;
            progress?.Report(new IngestionProgress(
                FilesProcessed: phase1Stats.FilesScanned,
                TotalFilesDiscovered: 0,
                ChunksProduced: phase1Stats.ChunksGenerated,
                ChunksIndexed: phase1Stats.ChunksIndexed,
                CurrentFile: group.Key.RelativeFilePath,
                Stage: IngestionStage.GeneratingResumenes,
                ResumenesCompleted: completedCount,
                ResumenesTotal: totalPending));
        }

        stats.ElapsedMs = sw.ElapsedMilliseconds;
        stats.Pending = (int)await _vectorStore.CountResumenPendingAsync(collectionName, CancellationToken.None);
        _logger.LogInformation(
            "Fase 2 (resumen de negocio, modo PerFile) terminada en {WallMs} ms: {Groups} grupos, {LlmCalls} llamadas LLM, {Hits} hits/{Misses} misses de caché.",
            sw.ElapsedMilliseconds, stats.Groups, stats.LlmCalls, stats.CacheHits, stats.CacheMisses);
        return stats;
    }

    /// <summary>SHA-256 sobre los content_hash del grupo, ordenados para ser independiente del orden de scroll.</summary>
    internal static string ComputeGroupContentHash(IReadOnlyList<CodeChunk> chunks)
    {
        var joined = string.Join('|', chunks.Select(c => c.ContentHash).OrderBy(h => h, StringComparer.Ordinal));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }


    private sealed class ResumenPhaseStats
    {
        public int Completed;
        public int SinNegocio;
        public int Pending;

        // ── 5.b: instrumentación A/B por granularidad (llamadas LLM, hits/misses,
        // tiempo) — obligatoria por la ficha para no aceptar la reducción de ÷10 como
        // hipótesis sin medir. Groups==Chunks en modo PerChunk (una llamada por chunk).
        public int Groups;
        public int LlmCalls;
        public int CacheHits;
        public int CacheMisses;
        public long ElapsedMs;
    }

    // Mutable stats class shared between producer and consumer via Interlocked
    private sealed class PipelineStats
    {
        public int FilesScanned;
        public int ChunksGenerated;
        public int ChunksIndexed;
        public int PointsDeleted;
        public int FilesSkipped;

        // ── 11.1: medición de tokens reales de la fase de embedding ─────────
        // Un solo escritor lógico por lote (ProcessBatchAsync agrega sus propios
        // T antes de soltar el control), pero varios consumidores concurrentes
        // pueden llamar a la vez: se protege con un candado propio, separado de
        // los contadores Interlocked de arriba porque agrega a una lista, no a
        // un entero.
        private readonly object _tokenStatsLock = new();
        private readonly List<int> _tokenCounts = [];
        public long ChunksTruncatedTotal;
        public long TokensDiscardedTotal;
        public int TokensMaxUsable;

        public void RecordTokenizationStats(IReadOnlyList<TokenizationStats> batchStats)
        {
            lock (_tokenStatsLock)
            {
                foreach (var s in batchStats)
                {
                    _tokenCounts.Add(s.TotalTokens);
                    if (s.Truncated) ChunksTruncatedTotal++;
                    TokensDiscardedTotal += s.Discarded;
                    TokensMaxUsable = s.MaxUsableTokens;
                }
            }
        }

        /// <summary>
        /// Percentil nearest-rank (1-indexado, indice=ceil(p*n)) sobre T
        /// ordenados. n=0 no produce percentiles ficticios: retorna null.
        /// </summary>
        public (int N, int? P50, int? P95) ComputeTokenPercentiles()
        {
            lock (_tokenStatsLock)
            {
                return Diagnostics.TokenPercentileCalculator.Compute(_tokenCounts);
            }
        }
    }
}
