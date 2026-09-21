using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Audit;
using RagEngine.Core.Infrastructure.Authorization;
using RagEngine.Core.Infrastructure.Chunking;
using RagEngine.Core.Infrastructure.Reranking;
using RagEngine.Core.Infrastructure.Scanning;
using RagEngine.Core.Infrastructure.State;
using RagEngine.Core.Infrastructure.Transport;
using RagEngine.Core.Infrastructure.Vectorization;
using RagEngine.Core.Infrastructure.VectorStore;
using RagEngine.Core.Pipeline;
using RagEngine.Core.Infrastructure.Summary;
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

        // TryAddSingleton: WebApplicationBuilder/generic Host ya registran IConfiguration
        // por su cuenta (este services.TryAdd no lo reemplaza, sólo cubre el caso de un
        // ServiceCollection "pelado" como el de los tests unitarios de este proyecto, que
        // le pasan `configuration` a este método pero no lo registran ellos mismos en DI).
        // TransportOptionsValidator (ítem 12.8) lo necesita para leer Qdrant:ApiKey y
        // Kestrel:Certificates:Default:Path, que no son parte de su propia sección.
        services.TryAddSingleton(configuration);

        services.Configure<QdrantOptions>(
            configuration.GetSection(QdrantOptions.SectionName));

        services.Configure<CollectionAuthorizationOptions>(
            configuration.GetSection(CollectionAuthorizationOptions.SectionName));
        services.AddOptions<CollectionAuthorizationOptions>()
            .Validate(options => Enum.IsDefined(options.Mode),
                "Authorization:Mode debe ser Local o Empresarial.")
            .ValidateOnStart();
        services.AddSingleton<ICollectionAuthorizationService, CollectionAuthorizationService>();
        services.AddSingleton<ICollectionActorResolver, CollectionActorResolver>();

        // Ítem 12.8: perfil de transporte. Sin sección "Transport" (el caso de hoy),
        // Published cae en false y no exige nada — comportamiento idéntico al de antes
        // de este ítem. Con Transport:Published=true, TransportOptionsValidator exige
        // credencial de Qdrant y TLS resuelto (en el proceso o corriente arriba) antes
        // de que el host arranque.
        services.Configure<TransportOptions>(
            configuration.GetSection(TransportOptions.SectionName));
        services.AddSingleton<IValidateOptions<TransportOptions>, TransportOptionsValidator>();
        services.AddOptions<TransportOptions>().ValidateOnStart();

        services.Configure<CrossEncoderOptions>(
            configuration.GetSection(CrossEncoderOptions.SectionName));

        services.Configure<IngestionOptions>(
            configuration.GetSection(IngestionOptions.SectionName));

        services.Configure<RetrievalFusionOptions>(
            configuration.GetSection(RetrievalFusionOptions.SectionName));

        services.Configure<TwoHopOptions>(
            configuration.GetSection(TwoHopOptions.SectionName));
        services.AddOptions<TwoHopOptions>()
            .Validate(o => o.SeedResults > 0, "TwoHop:SeedResults debe ser mayor a 0.")
            .Validate(o => o.MaxExpansionResults > 0, "TwoHop:MaxExpansionResults debe ser mayor a 0.")
            .Validate(o => o.Weight > 0, "TwoHop:Weight debe ser mayor a 0 (un peso <=0 deja el salto inerte).")
            .ValidateOnStart();

        // Ítem 7.a: catálogo de perfiles de recuperación por colección. Sin declarar
        // (sección "RetrievalProfiles" ausente) el diccionario queda vacío y
        // RetrievalProfileResolver resuelve siempre a null — comportamiento idéntico
        // al de antes de este ítem.
        services.Configure<RetrievalProfileCatalogOptions>(
            configuration.GetSection(RetrievalProfileCatalogOptions.SectionName));
        services.AddSingleton<IRetrievalProfileResolver, RetrievalProfileResolver>();

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
            // Ítem 12.1: apiKey vacío/null preserva exactamente el comportamiento previo
            // (new QdrantClient(host, port)) — https queda en false porque Qdrant local
            // no termina TLS; el perfil de despliegue empresarial con TLS es un ítem
            // aparte (ver docs/configuracion.md).
            return string.IsNullOrEmpty(opts.ApiKey)
                ? new QdrantClient(opts.Host, opts.GrpcPort)
                : new QdrantClient(opts.Host, opts.GrpcPort, https: false, apiKey: opts.ApiKey);
        });

        // ──── 4. Vector Store: Singleton (wraps the Singleton QdrantClient) ──────────
        services.AddSingleton<QdrantVectorStore>();
        services.AddSingleton<IVectorStoreAdmin>(sp => sp.GetRequiredService<QdrantVectorStore>());
        services.AddSingleton<IVectorStoreWriter>(sp => sp.GetRequiredService<QdrantVectorStore>());

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

        services.AddSingleton<ISummaryCache>(sp =>
        {
            var ingestionOpts = sp.GetRequiredService<IOptions<IngestionOptions>>().Value;
            var ollamaOpts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            var promptVersion = OllamaBusinessSummaryGenerator.ComputePromptVersion(ollamaOpts.ModelId);
            return SummaryCache.Open(ingestionOpts.ResumenCachePath, promptVersion);
        });

        // ── Auditoría local (ítem 12.11): base SQLite propia, separada de la caché de
        // resúmenes — un evento de auditoría es historia inmutable, no algo que se
        // regenere al cambiar de prompt_version.
        services.Configure<AuditOptions>(
            configuration.GetSection(AuditOptions.SectionName));
        services.AddSingleton<IAuditEventStore>(sp =>
        {
            var auditOpts = sp.GetRequiredService<IOptions<AuditOptions>>().Value;
            return SqliteAuditEventStore.Open(auditOpts.DbPath);
        });

        // ── Estado de ingesta por corrida/documento (ítem 13.1): base SQLite propia,
        // separada de auditoría (append-only) y de la caché de resúmenes — este
        // estado SÍ se actualiza en el tiempo (pending → running → succeeded/failed).
        services.AddSingleton<IIngestionStateStore>(sp =>
        {
            var ingestionOpts = sp.GetRequiredService<IOptions<IngestionOptions>>().Value;
            return SqliteIngestionStateStore.Open(ingestionOpts.IngestionStateDbPath);
        });

        // ── Minimización/retención de logs operativos (ítem 12.9): por defecto no
        // hay diagnóstico de contenido de consulta; el operador lo activa explícitamente
        // con una caducidad obligatoria (ver QueryContentDiagnostics).
        services.Configure<LoggingOptions>(
            configuration.GetSection(LoggingOptions.SectionName));

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
