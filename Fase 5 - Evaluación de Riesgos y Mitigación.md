Fase 5: Evaluación de Riesgos y Mitigación

### Cuellos de Botella Técnicos, Amenazas Arquitectónicas y Planes de Contingencia

──────

## Marco de Evaluación

Cada riesgo se clasifica por dos dimensiones:

Dimensión │ Escala
──────────────────────────────────────────────────────────────────────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────────────────────────────
Probabilidad │ 🔴 Alta / 🟡 Media / 🟢 Baja
Impacto │ 💀 Crítico / ⚠️ Alto / 📌 Medio / 📎 Bajo
──────

## RIESGO 1 — Degradación de Calidad del Embedding por Truncamiento Silencioso

Probabilidad: 🔴 Alta | Impacto: 💀 Crítico

### Descripción del Problema

El modelo all-MiniLM-L6-v2 tiene un límite físico de 512 tokens por secuencia. El tokenizador de FastBertTokenizer trunca silenciosamente cualquier texto más largo sin lanzar excepción. Si un método
C# + su EnrichedContent (contexto inyectado) supera ese límite, el vector resultante no representará el método completo, sino solo su primera mitad. El LLM recibirá código fragmentado sin saberlo.

### Síntoma Observable

    // Método original: 800 tokens
    public async Task<Order> ProcessComplexOrderAsync(OrderRequest request)
    {
        // ... 150 líneas de lógica de negocio ...
        // Las últimas 60 líneas son SILENCIOSAMENTE ignoradas por el tokenizador
        // El vector "cree" que el método termina en la línea 90
    }

### Mitigación: Token Budget con Guard Clause

    // RagEngine.Core/Infrastructure/Chunking/TokenBudgetGuard.cs

    public sealed class TokenBudgetGuard
    {
        private readonly int _modelMaxTokens;
        private readonly int _contextHeaderReserve;

        // modelMaxTokens = 512 para MiniLM
        // contextHeaderReserve = ~80 tokens para el header de contexto inyectado
        public TokenBudgetGuard(int modelMaxTokens = 512, int contextHeaderReserve = 80)
        {
            _modelMaxTokens = modelMaxTokens;
            _contextHeaderReserve = contextHeaderReserve;
        }

        /// <summary>
        /// Presupuesto real disponible para el contenido del chunk.
        /// </summary>
        public int AvailableContentTokens => _modelMaxTokens - _contextHeaderReserve;

        /// <summary>
        /// Valida que el chunk no exceda el límite ANTES de vectorizar.
        /// Lanza si el chunker no lo manejó correctamente.
        /// </summary>
        public void ValidateOrThrow(CodeChunk chunk)
        {
            var tokenCount = TokenEstimator.Estimate(chunk.EnrichedContent);
            if (tokenCount > _modelMaxTokens)
            {
                throw new ChunkTokenBudgetExceededException(
                    $"Chunk '{chunk.Id}' excede el límite: {tokenCount} > {_modelMaxTokens} tokens. " +
                    $"Archivo: {chunk.Metadata.FilePath}, L{chunk.Metadata.StartLine}");
            }
        }

        /// <summary>
        /// Versión segura: trunca con sufijo explicativo en lugar de lanzar.
        /// </summary>
        public string SafeTruncate(string enrichedContent)
        {
            var estimated = TokenEstimator.Estimate(enrichedContent);
            if (estimated <= _modelMaxTokens) return enrichedContent;

            var charLimit = (int)(_modelMaxTokens * 3.5); // Chars estimados
            return enrichedContent[..charLimit] +
                   "\n// [TRUNCADO: fragmento excede límite del modelo]";
        }
    }

    // En DefaultIngestionPipeline.ConsumeAndIndexAsync — añadir validación:

    private async Task VectorizeAndUpsertBatchAsync(...)
    {
        // GUARDIA: validar budget antes de vectorizar
        foreach (var chunk in batch)
        {
            chunk = chunk with
            {
                EnrichedContent = _tokenGuard.SafeTruncate(chunk.EnrichedContent)
            };
        }

        var embeddings = await _brain.GenerateBatchEmbeddingsAsync(
            batch.Select(c => c.EnrichedContent), ct);
        // ...
    }

Indicador de éxito: Log de advertencia [WARN] Chunk truncado nunca supera el 2% del total de chunks.
──────

## RIESGO 2 — Consumo de Memoria RAM Incontrolado Durante Ingesta Masiva

