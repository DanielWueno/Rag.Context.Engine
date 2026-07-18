Fase 3: Flujo de Datos — Sequence Diagrams

### El Viaje Completo de la Información: De Repositorio a Contexto del LLM

──────

## Dos Flujos Fundamentales del Sistema

El motor opera bajo dos flujos completamente independientes y asincrónicos:

Flujo │ Trigger │ Frecuencia │ Objetivo
───────────────────────────────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────────────
🔵 Ingestion Flow │ Comando manual / scheduler │ Periódico (al actualizar el repo) │ Poblar/actualizar el índice vectorial
🟢 Retrieval Flow │ Consulta del desarrollador / LLM │ En tiempo real (ms) │ Recuperar contexto relevante
──────

## 🔵 Flujo 1: Ingestion Pipeline — Paso a Paso

> 📌 **Actualización (julio 2026):** el flujo descrito abajo sigue vigente con dos evoluciones:
> el lado consumidor son ahora **2–4 tareas concurrentes** (no una), y cada lote vectoriza
> denso + disperso en paralelo antes del upsert dual con `wait:false`. Ver el apartado
> "Gestión de Concurrencia" al final de este documento y `docs/pipeline-de-ingesta.md`.

### Diagrama de Secuencia Completo

    Developer / CI Job
          │
          │  IngestRepositoryAsync(request)
          ▼
    ┌─────────────────────────────────────────────────────────────────┐
    │                     DefaultIngestionPipeline                    │
    │                                                                 │
    │  Step 1: Validate & Prepare                                     │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ • Verificar que rootPath existe                         │    │
    │  │ • ForceReindex? → DELETE colección Qdrant + recrear     │    │
    │  │ • Inicializar IngestionProgress reporter                │    │
    │  └─────────────────────────────────────────────────────────┘    │
    │                           │                                     │
    │  Step 2: Scan             ▼                                     │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ IIngestionScanner.ScanAsync() → IAsyncEnumerable<RawArtifact>│
    │  │ • FileSystemIngestionScanner itera directorios          │    │
    │  │ • Aplica ScanProfile (extensiones, exclusiones)         │    │
    │  │ • Emite RawArtifact uno por uno (streaming)             │    │
    │  └─────────────────────────────────────────────────────────┘    │
    │                           │                                     │
    │  Step 3: Read + Route     ▼                                     │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ Por cada RawArtifact:                                   │    │
    │  │ • File.ReadAllTextAsync(artifact.AbsolutePath)          │    │
    │  │ • ChunkingStrategyRouter.GetStrategy(artifact.Language) │    │
    │  └─────────────────────────────────────────────────────────┘    │
    │                           │                                     │
    │  Step 4: Chunk            ▼                                     │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ strategy.ChunkAsync() → IAsyncEnumerable<CodeChunk>     │    │
    │  │ • Roslyn AST para .cs                                   │    │
    │  │ • XDocument para .xaml                                  │    │
    │  │ • Regex léxico para .ts                                 │    │
    │  │ • Chunks enriquecidos con contexto estructural          │    │
    │  └─────────────────────────────────────────────────────────┘    │
    │                           │                                     │
    │  Step 5: Batch            ▼                                     │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ ChunkBatchAccumulator                                   │    │
    │  │ • Acumula chunks hasta BatchSize (ej: 32)               │    │
    │  │ • O hasta timeout de 500ms (para evitar esperas largas) │    │
    │  └─────────────────────────────────────────────────────────┘    │
    │                           │                                     │
    │  Step 6: Vectorize        ▼                                     │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ IVectorizationBrain.GenerateBatchEmbeddingsAsync()      │    │
    │  │ • Input: batch de chunk.EnrichedContent                 │    │
    │  │ • ONNX Runtime procesa el lote in-process               │    │
    │  │ • Output: List<float[]> — un vector por chunk           │    │
    │  └─────────────────────────────────────────────────────────┘    │
    │                           │                                     │
    │  Step 7: Upsert           ▼                                     │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ QdrantClient.UpsertAsync()                              │    │
    │  │ • PointStruct[] con Id, Vector, Payload                 │    │
    │  │ • Payload = serialización de CodeChunkMetadata          │    │
    │  │ • Operación idempotente (Upsert, no Insert)             │    │
    │  └─────────────────────────────────────────────────────────┘    │
    │                           │                                     │
    │  Step 8: Report           ▼                                     │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ IProgress<IngestionProgress>.Report()                   │    │
    │  │ • CLI: actualiza barra de progreso en consola           │    │
    │  │ • API: emite evento SSE al cliente                      │    │
    │  └─────────────────────────────────────────────────────────┘    │
    └─────────────────────────────────────────────────────────────────┘
          │
          │  return IngestionSummary
          ▼
    Developer / CI Job

