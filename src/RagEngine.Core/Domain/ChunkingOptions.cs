namespace RagEngine.Core.Domain;

/// <summary>
/// Configuration options that control how source artifacts are chunked.
/// </summary>
public sealed record ChunkingOptions
{
    /// <summary>Approximate maximum tokens per chunk. Default: 512 (MiniLM-L6-v2 limit).</summary>
    public int MaxTokensPerChunk { get; init; } = 512;

    /// <summary>
    /// Token overlap between consecutive chunks from the same file.
    /// Preserves context at cut boundaries.
    /// </summary>
    public int OverlapTokens { get; init; } = 64;

    /// <summary>Behavior when a single method/block exceeds MaxTokensPerChunk.</summary>
    public OversizedChunkBehavior OversizedBehavior { get; init; } = OversizedChunkBehavior.SplitWithOverlap;

    /// <summary>
    /// Number of "parent context" lines to prefix in each chunk.
    /// For a method, this injects the containing class signature.
    /// </summary>
    public int ParentContextLines { get; init; } = 5;

    /// <summary>Repository name used in enriched chunk context headers.</summary>
    public string RepositoryName { get; init; } = "unknown-repo";

    /// <summary>Number of chunks per ONNX inference batch. Default: 32.</summary>
    public int BatchSize { get; init; } = 32;

    public static ChunkingOptions Default => new();
}

public enum OversizedChunkBehavior
{
    SplitWithOverlap,   // Split with sliding window
    TruncateToMax,      // Truncate to max token limit
    IndexAsWholeFile    // Index the whole file as one chunk
}