Probabilidad: 🔴 Alta | Impacto: ⚠️ Alto

### Descripción del Problema

Un repositorio enterprise de 15,000 archivos con chunking Roslyn puede generar 300,000–500,000 chunks. Si el Channel<CodeChunk> no tiene backpressure adecuado, o si el consumer (ONNX) va más lento que
el producer (scanner), el buffer del channel crecerá hasta consumir toda la RAM disponible.

Adicionalmente, Roslyn carga el AST completo de cada archivo en memoria. Para archivos .cs grandes (controllers monolíticos, archivos generados), el AST puede pesar 10–50x el tamaño del archivo en
texto.

### Mitigación: Backpressure por Diseño + Límites Explícitos

    // En DefaultIngestionPipeline — configuración del Channel con límite estricto:

    var channel = Channel.CreateBounded<CodeChunk>(new BoundedChannelOptions(capacity: 512)
    {
        // Con FullMode.Wait, el PRODUCER se bloquea automáticamente
        // cuando el buffer está lleno. Esto es backpressure nativo.
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = true
    });

    // Gestión de memoria del AST de Roslyn — liberar explícitamente:

    private async IAsyncEnumerable<CodeChunk> ChunkFileWithMemoryControlAsync(
        RawArtifact artifact, string content, ChunkingOptions opts,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // El SyntaxTree de Roslyn vive en un scope controlado
        // usando un método separado para que el GC pueda recolectarlo
        // tan pronto como terminamos con el archivo.
        var chunks = await Task.Run(() =>
        {
            var tree = CSharpSyntaxTree.ParseText(content, cancellationToken: ct);
            var root = tree.GetRoot(ct);
            var result = ExtractChunksFromRoot(root, artifact, opts).ToList();

            // Ayudar al GC: el SyntaxTree es grande y no escapará del scope
            root = null!;
            GC.Collect(0, GCCollectionMode.Optimized, blocking: false);

            return result;
        }, ct);

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            yield return chunk;
        }
    }

    // Monitor de memoria en el pipeline — alerta si se supera el umbral:

    public sealed class MemoryPressureMonitor
    {
        private const long WarningThresholdBytes = 4L * 1024 * 1024 * 1024; // 4 GB

        public void CheckAndWarnIfNeeded(ILogger logger)
        {
            var currentMemory = GC.GetTotalMemory(false);
            if (currentMemory > WarningThresholdBytes)
            {
                logger.LogWarning(
                    "Presión de memoria alta durante ingesta: {MemoryGB:F2} GB. " +
                    "Considera reducir --batch-size o procesar en subconjuntos.",
                    currentMemory / (1024.0 * 1024 * 1024));

                // Forzar recolección full si estamos al borde
                if (currentMemory > WarningThresholdBytes * 1.5)
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
            }
        }
    }

Indicador de éxito: Ingesta de 15,000 archivos con consumo de RAM estabilizado por debajo de 3 GB.
──────

## RIESGO 3 — Deriva Semántica por Inconsistencia de Modelos

Probabilidad: 🟡 Media | Impacto: 💀 Crítico

### Descripción del Problema

Si el equipo indexa los chunks con all-MiniLM-L6-v2 v1.0 y luego actualiza el modelo a v2.0 (o cambia a bge-small-en-v1.5 ), los vectores ya almacenados en Qdrant son matemáticamente incompatibles
con los nuevos. Las búsquedas devolverán basura o resultados con scores anómalamente bajos sin ningún error visible.

### Mitigación: Versionado de Modelo en Metadata de Colección

    // RagEngine.Core/Domain/CollectionManifest.cs

    /// <summary>
    /// Metadata que se almacena en el payload de un punto especial en Qdrant
    /// (punto con Id = "00000000-0000-0000-0000-000000000001")
    /// que actúa como "manifiesto" de la colección.
    /// </summary>
    public sealed record CollectionManifest
    {
        public required string ModelName { get; init; }          // "all-MiniLM-L6-v2"
        public required string ModelVersion { get; init; }       // Hash del .onnx
        public required int EmbeddingDimensions { get; init; }   // 384
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset LastIngestedAt { get; init; }
        public required string RepositoryPath { get; init; }
        public required string ChunkingStrategyVersion { get; init; } // "2.1.0" (semver del core)
    }

    // En PrepareCollectionAsync — validar compatibilidad antes de indexar:

    private async Task ValidateModelCompatibilityAsync(
        string collectionName, CancellationToken ct)
    {
        var manifest = await LoadManifestAsync(collectionName, ct);
        if (manifest is null) return; // Colección nueva, sin conflicto

        var currentModelHash = ComputeModelHash(_options.ModelPath);

        if (manifest.ModelVersion != currentModelHash)
        {
            throw new ModelVersionMismatchException(
                $"La colección '{collectionName}' fue indexada con el modelo " +
                $"'{manifest.ModelName}' (hash: {manifest.ModelVersion[..8]}...). " +
                $"El modelo actual tiene hash '{currentModelHash[..8]}...'. " +
                $"Los vectores son INCOMPATIBLES. Usa --force para re-indexar completamente.");
        }
    }

    private static string ComputeModelHash(string modelPath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(modelPath);
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }

