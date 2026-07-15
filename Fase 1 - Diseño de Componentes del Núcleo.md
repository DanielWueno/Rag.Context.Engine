Fase 1: Diseño de Componentes del Núcleo

### Universal Local RAG Context Engine — Arquitectura Base

──────

## Visión General del Núcleo

El núcleo del sistema es un Class Library ( RagEngine.Core ) diseñado bajo los principios de Clean Architecture y Dependency Inversion. Ningún componente concreto depende de otro concreto; todos dependen
de abstracciones. Esto garantiza que el mismo núcleo funcione tanto en la CLI (Fase 1) como en la Minimal API (Fase 2) sin modificación alguna.

    RagEngine.Core/
    ├── Abstractions/          ← Contratos (interfaces puras)
    ├── Domain/                ← Entidades de dominio
    ├── Pipeline/              ← Orquestación de flujos
    ├── Infrastructure/        ← Implementaciones concretas (ONNX, Qdrant)
    └── Extensions/            ← DI Registration helpers
    ──────

## Componente 1: IIngestionScanner — El Explorador de Repositorios

Responsabilidad única: Descubrir, filtrar y leer archivos del sistema de archivos local, emitiendo un flujo de artefactos crudos (raw artifacts) de forma diferida.

### Contrato (Abstracción)

    // RagEngine.Core/Abstractions/IIngestionScanner.cs

    namespace RagEngine.Core.Abstractions;

    /// <summary>
    /// Descubre y emite artefactos de código fuente desde un directorio raíz.
    /// El uso de IAsyncEnumerable garantiza consumo diferido (streaming),
    /// evitando cargar el repositorio completo en memoria.
    /// </summary>
    public interface IIngestionScanner
    {
        /// <param name="rootPath">Ruta raíz del repositorio a analizar.</param>
        /// <param name="profile">Perfil de escaneo que define filtros y extensiones.</param>
        /// <param name="cancellationToken">Token para cancelación cooperativa.</param>
        IAsyncEnumerable<RawArtifact> ScanAsync(
            string rootPath,
            ScanProfile profile,
            CancellationToken cancellationToken = default);
    }

### Entidades de Dominio Asociadas

    // RagEngine.Core/Domain/RawArtifact.cs

    namespace RagEngine.Core.Domain;

    /// <summary>
    /// Representa un archivo de código fuente descubierto, sin procesar aún.
    /// Es un record inmutable para garantizar thread-safety en el pipeline.
    /// </summary>
    public sealed record RawArtifact(
        string AbsolutePath,
        string RelativePath,
        SourceLanguage Language,
        DateTimeOffset LastModified,
        long SizeBytes
    );

    public enum SourceLanguage
    {
        CSharp,       // .cs
        TypeScript,   // .ts, .tsx
        Xaml,         // .xaml
        Sql,          // .sql
        Markdown,     // .md
        PlainText,    // .txt, .config, .json, .xml
        Unknown
    }

    // RagEngine.Core/Domain/ScanProfile.cs

    namespace RagEngine.Core.Domain;

    /// <summary>
    /// Perfil de configuración para el scanner. Permite al usuario
    /// definir qué incluir y qué ignorar, similar a un .gitignore.
    /// </summary>
    public sealed record ScanProfile
    {
        /// <summary>Extensiones permitidas (ej: ".cs", ".ts").</summary>
        public required IReadOnlySet<string> AllowedExtensions { get; init; }

        /// <summary>Patrones glob de directorios a excluir.</summary>
        public required IReadOnlyList<string> ExcludePatterns { get; init; }

        /// <summary>Tamaño máximo de archivo en bytes. Default: 512KB.</summary>
        public long MaxFileSizeBytes { get; init; } = 524_288;

        /// <summary>Perfil preconfigurado para soluciones .NET empresariales.</summary>
        public static ScanProfile DotNetEnterprise => new()
        {
            AllowedExtensions = new HashSet<string>
            {
                ".cs", ".ts", ".tsx", ".xaml", ".sql",
                ".md", ".json", ".xml", ".csproj", ".sln"
            },
            ExcludePatterns = new[]
            {
                "**/bin/**", "**/obj/**", "**/node_modules/**",
                "**/.git/**", "**/packages/**", "**/.vs/**",
                "**/migrations/**"        // Excluir migraciones EF generadas
            }
        };
    }

### Decisiones de Diseño Clave

