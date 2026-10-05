using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Services.Generation;
using RagEngine.Core.Infrastructure;
using RagEngine.Core.Infrastructure.Generation;
using RagEngine.Core.Infrastructure.Summary;

namespace RagEngine.Core.Extensions;

// ─────────────────────────────────────────────────────────────────────────────
//  Typed options — bound from the "Ollama" section of appsettings.json
//
//  Ítem 9.8: OllamaOptions se movió a RagEngine.Core.Domain (ver Domain/OllamaOptions.cs).
//  Este archivo sólo la CONSUME para configurar el binding y construir el Kernel;
//  ya no la define, porque adaptadores de aplicación (Infrastructure/, Pipeline/)
//  la necesitaban y no pueden depender de Extensions/ (la composición del host).
// ─────────────────────────────────────────────────────────────────────────────
//  Generation DI extension  (separate from AddRagEngineCore to keep
//  the Core extension focused on infrastructure and allow hosts that
//  don't need LLM generation to opt out cleanly)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Registers the Semantic Kernel pipeline and the <see cref="IRagGenerationService"/>
/// implementation. Connects to a local Ollama instance using the OpenAI-compatible
/// API — no third-party Ollama SDK required.
/// </summary>
public static class GenerationServiceExtensions
{
    /// <summary>
    /// Adds the Semantic Kernel + Ollama connector and the RAG generation orchestrator.
    /// Must be called AFTER <c>AddRagEngineCore</c> since it depends on
    /// <see cref="ISemanticRetriever"/> being already registered.
    /// </summary>
    public static IServiceCollection AddRagEngineGeneration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── 1. Bind typed options ─────────────────────────────────────────────
        services.Configure<OllamaOptions>(
            configuration.GetSection(OllamaOptions.SectionName));
        services.Configure<RagGenerationOptions>(
            configuration.GetSection(RagGenerationOptions.SectionName));
        services.Configure<MetaIntentOptions>(
            configuration.GetSection(MetaIntentOptions.SectionName));

        // Singleton: reuses IVectorizationBrain (itself a singleton) and caches the
        // exemplar embeddings for the process lifetime instead of recomputing them
        // per request.
        services.AddSingleton<IMetaIntentDetector, SemanticMetaIntentDetector>();

        // ── 2. Register Semantic Kernel as a Singleton ────────────────────────
        //
        //  Why Singleton?
        //    • Kernel construction is NOT cheap: it validates the endpoint,
        //      creates the HttpClient pipeline, and sets up middleware.
        //    • The Kernel itself is stateless between calls — ChatHistory lives
        //      on the stack inside ChatAnswerStreamer.StreamAsync.
        //    • Matches how SK is documented for hosted-service scenarios.
        //
        //  Why AddOpenAIChatCompletion and not a dedicated Ollama package?
        //    • Ollama exposes a 100% OpenAI-compatible REST API at /v1.
        //    • SK 1.78.0 ships Connectors.OpenAI; no extra packages are needed.
        //    • El HttpClient (TLS contra la CA propia si aplica) y la clave efectiva
        //      ("ollama" dummy en local, el Bearer real en servidor) salen de
        //      OllamaHttpClientFactory — el único punto que construye este cliente
        //      para los dos consumidores de Ollama (ítem 10.6.2, porte del 17.1 local).
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;

            var httpClient = OllamaHttpClientFactory.Create(opts);

            var builder = Kernel.CreateBuilder();

            builder.AddOpenAIChatCompletion(
                modelId:    opts.ModelId,
                apiKey:     OllamaHttpClientFactory.ResolveSdkApiKey(opts),
                httpClient: httpClient);

            return builder.Build();
        });

        // ── 2b. Resilience pipeline para el HttpClient de chat conversacional ──
        //
        //  Ítem 8.e: hasta ahora el HttpClient de arriba sólo tenía Timeout — sin
        //  retry ni circuit breaker, a diferencia de Qdrant y del generador de
        //  resúmenes (ServiceCollectionExtensions.cs), que ya usan
        //  AddResiliencePipeline. Retry corto (igual que el resumen: la interacción
        //  es conversacional, un usuario esperando en pantalla no debe absorber
        //  backoff largo) + circuit breaker (igual que Qdrant: si Ollama está caído,
        //  cortar en vez de seguir intentando conexión por conexión). Ver
        //  ChatAnswerStreamer.StreamAsync para por qué el pipeline sólo cubre la
        //  apertura del stream y nunca reintenta una vez que ya se emitió contenido.
        //  Ítem 10.6.2: un 401/403 de Ollama (credencial ausente o inválida del perfil
        //  servidor) es un fallo de CONFIGURACIÓN, no transitorio — reintentarlo o
        //  contarlo para el circuit breaker no lo arregla nunca y solo demora el error
        //  al usuario. Se excluye explícitamente de ambas estrategias.
        services.AddResiliencePipeline(ChatAnswerStreamer.ResiliencePipelineName, builder =>
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex is not HttpOperationException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }),
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Constant
            });

            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex is not HttpOperationException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(15)
            });
        });

        // ── 3. Colaboradores de generación (ítem 2.2: un rol, una clase) ─────
        //
        //  Singleton porque todas sus dependencias lo son (Kernel, ISummaryCache,
        //  IOptionsMonitor, ILogger) y ninguno guarda estado entre turnos. Registrarlos
        //  Scoped sería igual de correcto pero pagaría una construcción por petición
        //  sin ganar nada; registrarlos aquí y no dentro de RagGenerationService es lo
        //  que permite sustituirlos en un test sin levantar la tubería entera.
        //  Fábricas explícitas: los colaboradores son internal, y ActivatorUtilities
        //  —lo que usa AddSingleton<T>()— sólo mira constructores públicos.
        services.AddSingleton(sp => new ConfidenceGate(
            sp.GetRequiredService<IOptionsMonitor<RagGenerationOptions>>(),
            sp.GetRequiredService<ILogger<ConfidenceGate>>()));
        services.AddSingleton(sp => new GenerationContextAssembler(
            sp.GetRequiredService<ISummaryCache>(),
            sp.GetRequiredService<IOptionsMonitor<RagGenerationOptions>>(),
            sp.GetRequiredService<ILogger<GenerationContextAssembler>>()));
        services.AddSingleton(sp => new ChatAnswerStreamer(
            sp.GetRequiredService<Kernel>(),
            sp.GetRequiredService<ILogger<ChatAnswerStreamer>>(),
            sp.GetRequiredService<Polly.Registry.ResiliencePipelineProvider<string>>()));

        // ── 4. Register the RAG orchestrator ─────────────────────────────────
        //
        //  Scoped (not Singleton) because ISemanticRetriever is Scoped.
        //  Each CLI command execution gets its own scope (via SpectreHostTypeRegistrar),
        //  so this is effectively one instance per command invocation.
        services.AddScoped<IRagGenerationService>(sp => new RagGenerationService(
            sp.GetRequiredService<ISemanticRetriever>(),
            sp.GetRequiredService<ILogger<RagGenerationService>>(),
            sp.GetRequiredService<IOptionsMonitor<RagGenerationOptions>>(),
            sp.GetRequiredService<IMetaIntentDetector>(),
            sp.GetRequiredService<ConfidenceGate>(),
            sp.GetRequiredService<GenerationContextAssembler>(),
            sp.GetRequiredService<ChatAnswerStreamer>()));

        return services;
    }
}