Indicador de éxito: Intentar buscar en una colección con modelo cambiado lanza ModelVersionMismatchException con mensaje accionable, nunca devuelve resultados silenciosamente incorrectos.
──────

## RIESGO 4 — Latencia Inaceptable en ONNX en Hardware Limitado

Probabilidad: 🟡 Media | Impacto: ⚠️ Alto

### Descripción del Problema

En laptops sin AVX2 o con CPU de baja generación, la inferencia ONNX para un batch de 32 chunks puede tomar 2–5 segundos. En una ingesta de 300,000 chunks, esto representa 5–8 horas de tiempo total,
inaceptable para uso diario.

### Mitigación: Estrategia de Ejecución Adaptativa

> 📌 **Estado (julio 2026) — mitigado y verificado por otra vía:** antes que detectar GPUs, las
> dos palancas que resultaron decisivas en CPU fueron (a) la **cuantización int8**
> (`model_qint8_arm64.onnx`: 2.3× más rápido que fp32 con coseno ES↔EN 0.91 vs 0.92) y
> (b) el **padding dinámico por lote** (rellenar hasta la secuencia más larga real, no hasta
> `MaxSequenceLength` fijo). Medido: 21k chunks en 2m26s con un modelo de 12 capas. El
> detector adaptativo de abajo sigue siendo válido como evolución futura para GPU.

    // RagEngine.Core/Infrastructure/OnnxCapabilityDetector.cs

    public static class OnnxCapabilityDetector
    {
        public static OnnxExecutionProfile DetectOptimalProfile()
        {
            // 1. ¿Hay GPU CUDA disponible?
            if (IsCudaAvailable())
                return OnnxExecutionProfile.CudaGpu;

            // 2. ¿Hay GPU DirectML (Windows, AMD/Intel/NVIDIA)?
            if (IsDirectMlAvailable())
                return OnnxExecutionProfile.DirectMlGpu;

            // 3. CPU: ¿soporta AVX-512?
            if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported)
                return OnnxExecutionProfile.CpuAvx512;

            // 4. CPU: ¿soporta AVX2?
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                return OnnxExecutionProfile.CpuAvx2;

            // 5. Fallback: CPU genérico
            return OnnxExecutionProfile.CpuGeneric;
        }

        private static bool IsCudaAvailable()
        {
            try
            {
                // Intentar crear una sesión con CUDA provider
                var opts = new SessionOptions();
                opts.AppendExecutionProvider_CUDA(0);
                return true;
            }
            catch { return false; }
        }
    }

    public enum OnnxExecutionProfile
    {
        CudaGpu,        // Throughput: ~5,000 chunks/seg
        DirectMlGpu,    // Throughput: ~2,000 chunks/seg
        CpuAvx512,      // Throughput: ~600 chunks/seg
        CpuAvx2,        // Throughput: ~400 chunks/seg
        CpuGeneric      // Throughput: ~150 chunks/seg
    }

    // Configuración dinámica del SessionOptions según el perfil detectado:

    public static SessionOptions BuildSessionOptions(OnnxExecutionProfile profile)
    {
        var opts = new SessionOptions();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        switch (profile)
        {
            case OnnxExecutionProfile.CudaGpu:
                opts.AppendExecutionProvider_CUDA(0);
                break;

            case OnnxExecutionProfile.DirectMlGpu:
                opts.AppendExecutionProvider_DML(0);
                break;

            case OnnxExecutionProfile.CpuAvx512:
            case OnnxExecutionProfile.CpuAvx2:
                opts.EnableCpuMemArena = true;
                opts.InterOpNumThreads = Environment.ProcessorCount;
                opts.IntraOpNumThreads = Environment.ProcessorCount;
                break;

            case OnnxExecutionProfile.CpuGeneric:
                opts.EnableCpuMemArena = false;
                opts.InterOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
                break;
        }

        return opts;
    }

