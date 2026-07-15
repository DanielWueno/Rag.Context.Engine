namespace RagEngine.Core.Domain;

/// <summary>
/// A retrieved chunk paired with its cosine similarity score from Qdrant.
/// </summary>
public sealed record ScoredChunk(CodeChunk Chunk, float Score);

/// <summary>
/// Optional metadata filter for Qdrant vector search queries.
/// </summary>
public sealed record RetrievalFilter
{
    /// <summary>Restrict results to a specific programming language.</summary>
    public string? Language { get; init; }

    /// <summary>Restrict results to files under a specific path prefix.</summary>
    public string? FilePathPrefix { get; init; }

    /// <summary>Restrict results to a specific chunk type (e.g., "Method").</summary>
    public string? ChunkType { get; init; }
}

/// <summary>
/// Configuration options for a single ingestion run.
/// </summary>
public sealed record IngestionOptions
{
    /// <summary>Number of chunks to batch before sending to ONNX and Qdrant.</summary>
    public int BatchSize { get; init; } = 32;

    /// <summary>If true, skips files whose content hash matches an already-indexed chunk.</summary>
    public bool IncrementalMode { get; init; } = false;

    /// <summary>
    /// Glob patterns for files to include (e.g., "*.cs", "*.ts").
    /// Empty means all supported file types.
    /// </summary>
    public IReadOnlyList<string> IncludePatterns { get; init; } = [];

    /// <summary>Glob patterns for files to explicitly exclude.</summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } =
    [
        "**/bin/**", "**/obj/**", "**/node_modules/**", "**/.git/**"
    ];
}

/// <summary>
/// Summary report produced at the end of a completed ingestion run.
/// </summary>
public sealed record IngestionResult
{
    public required int FilesScanned { get; init; }
    public required int ChunksProduced { get; init; }
    public required int ChunksUpserted { get; init; }
    public required int ChunksSkipped { get; init; }
    public required int Errors { get; init; }
    public required TimeSpan Elapsed { get; init; }
}