Decisión │ Justificación
───────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
IAsyncEnumerable<T> en lugar de Task<List<T>> │ Un repositorio enterprise puede tener 50K+ archivos. El streaming evita picos de memoria al inicio del pipeline. El consumidor procesa cada artefacto
│ a medida que llega.
record inmutable para RawArtifact │ El pipeline es concurrente. Los records son inherentemente thread-safe y permiten igualdad por valor, útil para deduplicación.
ScanProfile separado de la interfaz │ El scanner es agnóstico a la lógica de negocio. Cambiar qué extensiones procesar no requiere recompilar ni reemplazar el scanner.
──────

## Componente 2: IVectorizationBrain — El Motor de Embeddings

Responsabilidad única: Transformar texto (chunks de código/documentación) en vectores de alta dimensión ( float[] ) utilizando un modelo de lenguaje local, ejecutado in-process vía ONNX Runtime. Cero
llamadas de red.

### Contrato (Abstracción)

    // RagEngine.Core/Abstractions/IVectorizationBrain.cs

    namespace RagEngine.Core.Abstractions;

    /// <summary>
    /// Convierte fragmentos de texto en embeddings vectoriales usando
    /// un modelo local. Implementación concreta usa ONNX Runtime.
    /// </summary>
    public interface IVectorizationBrain
    {
        /// <summary>
        /// Dimensionalidad del espacio vectorial del modelo cargado.
        /// (ej: 384 para all-MiniLM-L6-v2).
        /// Necesario para la configuración de la colección en Qdrant.
        /// </summary>
        int EmbeddingDimensions { get; }

        /// <summary>
        /// Vectoriza un único fragmento de texto.
        /// </summary>
        Task<float[]> GenerateEmbeddingAsync(
            string text,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Vectoriza un lote de fragmentos. Mucho más eficiente
        /// que llamadas individuales por el batching interno de ONNX.
        /// </summary>
        Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
            IEnumerable<string> texts,
            CancellationToken cancellationToken = default);
    }

### Implementación Concreta: OnnxVectorizationBrain

    // RagEngine.Core/Infrastructure/OnnxVectorizationBrain.cs

    namespace RagEngine.Core.Infrastructure;

    using Microsoft.ML.OnnxRuntime;
    using Microsoft.ML.OnnxRuntime.Tensors;

    /// <summary>
    /// Implementación usando ONNX Runtime con modelo Hugging Face local.
    /// La sesión de ONNX es costosa de crear: se instancia UNA vez y se reutiliza.
    /// Implementa IDisposable para liberar recursos nativos correctamente.
    /// </summary>
    public sealed class OnnxVectorizationBrain : IVectorizationBrain, IDisposable
    {
        private readonly InferenceSession _session;
        private readonly BertTokenizer _tokenizer;   // Tokenizador propio o vía HuggingFace.Tokenizers
        private readonly int _maxSequenceLength;
        private bool _disposed;

        public int EmbeddingDimensions { get; }

        public OnnxVectorizationBrain(OnnxBrainOptions options)
        {
            // SessionOptions optimizadas para CPU inference:
            var sessionOptions = new SessionOptions();
            sessionOptions.EnableCpuMemArena = true;
            sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            // Para entornos con GPU (futuro): sessionOptions.AppendExecutionProvider_CUDA(0);

            _session = new InferenceSession(options.ModelPath, sessionOptions);
            _tokenizer = BertTokenizer.FromPretrained(options.TokenizerPath);
            _maxSequenceLength = options.MaxSequenceLength;  // 512 para MiniLM
            EmbeddingDimensions = options.EmbeddingDimensions;  // 384 para MiniLM
        }

        public async Task<float[]> GenerateEmbeddingAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            var results = await GenerateBatchEmbeddingsAsync([text], cancellationToken);
            return results[0];
        }

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
            IEnumerable<string> texts,
            CancellationToken cancellationToken = default)
        {
            // ONNX Runtime es sincrónico en CPU; usamos Task.Run para no bloquear el hilo de llamada.
            return Task.Run(() =>
            {
                var textList = texts.ToList();
                var encodings = textList.Select(t => _tokenizer.Encode(t, _maxSequenceLength)).ToList();

                // Construcción de tensores de entrada (input_ids, attention_mask, token_type_ids)
                var inputIds = BuildTensor(encodings, e => e.InputIds);
                var attentionMask = BuildTensor(encodings, e => e.AttentionMask);
                var tokenTypeIds = BuildTensor(encodings, e => e.TokenTypeIds);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                    NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
                    NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
                };

                using var outputs = _session.Run(inputs);
                var lastHiddenState = outputs.First().AsTensor<float>();

                // Mean Pooling: promedio de los token embeddings ponderado por attention_mask
                IReadOnlyList<float[]> embeddings = MeanPool(lastHiddenState, attentionMask, textList.Count);
                return embeddings;

            }, cancellationToken);
        }

        // Mean Pooling + L2 Normalization (estándar para sentence-transformers)
        private IReadOnlyList<float[]> MeanPool(
            Tensor<float> hiddenState, DenseTensor<long> mask, int batchSize)
        {
            // ... implementación del pooling ...
        }

        public void Dispose()
        {
            if (!_disposed) { _session.Dispose(); _disposed = true; }
        }
    }

    // RagEngine.Core/Infrastructure/OnnxBrainOptions.cs
    public sealed record OnnxBrainOptions
    {
        public required string ModelPath { get; init; }      // Ruta al .onnx exportado
        public required string TokenizerPath { get; init; }  // Ruta al tokenizer.json
        public int MaxSequenceLength { get; init; } = 512;
        public int EmbeddingDimensions { get; init; } = 384;
        public int BatchSize { get; init; } = 32;            // Chunks por lote ONNX
    }