Plan de contingencia adicional: Si el hardware del desarrollador es muy limitado, el comando rag ingest aceptará --remote-embed http://embed-server:8080 para delegar la vectorización a un servidor
remoto con GPU, manteniendo el resto del pipeline local. El IVectorizationBrain tiene implementación HttpRemoteVectorizationBrain como alternativa.
──────

## RIESGO 5 — Colisión de IDs de Chunks en Re-indexaciones

Probabilidad: 🟡 Media | Impacto: 📌 Medio

### Descripción del Problema

Si el ID de un chunk se genera basado en FilePath + StartLine y un desarrollador refactoriza un archivo moviendo métodos, todos los IDs cambian. En una re-indexación con --force=false (incremental),
Qdrant terminará con duplicados: los chunks viejos (con IDs del hash anterior) más los nuevos (con IDs recalculados).

### Mitigación: ID Determinístico Basado en Contenido Semántico

    // RagEngine.Core/Infrastructure/ChunkIdGenerator.cs

    public static class ChunkIdGenerator
    {
        /// <summary>
        /// Genera un UUID v5 (determinístico, basado en namespace + contenido).
        /// El mismo chunk siempre produce el mismo ID, independientemente de su
        /// posición en el archivo. Permite Upsert idempotente sin duplicados.
        /// </summary>
        public static string Generate(string repositoryName, string relativePath, string content)
        {
            // UUID v5 usa SHA-1 internamente; es determinístico para la misma entrada
            var namespaceGuid = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"); // UUID DNS namespace
            var input = $"{repositoryName}::{relativePath}::{content.GetHashCode():X8}";
            return GuidV5.Create(namespaceGuid, input).ToString();
        }
    }

    // Para limpieza de chunks huérfanos en re-indexación incremental:

    public sealed class OrphanChunkCleaner
    {
        /// <summary>
        /// Elimina de Qdrant los chunks que pertenecen a un archivo
        /// que ya no existe en el repositorio (archivos borrados o renombrados).
        /// </summary>
        public async Task CleanOrphansAsync(
            string collectionName,
            IReadOnlySet<string> currentFilePaths,
            CancellationToken ct)
        {
            // Obtener todos los file_path únicos almacenados en Qdrant
            var indexedPaths = await GetIndexedFilePathsAsync(collectionName, ct);

            var orphanPaths = indexedPaths.Except(currentFilePaths).ToList();

            if (!orphanPaths.Any()) return;

            // Eliminar por filtro de payload — operación bulk eficiente
            foreach (var orphanPath in orphanPaths)
            {
                await _qdrant.DeleteAsync(collectionName,
                    filter: new Filter
                    {
                        Must =
                        {
                            new Condition
                            {
                                Field = new FieldCondition
                                {
                                    Key = "file_path",
                                    Match = new Match { Text = orphanPath }
                                }
                            }
                        }
                    }, cancellationToken: ct);
            }
        }
    }
    ──────

## RIESGO 6 — Baja Precisión para Queries Altamente Técnicos

Probabilidad: 🟡 Media | Impacto: ⚠️ Alto

### Descripción del Problema

