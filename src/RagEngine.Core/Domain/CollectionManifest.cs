namespace RagEngine.Core.Domain;

/// <summary>
/// Tracks model identity and collection metadata to detect semantic drift.
/// Stored as a Qdrant collection payload entry keyed by "__manifest__".
/// The SHA-256 hash of model.onnx identifies the embedding model used;
/// a hash mismatch signals that the collection must be re-indexed.
/// </summary>
public sealed record CollectionManifest
{
    public required string CollectionName { get; init; }
    public required string ModelName { get; init; }
    public required string ModelOnnxSha256 { get; init; }
    public required int EmbeddingDimension { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastIndexedAt { get; set; } = DateTimeOffset.UtcNow;
    public int TotalChunks { get; set; }
}