### Implementación del Pipeline: DefaultIngestionPipeline

    // RagEngine.Core/Pipeline/DefaultIngestionPipeline.cs

    namespace RagEngine.Core.Pipeline;

    public sealed class DefaultIngestionPipeline : IIngestionPipeline
    {
        private readonly IIngestionScanner _scanner;
        private readonly ChunkingStrategyRouter _chunkRouter;
        private readonly IVectorizationBrain _brain;
        private readonly QdrantClient _qdrant;
        private readonly ILogger<DefaultIngestionPipeline> _logger;

        public async Task<IngestionSummary> IngestRepositoryAsync(
            IngestionRequest request,
            IProgress<IngestionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var stats = new IngestionStats();

            // ── Step 1: Prepare ──────────────────────────────────────────────
            await PrepareCollectionAsync(request, cancellationToken);

            // ── Step 2→8: Pipeline principal ─────────────────────────────────
            // El pipeline usa un Channel<T> como buffer entre
            // el producer (scanner+chunker) y el consumer (vectorizer+indexer).
            // Esto desacopla las velocidades de ambos lados.

            var channel = Channel.CreateBounded<CodeChunk>(new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.Wait,   // Backpressure natural
                SingleReader = false,
                SingleWriter = false
            });

            // PRODUCER: Scan → Chunk → escribir en el channel
            var producerTask = ProduceChunksAsync(
                request, channel.Writer, progress, stats, cancellationToken);

            // CONSUMER: Leer del channel → Vectorizar → Upsert en Qdrant
            var consumerTask = ConsumeAndIndexAsync(
                request.CollectionName, channel.Reader,
                request.Options.BatchSize, stats, cancellationToken);

            // Esperar que ambos terminen
            await Task.WhenAll(producerTask, consumerTask);

            sw.Stop();
            return new IngestionSummary(
                FilesScanned: stats.FilesScanned,
                ChunksGenerated: stats.ChunksGenerated,
                ChunksIndexed: stats.ChunksIndexed,
                FilesSkipped: stats.FilesSkipped,
                TotalDuration: sw.Elapsed,
                EstimatedMemoryPeakBytes: GC.GetTotalMemory(false));
        }

        private async Task ProduceChunksAsync(
            IngestionRequest request,
            ChannelWriter<CodeChunk> writer,
            IProgress<IngestionProgress>? progress,
            IngestionStats stats,
            CancellationToken ct)
        {
            try
            {
                await foreach (var artifact in _scanner.ScanAsync(
                    request.RepositoryPath, request.Profile, ct))
                {
                    Interlocked.Increment(ref stats.FilesScanned);
                    progress?.Report(new(stats.FilesScanned, 0,
                        artifact.RelativePath, IngestionStage.Scanning));

                    string content;
                    try
                    {
                        content = await File.ReadAllTextAsync(artifact.AbsolutePath, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Error leyendo {File}: {Err}", artifact.RelativePath, ex.Message);
                        Interlocked.Increment(ref stats.FilesSkipped);
                        continue;
                    }

                    var strategy = _chunkRouter.GetStrategy(artifact.Language);

                    await foreach (var chunk in strategy.ChunkAsync(
                        artifact, content, request.ChunkingOptions, ct))
                    {
                        Interlocked.Increment(ref stats.ChunksGenerated);
                        await writer.WriteAsync(chunk, ct);  // Backpressure automático
                    }
                }
            }
            finally
            {
                writer.Complete();  // Señalizar fin de producción
            }
        }

        private async Task ConsumeAndIndexAsync(
            string collectionName,
            ChannelReader<CodeChunk> reader,
            int batchSize,
            IngestionStats stats,
            CancellationToken ct)
        {
            var batch = new List<CodeChunk>(batchSize);

            await foreach (var chunk in reader.ReadAllAsync(ct))
            {
                batch.Add(chunk);

                if (batch.Count >= batchSize)
                {
                    await VectorizeAndUpsertBatchAsync(collectionName, batch, stats, ct);
                    batch.Clear();
                }
            }

            // Procesar el último lote incompleto
            if (batch.Count > 0)
                await VectorizeAndUpsertBatchAsync(collectionName, batch, stats, ct);
        }

        private async Task VectorizeAndUpsertBatchAsync(
            string collectionName,
            List<CodeChunk> batch,
            IngestionStats stats,
            CancellationToken ct)
        {
            // Step 6: Vectorizar
            var texts = batch.Select(c => c.EnrichedContent);
            var embeddings = await _brain.GenerateBatchEmbeddingsAsync(texts, ct);

            // Step 7: Construir PointStructs para Qdrant
            var points = batch.Zip(embeddings, (chunk, vector) =>
                new PointStruct
                {
                    Id = new PointId { Uuid = chunk.Id },
                    Vectors = vector,
                    Payload =
                    {
                        ["content"]        = chunk.Content,
                        ["file_path"]      = chunk.Metadata.FilePath,
                        ["language"]       = chunk.Metadata.Language.ToString(),
                        ["namespace"]      = chunk.Metadata.Namespace ?? string.Empty,
                        ["class_name"]     = chunk.Metadata.ClassName ?? string.Empty,
                        ["method_name"]    = chunk.Metadata.MethodName ?? string.Empty,
                        ["start_line"]     = chunk.Metadata.StartLine,
                        ["end_line"]       = chunk.Metadata.EndLine,
                        ["chunk_type"]     = chunk.Type.ToString(),
                        ["last_modified"]  = chunk.Metadata.LastModified.ToString("O"),
                        ["repository"]     = chunk.Metadata.RepositoryName,
                    }
                }).ToList();

            await _qdrant.UpsertAsync(collectionName, points, cancellationToken: ct);
            Interlocked.Add(ref stats.ChunksIndexed, batch.Count);
        }
    }
    ──────