Los modelos de sentence-transformers como all-MiniLM-L6-v2 están entrenados principalmente con texto en prosa. Para queries muy específicos del dominio técnico (ej: "IUnitOfWork pattern
SaveChangesAsync" ) o para código con acrónimos de empresa (ej: "ERPv3 FiscalYearClosingJob" ), la similitud semántica puede ser baja aunque el código exista en el índice.

### Mitigación: Estrategia de Búsqueda Híbrida (Dense + Sparse)

> 📌 **Estado (julio 2026) — implementado (Sprint 5) y refinado (Sprint 7).** La híbrida
> funcionó, pero la implementación real enseñó dos lecciones que este análisis no anticipó:
>
> 1. **Los scores RRF no son umbralizables como similitudes.** `Σ 1/(k + rank)` es función del
>    ranking (tope ~0.5): el vecino #1 de una consulta absurda puntúa igual que el de una
>    perfecta. Cualquier control de calidad por umbral debe aplicarse *dentro* de la rama
>    densa (`ScoreThreshold` del prefetch), nunca sobre el score fusionado.
> 2. **El TF disperso necesita saturación.** El peso proporcional (`count/totalTerms`)
>    convertía a los micro-chunks en imanes del ranking; la forma BM25 `tf/(tf+1)` mide
>    presencia sin castigar la longitud del documento.
>
> Además, para corpus con identificadores en español la híbrida solo rinde con
> **normalización léxica simétrica** (folding de acentos + stemming ligero ES/EN) antes del
> hash — ver RIESGO 8 y `docs/busqueda-hibrida.md`.

    // RagEngine.Core/Abstractions/IHybridRetriever.cs

    /// <summary>
    /// Combina búsqueda vectorial densa (semántica) con búsqueda BM25 dispersa
    /// (keyword-based) usando Reciprocal Rank Fusion para el ranking final.
    /// Qdrant soporta vectores dispersos nativamente desde v1.7.
    /// </summary>
    public interface IHybridRetriever
    {
        Task<IReadOnlyList<RetrievalResult>> HybridSearchAsync(
            string query,
            RetrievalOptions options,
            float denseWeight = 0.7f,   // 70% semántico
            float sparseWeight = 0.3f,  // 30% keyword
            CancellationToken cancellationToken = default);
    }

    // Implementación con Qdrant sparse vectors (BM25):

    public async Task<IReadOnlyList<RetrievalResult>> HybridSearchAsync(
        string query, RetrievalOptions options,
        float denseWeight, float sparseWeight, CancellationToken ct)
    {
        // 1. Vectorización densa (semántica)
        var denseVector = await _brain.GenerateEmbeddingAsync(query, ct);

        // 2. Vectorización dispersa (BM25 / TF-IDF)
        var sparseVector = _bm25Encoder.Encode(query); // Diccionario token_id → peso

        // 3. Búsqueda en Qdrant con ambos vectores simultáneamente
        var results = await _qdrant.QueryAsync(
            collectionName: options.CollectionName,
            prefetch: new List<PrefetchQuery>
            {
                // Búsqueda densa
                new() { Query = denseVector, Using = "dense", Limit = 20 },
                // Búsqueda dispersa
                new() { Query = new SparseVector(sparseVector), Using = "sparse", Limit = 20 }
            },
            // Fusión por Reciprocal Rank Fusion
            query: new FusionQuery { Fusion = Fusion.Rrf },
            limit: (ulong)options.TopK,
            withPayload: true,
            cancellationToken: ct);

        return results.Select(MapToRetrievalResult).ToList().AsReadOnly();
    }

Cuándo activarlo: Disponible via flag --hybrid en el comando rag search . Recomendado cuando --min-score produce 0 resultados con búsqueda densa pura.
──────

## RIESGO 7 — Ruptura del Pipeline por Archivos Malformados

Probabilidad: 🔴 Alta | Impacto: 📌 Medio

### Descripción del Problema

Los repositorios reales contienen:

• Archivos .cs con errores de compilación (Roslyn puede parsearlos, pero el AST estará incompleto)
• Archivos con encoding inesperado (UTF-16, Latin-1)
• Archivos binarios con extensión .cs (sí existe en repos legacy)
• Archivos de 0 bytes
• Archivos .xaml con XML malformado

Un solo archivo problemático NO debe detener la ingesta de los 14,999 restantes.

### Mitigación: Pipeline con Error Isolation por Artefacto

    // En ProduceChunksAsync — aislamiento de errores por archivo:

    private async Task ProduceChunksAsync(...)
    {
        await foreach (var artifact in _scanner.ScanAsync(...))
        {
            try
            {
                // Detectar encoding antes de leer
                var encoding = DetectEncoding(artifact.AbsolutePath);
                var content  = await File.ReadAllTextAsync(
                    artifact.AbsolutePath, encoding, ct);

                // Validaciones de sanidad
                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogDebug("Archivo vacío ignorado: {File}", artifact.RelativePath);
                    continue;
                }

                if (IsBinaryContent(content))
                {
                    _logger.LogWarning("Archivo binario con extensión de texto ignorado: {File}",
                        artifact.RelativePath);
                    Interlocked.Increment(ref stats.FilesSkipped);
                    continue;
                }

                var strategy = _chunkRouter.GetStrategy(artifact.Language);

                await foreach (var chunk in strategy.ChunkAsync(artifact, content, opts, ct))
                    await channel.Writer.WriteAsync(chunk, ct);
            }
            catch (OperationCanceledException)
            {
                throw; // Cancelación cooperativa: siempre propagar
            }
            catch (Exception ex)
            {
                // Cualquier otro error: loguear y continuar
                _logger.LogError(ex,
                    "Error procesando '{File}'. El archivo será omitido.",
                    artifact.RelativePath);
                Interlocked.Increment(ref stats.FilesSkipped);
            }
        }
    }

    private static bool IsBinaryContent(string content)
    {
        // Heurística: si más del 10% de los primeros 1024 chars son NULL → binario
        var sample = content.Length > 1024 ? content[..1024] : content;
        var nullCount = sample.Count(c => c == '\0');
        return (double)nullCount / sample.Length > 0.10;
    }
    ──────

