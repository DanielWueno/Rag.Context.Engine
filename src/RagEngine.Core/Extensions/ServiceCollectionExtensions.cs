using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Chunking;
using RagEngine.Core.Infrastructure.Reranking;
using RagEngine.Core.Infrastructure.Scanning;
using RagEngine.Core.Infrastructure.Vectorization;
using RagEngine.Core.Infrastructure.VectorStore;
using RagEngine.Core.Pipeline;
using RagEngine.Core.Services.Summary;
using RagEngine.Core.Utilities;

namespace RagEngine.Core.Extensions;

/// <summary>
/// Shared IServiceCollection extension for registering all RagEngine.Core services.
/// Called from both the CLI host and any future Minimal API host.
/// This is the single registration point \u2014 host projects remain thin.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all RagEngine core services with their appropriate lifetimes.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The host configuration (for binding options).</param>
    public static IServiceCollection AddRagEngineCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // \u2500\u2500 1. Typed Options \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        // ──── 1. Typed Options ─────────────────────────────────────────────────────────
        services.Configure<OnnxBrainOptions>(
            configuration.GetSection(OnnxBrainOptions.SectionName));

        services.Configure<QdrantOptions>(
            configuration.GetSection(QdrantOptions.SectionName));

        services.Configure<CrossEncoderOptions>(
            configuration.GetSection(CrossEncoderOptions.SectionName));

        services.Configure<IngestionOptions>(
            configuration.GetSection(IngestionOptions.SectionName));

        services.Configure<RetrievalFusionOptions>(
            configuration.GetSection(RetrievalFusionOptions.SectionName));

        // OllamaOptions también se registra aquí (no solo en AddRagEngineGeneration):
        // la Fase 2 de ingesta (resumen de negocio) necesita el endpoint de Ollama
        // sin que el host tenga que habilitar generación conversacional. Configure<T>
        // llamado dos veces sobre la misma sección desde dos extensiones es inofensivo.
        services.Configure<OllamaOptions>(
            configuration.GetSection(OllamaOptions.SectionName));

        // ── 1b. Resolución de rutas de modelo ──────────────────────────────────────
        // Las rutas de appsettings admiten ~ y ${RAG_MODELS_DIR} para que el archivo
        // versionado no lleve la ruta absoluta de la máquina de nadie. Se resuelven
        // aquí, una sola vez, en vez de en cada consumidor.
        //
        // No se usa ValidateOnStart: el CLI construye el Host y le entrega el
        // contenedor a Spectre sin arrancarlo (ver RagEngine.Cli/Program.cs), así que
        // no dispararía; y validar la existencia del modelo al arranque rompería
        // `rag doctor`, cuyo trabajo es justamente reportar qué falta. La validación
        // vive donde se abre el archivo: OnnxVectorizationBrain y
        // OnnxCrossEncoderReRanker ya fallan con un mensaje accionable.
        services.PostConfigure<OnnxBrainOptions>(opts =>
        {
            opts.ModelPath = RagEnginePaths.ResolveModelPath(opts.ModelPath);
            opts.VocabPath = RagEnginePaths.ResolveModelPath(opts.VocabPath);
        });

        services.PostConfigure<CrossEncoderOptions>(opts =>
        {
            opts.ModelPath = RagEnginePaths.ResolveModelPath(opts.ModelPath);
            opts.VocabPath = RagEnginePaths.ResolveModelPath(opts.VocabPath);
        });

        // ── 2. ONNX Brain: Singleton (expensive to initialize — one InferenceSession) ──
        services.AddSingleton<IVectorizationBrain, OnnxVectorizationBrain>();

        // ── 2b. Cross-Encoder re-ranker: Singleton con carga perezosa — la
        // InferenceSession solo se crea si alguna búsqueda pide --rerank ──
        services.AddSingleton<IReRanker, OnnxCrossEncoderReRanker>();

        // ── 3. Sparse Tokenizer: Singleton (stateless, thread-safe, zero I/O) ────────
        services.AddSingleton<ISparseTokenizer, SparseTokenizer>();

        // ──── 3. Qdrant Client: Singleton (reuse gRPC connection) ──────────────────
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<QdrantOptions>>().Value;
            return new QdrantClient(opts.Host, opts.GrpcPort);
        });

        // ──── 4. Vector Store: Singleton (wraps the Singleton QdrantClient) ──────────
        services.AddSingleton<QdrantVectorStore>();

        // ──── Add Polly Resilience ──────────────────────────────────────────
        services.AddResiliencePipeline("qdrant", builder =>
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential
            });

            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(15)
            });
        });

        // Reintentos por-chunk para la generación de resúmenes (decisión 4). Cortos
        // y acotados: un timeout ya cuesta hasta OllamaOptions.TimeoutSeconds por
        // intento, y el circuit breaker de fallos de CONEXIÓN consecutivos vive a
        // nivel de la Fase 2 de ingesta (DefaultIngestionPipeline), no aquí.
        services.AddResiliencePipeline(OllamaBusinessSummaryGenerator.ResiliencePipelineName, builder =>
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Constant
            });
        });

        // ──── Resumen de negocio (Fase 2 de ingesta, opt-in): Singleton ─────────
        // Registrado en Core (no en AddRagEngineGeneration, que es opcional) para
        // que la ingesta no dependa de que el host habilite generación conversacional.
        services.AddSingleton<IBusinessSummaryGenerator, OllamaBusinessSummaryGenerator>();

        services.AddSingleton(sp =>
        {
            var ingestionOpts = sp.GetRequiredService<IOptions<IngestionOptions>>().Value;
            var ollamaOpts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            var promptVersion = OllamaBusinessSummaryGenerator.ComputePromptVersion(ollamaOpts.ModelId);
            return SummaryCache.Open(ingestionOpts.ResumenCachePath, promptVersion);
        });

        // ── 5. Chunking Strategies: Auto-Discovery ────────────────────────────
        services.AddSingleton<FallbackChunkingStrategy>();

        var strategyTypes = typeof(IChunkingStrategy).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && 
                        !t.IsInterface && 
                        typeof(IChunkingStrategy).IsAssignableFrom(t) &&
                        t != typeof(FallbackChunkingStrategy));

        foreach (var type in strategyTypes)
        {
            services.AddSingleton(type);
            services.AddSingleton(typeof(IChunkingStrategy), sp => sp.GetRequiredService(type));
        }

        services.AddSingleton<ChunkingStrategyRouter>();

        // ──── 6. Scoped Services: one instance per CLI command execution ────────
        services.AddScoped<IIngestionScanner, FileSystemIngestionScanner>();
        services.AddScoped<ISemanticRetriever, QdrantSemanticRetriever>();
        services.AddScoped<IIngestionPipeline, DefaultIngestionPipeline>();

        // ── 7. Pipeline utilities ──────────────────────────────────────────────
        services.AddSingleton<ContextAssembler>();

        return services;
    }
}
