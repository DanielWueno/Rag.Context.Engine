namespace RagEngine.Core.Infrastructure.VectorStore;

/// <summary>
/// Configuration for the Qdrant vector database connection.
/// Bound from the "Qdrant" section of appsettings.json.
/// </summary>
public sealed record QdrantOptions
{
    public const string SectionName = "Qdrant";

    /// <summary>Qdrant server hostname. Default: localhost.</summary>
    public string Host { get; init; } = "localhost";

    /// <summary>gRPC port (used by the .NET client). Default: 6334.</summary>
    public int GrpcPort { get; init; } = 6334;

    /// <summary>REST API port (for health checks via curl). Default: 6333.</summary>
    public int HttpPort { get; init; } = 6333;

    /// <summary>Default collection name when not specified by the user.</summary>
    public string DefaultCollection { get; init; } = "rag-engine";
}