## RIESGO 8 — Asimetría de Idioma entre Consulta y Corpus

Probabilidad: 🔴 Alta (materializado) | Impacto: ⚠️ Alto

### Descripción del Problema

Riesgo **materializado en producción** antes de ser catalogado — se documenta con su autopsia.
Con un corpus de dominio en español (clases `Auditoria`, `Hallazgo`) las consultas en español
devolvían "0 resultados" mientras su traducción al inglés recuperaba resultados precisos.

### Síntoma Observable

`rag ask` responde sistemáticamente "I cannot find enough information..." para consultas en un
idioma, mientras la misma intención traducida funciona. El retrieval devuelve chunks (los logs
muestran Count > 0), pero irrelevantes.

### Causa Raíz (cuatro capas alineadas)

1. **Modelo denso monolingüe:** all-MiniLM-L6-v2 (entrenado en inglés) producía embeddings
   cuasi-aleatorios para consultas en español → vecinos irrelevantes.
2. **Matching disperso sin morfología:** hash exacto sobre términos sin normalizar →
   `auditoría` ≠ `auditoria` ≠ `auditorias` (acentos y plurales rompían el recall léxico).
3. **Umbral fantasma:** `min-score` era un no-op tras la migración a RRF, ocultando que el
   problema era de relevancia y no de filtrado.
4. **Chunks vacíos:** el bug de `BuildClassHeaderChunk` entregaba cáscaras al LLM, que
   respondía honestamente que no encontraba información.

### Mitigación: Simetría Multilingüe en Ambas Ramas (implementada, Sprint 7)

- Rama densa: modelo multilingüe (`paraphrase-multilingual-MiniLM-L12-v2`) — 50+ idiomas en
  el mismo espacio vectorial de 384 dims.
- Rama dispersa: normalización léxica **idéntica en ingesta y consulta** — folding de acentos
  + stemming ligero ES/EN (`auditoría`/`auditorias` → `auditori`).
- Verificación de regresión: coseno entre pares de consultas equivalentes ES↔EN debe
  mantenerse > 0.85 (medido: 0.92/0.89). Si se cambia de modelo denso, repetir la medición
  y recalibrar `min-score` (ver `docs/configuracion.md`).

──────

## Matriz de Riesgos Consolidada

# │ Riesgo │ Prob. │ Impacto │ Estrategia │ Sprint │ Estado

─────────────────────────────┼───────────────────────────────────────┼─────────────────────────────┼─────────────────────────────┼────────────────────────────────────────────┼─────────────────────────────┼─────────────────────────────
R1 │ Truncamiento silencioso de tokens │ 🔴 Alta │ 💀 Crítico │ TokenBudgetGuard + SafeTruncate │ S1 │ Mitigado
R2 │ OOM en ingesta masiva │ 🔴 Alta │ ⚠️ Alto │ BoundedChannel + GC explícito │ S1 │ Mitigado
R3 │ Deriva semántica por cambio de modelo │ 🟡 Media │ 💀 Crítico │ CollectionManifest + hash validation │ S1 │ Parcial (manifest sin validación activa; regla operativa: re-ingestar al cambiar vectorización)
R4 │ Latencia ONNX en hardware bajo │ 🟡 Media │ ⚠️ Alto │ Cuantización int8 + padding dinámico (detector GPU: futuro) │ S0/S7 │ Mitigado y medido
R5 │ Duplicados por re-indexación │ 🟡 Media │ 📌 Medio │ UUID v5 + OrphanChunkCleaner │ S3 │ Mitigado
R6 │ Baja precisión en queries técnicos │ 🟡 Media │ ⚠️ Alto │ Búsqueda híbrida dense+sparse (RRF) + TF saturado │ S5/S7 │ Mitigado y refinado
R7 │ Ruptura por archivos malformados │ 🔴 Alta │ 📌 Medio │ Error isolation por artefacto │ S1 │ Mitigado
R8 │ Asimetría de idioma consulta↔corpus │ 🔴 Alta │ ⚠️ Alto │ Modelo multilingüe + normalización léxica simétrica │ S7 │ Materializado → corregido y verificado
──────