## 🟢 Flujo 2: Retrieval Pipeline — Paso a Paso

    Developer / LLM Client
          │
          │  "¿Cómo funciona la validación de pedidos?"
          ▼
    ┌─────────────────────────────────────────────────────────────────┐
    │                      ISemanticRetriever                         │
    │                                                                 │
    │  Step 1: Query Preprocessing                                    │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ • Normalizar query (trim, lowercase opcional)           │    │
    │  │ • Detectar términos técnicos especiales                 │    │
    │  │   (ej: "OrderService" → boost en filtro por clase)     │    │
    │  └─────────────────────────────────────────────────────────┘    │
    │                           │                                     │
    │  Step 2: Query Vectorization                                    │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ IVectorizationBrain.GenerateEmbeddingAsync(query)       │    │
    │  │ • Mismo modelo que en ingesta (CRÍTICO para coherencia) │    │
    │  │ • Output: float[384] — vector del query                 │    │
    │  └─────────────────────────────────────────────────────────┘    │
    │                           │                                     │
    │  Step 3: Vector Search en Qdrant                                │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ QdrantClient.SearchAsync()                              │    │
    │  │ • Algorithm: HNSW (Hierarchical Navigable Small World)  │    │
    │  │ • Metric: Cosine Similarity                             │    │
    │  │ • Limit: TopK * 3 (si UseReRanking=true)               │    │
    │  │ • Filter: payload conditions (language, namespace)      │    │
    │  │ • ScoreThreshold: MinimumSimilarityScore (0.70)         │    │
    │  │ • Output: ScoredPoint[] con scores y payloads           │    │
    │  └─────────────────────────────────────────────────────────┘    │
    │                           │                                     │
    │  Step 4: [Opcional] Re-Ranking                                  │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ CrossEncoderReRanker                                    │    │
    │  │ • Toma el query + Top 30 candidatos                     │    │
    │  │ • Cross-Encoder evalúa cada par (query, chunk)          │    │
    │  │ • Re-ordena por relevancia real (no solo similitud)     │    │
    │  │ • Retorna Top K final                                   │    │
    │  └─────────────────────────────────────────────────────────┘    │
    │                           │                                     │
    │  Step 5: Context Assembly                                       │
    │  ┌─────────────────────────────────────────────────────────┐    │
    │  │ ContextAssembler                                        │    │
    │  │ • Ordena resultados por: FilePath + StartLine           │    │
    │  │   (presentar el código en orden natural, no por score)  │    │
    │  │ • Formatea cada chunk con su metadata como bloque de    │    │
    │  │   código Markdown                                       │    │
    │  │ • Calcula tokens totales del contexto ensamblado        │    │
    │  │ • Trunca si supera el límite del LLM destino            │    │
    │  └─────────────────────────────────────────────────────────┘    │
    └─────────────────────────────────────────────────────────────────┘
          │
          │  return IReadOnlyList<RetrievalResult>
          ▼
    Developer / LLM Client
    (Contexto listo para inyectar en el prompt)

