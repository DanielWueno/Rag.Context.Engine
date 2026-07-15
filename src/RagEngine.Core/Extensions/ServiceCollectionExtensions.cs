using Microsoft.Extensions.DependencyInjection;

namespace RagEngine.Core.Extensions;

/// <summary>
/// Shared IServiceCollection extension for registering RagEngine.Core services.
/// Used by both the CLI host and any future Minimal API host.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all RagEngine core services with their default implementations.
    /// Call this from Program.cs in any host project.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddRagEngineCore(this IServiceCollection services)
    {
        // Implementations will be registered here as they are built in S1–S4.
        // This method acts as the single registration point to keep hosts thin.

        // Example (will be uncommented when implementations exist):
        // services.AddSingleton<IVectorizationBrain, OnnxVectorizationBrain>();
        // services.AddSingleton<ISemanticRetriever, QdrantSemanticRetriever>();
        // services.AddSingleton<IIngestionScanner, FileSystemIngestionScanner>();
        // services.AddSingleton<IIngestionPipeline, ChannelIngestionPipeline>();

        return services;
    }
}