### Decisiones de Diseño Clave

Decisión │ Justificación
─────────────────────────────────────────┼──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
InferenceSession como Singleton │ La carga del modelo .onnx consume tiempo y memoria. Una sola instancia registrada como Singleton en el contenedor DI es el patrón correcto.
GenerateBatchEmbeddingsAsync separado │ El batch inference de ONNX es 5-10x más eficiente que N llamadas individuales. El pipeline debe preferir siempre el método batch.
Mean Pooling manual │ Los modelos de Hugging Face exportados a ONNX exponen la capa last_hidden_state . El pooling y la normalización L2 deben implementarse en la aplicación
│ cliente.
Task.Run wrapper │ ONNX Runtime en CPU es una operación de bloqueo de CPU intensiva. Envolverla en Task.Run libera el hilo del caller (especialmente crítico en la futura Minimal
│ API).
──────

## Componente 3: ISemanticRetriever — El Motor de Búsqueda Semántica

Responsabilidad única: Dado un query en lenguaje natural, convertirlo en un vector, consultarlo contra la base de datos vectorial Qdrant y devolver los fragmentos de código más semánticamente relevantes,
listos para inyectar en el contexto del LLM.

### Contrato (Abstracción)

    // RagEngine.Core/Abstractions/ISemanticRetriever.cs

    namespace RagEngine.Core.Abstractions;

    /// <summary>
    /// Ejecuta búsquedas semánticas contra la base de datos vectorial.
    /// Combina búsqueda vectorial densa con filtrado por metadatos.
    /// </summary>
    public interface ISemanticRetriever
    {
        /// <summary>
        /// Busca los K fragmentos más relevantes para la consulta dada.
        /// </summary>
        /// <param name="query">Pregunta o descripción en lenguaje natural.</param>
        /// <param name="options">Parámetros de búsqueda (K, filtros, umbral de similitud).</param>
        Task<IReadOnlyList<RetrievalResult>> SearchAsync(
            string query,
            RetrievalOptions options,
            CancellationToken cancellationToken = default);
    }

