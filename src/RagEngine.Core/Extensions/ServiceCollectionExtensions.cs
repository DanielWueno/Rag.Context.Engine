using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Infrastructure.Chunking;
using RagEngine.Core.Infrastructure.Scanning;
using RagEngine.Core.Infrastructure.Vectorization;
using RagEngine.Core.Infrastructure.VectorStore;
using RagEngine.Core.Pipeline;

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
        services.Configure<OnnxBrainOptions>(
            configuration.GetSection(OnnxBrainOptions.SectionName));

        services.Configure<QdrantOptions>(
            configuration.GetSection(QdrantOptions.SectionName));

        // \u2500\u2500 2. ONNX Brain: Singleton (expensive to initialize \u2014 one InferenceSession) \u2500\u2500
        services.AddSingleton<IVectorizationBrain, OnnxVectorizationBrain>();

        // \u2500\u2500 3. Qdrant Client: Singleton (reuse gRPC connection) \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<QdrantOptions>>().Value;
            return new QdrantClient(opts.Host, opts.GrpcPort);
        });

        // ──── 4. Vector Store: Singleton (wraps the Singleton QdrantClient) ──────────
        services.AddSingleton<QdrantVectorStore>();

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