## Lista de Verificación Pre-Producción (Checklist)

Antes de escalar a la Fase 2 (Minimal API / Producción):

    SEGURIDAD Y DATOS
      ✅ CollectionManifest implementado y validado en cada arranque
      ✅ No se almacena ninguna clave API ni secreto en el payload de Qdrant
      ✅ El modelo ONNX y el tokenizador son archivos locales (sin red en runtime)
      ✅ Qdrant corre en red Docker interna, sin exposición de puerto al exterior

    RESILIENCIA
      ✅ TokenBudgetGuard activo en el pipeline de vectorización
      ✅ BoundedChannel con backpressure en el ingestion pipeline
      ✅ Error isolation: un archivo malformado no detiene la ingesta
      ✅ OrphanChunkCleaner ejecutado en cada re-indexación incremental
      ✅ Polly retry+circuit breaker en todas las llamadas a QdrantClient

    OBSERVABILIDAD
      ✅ Serilog con structured JSON sink configurado
      ✅ Métricas de throughput (chunks/seg) emitidas en cada lote
      ✅ rag doctor valida todas las dependencias al arrancar
      ✅ IngestionSummary con FilesSkipped muestra alerta si > 5%

    RENDIMIENTO
      ✅ OnnxCapabilityDetector selecciona el execution provider óptimo
      ✅ BatchSize configurable en appsettings.json
      ✅ Benchmark baseline documentado: N archivos / M chunks / T minutos

    CALIDAD DE RECUPERACIÓN
      ✅ MinimumSimilarityScore calibrado con al menos 20 queries de prueba
      ✅ Modo híbrido (dense + sparse) disponible como flag opt-in
      ✅ Re-ranking Cross-Encoder disponible para queries críticos
      ✅ ContextAssembler respeta el límite de tokens del LLM destino
    ──────

## Resumen Ejecutivo — Los 3 Riesgos No Negociables

│ De los siete riesgos identificados, tres tienen consecuencias silenciosas e invisibles que hacen que el sistema parezca funcionar correctamente mientras entrega resultados incorrectos. Son los más
│ peligrosos:

    ┌─────────────────────────────────────────────────────────────────┐
    │  🔴 R1 Truncamiento silencioso → Vectores incompletos           │
    │     El sistema indexa "la mitad" de los métodos sin advertir.   │
    │     Implementar TokenBudgetGuard en Sprint 1. No es opcional.   │
    │                                                                 │
    │  🔴 R3 Deriva de modelo → Resultados de búsqueda corruptos      │
    │     Cambiar el modelo sin --force produce scores sin sentido.   │
    │     CollectionManifest con hash validation. No es opcional.     │
    │                                                                 │
    │  🔴 R7 Archivos malformados → Pipeline detenido silenciosamente │
    │     Un XML inválido puede matar la ingesta completa si no hay   │
    │     error isolation. Try/catch por artefacto. No es opcional.   │
    └─────────────────────────────────────────────────────────────────┘
    ──────

## ✅ Diseño Arquitectónico Completo — Índice Final

Fase │ Contenido
──────────────────────────────────────────────────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
Fase 1 │ Interfaces principales: IIngestionScanner , IVectorizationBrain , ISemanticRetriever , IIngestionPipeline + DI
Fase 2 │ Chunking con Roslyn AST (C#), XDocument (XAML), Regex léxico (TypeScript) + Strategy Router
Fase 3 │ Flujo de ingesta con Channel<T> + flujo de retrieval + ensamblado de contexto Markdown
Fase 4 │ 5 sprints CLI: Scaffolding → Ingesta E2E → Búsqueda → Multi-lang → SK Plugin → Hardening
Fase 5 │ 7 riesgos con mitigaciones en código: truncamiento, OOM, deriva de modelo, latencia, duplicados, precisión, resiliencia

El diseño está listo para comenzar el Sprint 0. El primer comando ejecutable será:

    rag ingest ./MiRepositorio --collection poc-test