### Entidades de Dominio Asociadas

    // RagEngine.Core/Domain/RetrievalResult.cs

    namespace RagEngine.Core.Domain;

    /// <summary>
    /// Un fragmento de código recuperado con su puntuación de relevancia y metadatos.
    /// Este es el artefacto final que se inyecta en el prompt del LLM.
    /// </summary>
    public sealed record RetrievalResult(
        string ChunkId,
        string Content,                    // El fragmento de código en texto plano
        float SimilarityScore,             // Cosine similarity [0.0 - 1.0]
        CodeChunkMetadata Metadata         // Contexto estructural del fragmento
    );

    /// <summary>
    /// Metadatos ricos que permiten al LLM entender el CONTEXTO del fragmento
    /// sin necesidad de ver el archivo completo.
    /// </summary>
    public sealed record CodeChunkMetadata(
        string FilePath,
        SourceLanguage Language,
        string? Namespace,
        string? ClassName,
        string? MethodName,
        int StartLine,
        int EndLine,
        DateTimeOffset LastModified,
        string RepositoryName              // Para multi-repo enterprise scenarios
    );

    // RagEngine.Core/Domain/RetrievalOptions.cs

    namespace RagEngine.Core.Domain;

    /// <summary>
    /// Parámetros para refinar la búsqueda semántica.
    /// Diseñado para uso tanto programático (API) como interactivo (CLI).
    /// </summary>
    public sealed record RetrievalOptions
    {
        /// <summary>Número máximo de resultados a retornar. Default: 10.</summary>
        public int TopK { get; init; } = 10;

        /// <summary>
        /// Umbral mínimo de similitud. Resultados por debajo son descartados.
        /// 0.7 es un buen punto de partida para código fuente.
        /// </summary>
        public float MinimumSimilarityScore { get; init; } = 0.70f;

        /// <summary>Filtrar por lenguaje de programación específico.</summary>
        public SourceLanguage? FilterByLanguage { get; init; }

        /// <summary>Filtrar por namespace (útil en soluciones .NET grandes).</summary>
        public string? FilterByNamespace { get; init; }

        /// <summary>Nombre de la colección en Qdrant.</summary>
        public required string CollectionName { get; init; }

        /// <summary>
        /// Si true, aplica Re-Ranking con un Cross-Encoder para mayor precisión.
        /// Más costoso computacionalmente.
        /// </summary>
        public bool UseReRanking { get; init; } = false;
    }

### Implementación Concreta: QdrantSemanticRetriever

    // RagEngine.Core/Infrastructure/QdrantSemanticRetriever.cs

    namespace RagEngine.Core.Infrastructure;

    using Qdrant.Client;
    using Qdrant.Client.Grpc;

    /// <summary>
    /// Implementación de búsqueda semántica usando Qdrant via su cliente gRPC oficial.
    /// El cliente gRPC es preferido sobre HTTP para menor latencia en operaciones bulk.
    /// </summary>
    public sealed class QdrantSemanticRetriever : ISemanticRetriever
    {
        private readonly QdrantClient _qdrantClient;
        private readonly IVectorizationBrain _brain;
        private readonly ILogger<QdrantSemanticRetriever> _logger;

        public QdrantSemanticRetriever(
            QdrantClient qdrantClient,
            IVectorizationBrain brain,
            ILogger<QdrantSemanticRetriever> logger)
        {
            _qdrantClient = qdrantClient;
            _brain = brain;
            _logger = logger;
        }

        public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
            string query,
            RetrievalOptions options,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "Ejecutando búsqueda semántica. Query: '{Query}', TopK: {TopK}",
                query, options.TopK);

            // 1. Vectorizar el query
            var queryVector = await _brain.GenerateEmbeddingAsync(query, cancellationToken);

            // 2. Construir filtros de Qdrant desde RetrievalOptions
            var filter = BuildQdrantFilter(options);

            // 3. Ejecutar búsqueda vectorial
            var searchResults = await _qdrantClient.SearchAsync(
                collectionName: options.CollectionName,
                vector: queryVector,
                filter: filter,
                limit: (ulong)(options.UseReRanking ? options.TopK * 3 : options.TopK), // Over-fetch para re-ranking
                scoreThreshold: options.MinimumSimilarityScore,
                withPayload: true,
                cancellationToken: cancellationToken
            );

            // 4. Mapear resultados de Qdrant a domain entities
            var results = searchResults
                .Select(MapToRetrievalResult)
                .ToList();

            // 5. Re-ranking opcional (Cross-Encoder)
            if (options.UseReRanking && results.Count > options.TopK)
            {
                results = await ReRankAsync(query, results, options.TopK, cancellationToken);
            }

            return results.AsReadOnly();
        }

        private static Filter? BuildQdrantFilter(RetrievalOptions options)
        {
            var conditions = new List<Condition>();

            if (options.FilterByLanguage.HasValue)
            {
                conditions.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "language",
                        Match = new Match { Text = options.FilterByLanguage.Value.ToString() }
                    }
                });
            }

            if (!string.IsNullOrWhiteSpace(options.FilterByNamespace))
            {
                conditions.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "namespace",
                        Match = new Match { Text = options.FilterByNamespace }
                    }
                });
            }

            return conditions.Count == 0
                ? null
                : new Filter { Must = { conditions } };
        }

        private static RetrievalResult MapToRetrievalResult(ScoredPoint point)
        {
            var payload = point.Payload;
            return new RetrievalResult(
                ChunkId: point.Id.ToString(),
                Content: payload["content"].StringValue,
                SimilarityScore: point.Score,
                Metadata: new CodeChunkMetadata(
                    FilePath: payload["file_path"].StringValue,
                    Language: Enum.Parse<SourceLanguage>(payload["language"].StringValue),
                    Namespace: payload.GetValueOrDefault("namespace")?.StringValue,
                    ClassName: payload.GetValueOrDefault("class_name")?.StringValue,
                    MethodName: payload.GetValueOrDefault("method_name")?.StringValue,
                    StartLine: (int)payload["start_line"].IntegerValue,
                    EndLine: (int)payload["end_line"].IntegerValue,
                    LastModified: DateTimeOffset.Parse(payload["last_modified"].StringValue),
                    RepositoryName: payload["repository_name"].StringValue
                )
            );
        }
    }
    ──────