### El Contexto Ensamblado — Output Final

Este es el artefacto que el LLM recibe. El formato es deliberado para maximizar la comprensión del modelo:

    // RagEngine.Core/Pipeline/ContextAssembler.cs

    namespace RagEngine.Core.Pipeline;

    public sealed class ContextAssembler
    {
        /// <summary>
        /// Convierte los RetrievalResults en un bloque de texto Markdown
        /// optimizado para ser inyectado en el system prompt de un LLM.
        /// </summary>
        public string Assemble(
            IReadOnlyList<RetrievalResult> results,
            string originalQuery,
            int maxContextTokens = 8_000)
        {
            // 1. Ordenar por archivo y línea (no por score) para coherencia narrativa
            var ordered = results
                .OrderBy(r => r.Metadata.FilePath)
                .ThenBy(r => r.Metadata.StartLine)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("# CONTEXTO DE CÓDIGO FUENTE RECUPERADO");
            sb.AppendLine($"# Query original: \"{originalQuery}\"");
            sb.AppendLine($"# Fragmentos recuperados: {ordered.Count}");
            sb.AppendLine();

            int currentTokens = TokenEstimator.Estimate(sb.ToString());

            foreach (var result in ordered)
            {
                var chunkBlock = FormatChunkAsMarkdown(result);
                var chunkTokens = TokenEstimator.Estimate(chunkBlock);

                // Detener si superamos el límite de contexto
                if (currentTokens + chunkTokens > maxContextTokens)
                {
                    sb.AppendLine($"<!-- {ordered.Count - ordered.IndexOf(result)} fragmentos adicionales omitidos por límite de contexto -->");
                    break;
                }

                sb.Append(chunkBlock);
                currentTokens += chunkTokens;
            }

            return sb.ToString();
        }

        private static string FormatChunkAsMarkdown(RetrievalResult result)
        {
            /*
             * Output ejemplo:
             * ════════════════════════════════════════════════════════
             * ## [src/Services/OrderService.cs] — Relevancia: 94.2%
             * **Namespace:** MyCompany.ERP.Services
             * **Clase:** OrderService : IOrderService
             * **Método:** ValidateOrderAsync(Order order)
             * **Líneas:** 87–134
             *
             * ```csharp
             * public async Task<ValidationResult> ValidateOrderAsync(Order order)
             * {
             *     if (order.Items.Count == 0)
             *         return ValidationResult.Fail("El pedido no tiene artículos.");
             *     ...
             * }
             * ```
             * ════════════════════════════════════════════════════════
             */

            var m = result.Metadata;
            var lang = m.Language.ToString().ToLowerInvariant();
            var score = (result.SimilarityScore * 100).ToString("F1");

            var header = new StringBuilder();
            header.AppendLine($"## [{m.FilePath}] — Relevancia: {score}%");

            if (!string.IsNullOrEmpty(m.Namespace))
                header.AppendLine($"**Namespace:** `{m.Namespace}`");
            if (!string.IsNullOrEmpty(m.ClassName))
                header.AppendLine($"**Clase:** `{m.ClassName}`");
            if (!string.IsNullOrEmpty(m.MethodName))
                header.AppendLine($"**Método:** `{m.MethodName}`");

            header.AppendLine($"**Líneas:** {m.StartLine}–{m.EndLine}");
            header.AppendLine();
            header.AppendLine($"```{lang}");
            header.AppendLine(result.Content);
            header.AppendLine("```");
            header.AppendLine();
            header.AppendLine("---");
            header.AppendLine();

            return header.ToString();
        }
    }
    ──────

## Flujo de Estado en Qdrant — Ciclo de Vida de un Chunk

    RawArtifact (disco)
          │
          │ ChunkingStrategy
          ▼
    CodeChunk (en memoria RAM)
          │
          │ IVectorizationBrain
          ▼
    PointStruct { Id, Vector[384], Payload{metadata} }
          │
          │ QdrantClient.UpsertAsync()
          ▼
    ┌─────────────────────────────────────────┐
    │           Qdrant Collection             │
    │                                         │
    │  HNSW Index    │   Payload Store        │
    │  ─────────     │   ────────────         │
    │  Vector[384]   │   content: string      │
    │  (navegable)   │   file_path: string    │
    │                │   namespace: string    │
    │                │   class_name: string   │
    │                │   method_name: string  │
    │                │   start_line: int      │
    │                │   end_line: int        │
    │                │   chunk_type: string   │
    │                │   language: string     │
    │                │   last_modified: iso8601│
    └─────────────────────────────────────────┘
          │
          │ QdrantClient.SearchAsync() → cosine similarity
          ▼
    ScoredPoint[] → RetrievalResult[] → Markdown Context
          │
          │ System Prompt injection
          ▼
    LLM (Claude / GPT-4 / Phi / local Ollama)
    ──────