## Componente 4: IIngestionPipeline — El Orquestador Principal

Este es el componente de "pegamento" que conecta los tres anteriores bajo un flujo controlado.

    // RagEngine.Core/Abstractions/IIngestionPipeline.cs

    namespace RagEngine.Core.Abstractions;

    /// <summary>
    /// Orquesta el pipeline completo: escaneo → chunking → vectorización → almacenamiento.
    /// Este es el punto de entrada principal para la ingesta de un repositorio.
    /// </summary>
    public interface IIngestionPipeline
    {
        Task<IngestionSummary> IngestRepositoryAsync(
            IngestionRequest request,
            IProgress<IngestionProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }

    public sealed record IngestionRequest(
        string RepositoryPath,
        string CollectionName,
        ScanProfile Profile,
        bool ForceReindex = false          // Si true, elimina y recrea la colección en Qdrant
    );

    public sealed record IngestionSummary(
        int FilesScanned,
        int ChunksGenerated,
        int ChunksIndexed,
        int FilesSkipped,                  // Por error, tamaño, etc.
        TimeSpan TotalDuration,
        long EstimatedMemoryPeakBytes
    );

    public sealed record IngestionProgress(
        int FilesProcessed,
        int TotalFilesDiscovered,
        string CurrentFile,
        IngestionStage Stage
    );

    public enum IngestionStage
    {
        Scanning,
        Chunking,
        Vectorizing,
        Indexing
    }
    ──────

## Registro en el Contenedor DI

    // RagEngine.Core/Extensions/ServiceCollectionExtensions.cs

    namespace RagEngine.Core.Extensions;

    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registra todos los servicios del núcleo RAG en el contenedor DI.
        /// Llamado tanto desde la CLI como desde la Minimal API.
        /// </summary>
        public static IServiceCollection AddRagEngineCore(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            // 1. Configuración tipada
            services.Configure<OnnxBrainOptions>(
                configuration.GetSection("OnnxBrain"));
            services.Configure<QdrantOptions>(
                configuration.GetSection("Qdrant"));

            // 2. ONNX Brain: Singleton por el alto costo de inicialización del modelo
            services.AddSingleton<IVectorizationBrain, OnnxVectorizationBrain>();

            // 3. Qdrant Client: Singleton para reutilizar conexión gRPC
            services.AddSingleton(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<QdrantOptions>>().Value;
                return new QdrantClient(opts.Host, opts.Port);
            });

            // 4. Servicios Scoped: ciclo de vida por operación
            services.AddScoped<IIngestionScanner, FileSystemIngestionScanner>();
            services.AddScoped<ISemanticRetriever, QdrantSemanticRetriever>();
            services.AddScoped<IIngestionPipeline, DefaultIngestionPipeline>();

            return services;
        }
    }
    ──────

## Resumen del Mapa de Dependencias

    IIngestionPipeline (Scoped)
        ├── IIngestionScanner (Scoped)   → Lee el filesystem
        ├── IChunkingStrategy (Scoped)   → [Fase 2 del diseño]
        ├── IVectorizationBrain (Singleton) → ONNX Runtime in-process
        └── QdrantClient (Singleton)    → gRPC → Qdrant Docker

│ Principio Rector: Ninguna implementación concreta está referenciada fuera del namespace Infrastructure . La CLI y la Minimal API solo interactúan con las interfaces del namespace Abstractions .
──────