## Gestión de Concurrencia — El Channel como Backbone

El patrón Channel<T> de System.Threading.Channels es la pieza clave de resiliencia:

    PRODUCER SIDE                          CONSUMER SIDE (×2–4 concurrentes)
    ─────────────                          ──────────────────────────────────
    Scanner (I/O bound)                    Consumer 1..N (Clamp(cores/4, 2, 4))
        │                                       ▲
        │ FileSystemIngestionScanner            │
        ▼                                       │
    Chunker (CPU: Roslyn)         Channel<CodeChunk>
        │                         capacity: 512
        │  [filtro: contenido ≥ 60 chars]       │
        └──────────── Write ────────────────────┘
                                                │
                                         Batch Accumulator (batch=32)
                                                │
                                  ┌─────────────┴─────────────┐
                                  │ Task.WhenAll (en paralelo)│
                                  │  ONNX denso │ Sparse TF   │
                                  └─────────────┬─────────────┘
                                                │
                                  QdrantClient.UpsertAsync(wait:false)

> 📌 **Actualización (julio 2026):** el diseño original usaba **un** consumidor; con el esquema
> dual del Sprint 5, la latencia del upsert (HNSW + índice invertido disperso) entraba íntegra
> a la ruta crítica entre lote y lote. Hoy 2–4 consumidores compiten por el mismo Channel
> (`SingleReader = false`): mientras uno espera el upsert, otro está en inferencia ONNX. Los
> upserts de ingesta masiva usan `wait: false` — Qdrant confirma al persistir en WAL
> (durabilidad garantizada) y aplica los índices asíncronamente. El productor descarta chunks
> con contenido < 60 chars (constructores boilerplate, interfaces marcador), que contaminaban
> el ranking de ambas ramas con embeddings de puro encabezado.

Beneficios de este diseño:

Propiedad │ Descripción
──────────────────────────────────────────────────────────────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────────────────────────────────────
Backpressure natural │ Si Qdrant va lento, el Channel se llena y el producer espera automáticamente. No hay OOM por acumulación.
Desacoplamiento de velocidades │ El scanner de I/O no necesita esperar al ONNX de CPU. Ambos corren a su velocidad óptima.
Cancelación cooperativa │ CancellationToken propagado en cada await . Ctrl+C en CLI detiene todo limpiamente.
Idempotencia │ UpsertAsync en Qdrant permite re-ejecutar el pipeline sin duplicar datos.
──────

## Métricas de Rendimiento — Medidas Reales (Sprint 7)

│ Hardware de referencia: Apple Silicon, Qdrant local en Docker, modelo multilingüe int8 ARM64, batch 32, 2–4 consumidores.

Corpus │ Volumen │ Duración medida │ Cuello de botella
────────────────────────────────────────────────────────────────────┼───────────────────────────────────────────────────────────────────┼───────────────────────────────────────────────────────────────────┼───────────────────────────────────────
RagEngine (este repo) │ 57 archivos → ~430 chunks │ ~4 s │ Inferencia ONNX
BusinessSuite.Xaf │ 2,148 archivos → ~20,000 chunks │ 2m 26s (~137 chunks/seg e2e) │ Inferencia ONNX (~150 ms/lote int8)
Búsqueda híbrida (rag search) │ 1 consulta, colección de 20k puntos │ ~130–150 ms │ Embedding de la consulta + RRF

│ Proyección: un repositorio enterprise de 10,000 archivos C# → ingesta inicial en ~12–15 minutos (las estimaciones originales del POC preveían 50–100 min con un solo consumidor y sin cuantización). Re-ingestas idempotentes; la tokenización dispersa es despreciable (~1 ms/lote, en paralelo con ONNX).
──────

> 📌 **Nota histórica:** la tabla original de este apartado contenía estimaciones pre-POC sobre
> Intel i7 (pipeline completo ~100–200 archivos/seg, 10k archivos ≈ 50–100 min). Se reemplazó
> por mediciones reales tras la paralelización del consumidor y la cuantización int8 del
> Sprint 7. Fuente de los números: logs estructurados (`logs/rag-engine-*.json`, evento
> "Ingestion complete") — ver `docs/pipeline-de-ingesta.md`.
