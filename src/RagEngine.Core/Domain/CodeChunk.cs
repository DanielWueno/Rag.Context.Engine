namespace RagEngine.Core.Domain;

/// <summary>
/// Represents a discrete, semantically meaningful fragment of source code
/// produced by the chunking stage of the ingestion pipeline.
/// </summary>
public sealed record CodeChunk
{
    /// <summary>
    /// Deterministic UUID v5 derived from the file path + content hash.
    /// Enables idempotent re-indexing without duplicates.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>The raw text content of this chunk.</summary>
    public required string Content { get; init; }

    /// <summary>Absolute path of the source file this chunk belongs to.</summary>
    public required string FilePath { get; init; }

    /// <summary>Programming language identifier (e.g., "csharp", "typescript", "sql").</summary>
    public required string Language { get; init; }

    /// <summary>1-based line number where this chunk starts in the source file.</summary>
    public required int StartLine { get; init; }

    /// <summary>1-based line number where this chunk ends in the source file.</summary>
    public required int EndLine { get; init; }

    /// <summary>
    /// Structural label for the chunk's role (e.g., "Method", "Class", "Interface", "Block").
    /// </summary>
    public string ChunkType { get; init; } = "Block";

    /// <summary>Optional parent symbol name (e.g., the class containing a method chunk).</summary>
    public string? ParentSymbol { get; init; }

    /// <summary>SHA-256 hash of <see cref="Content"/> for change detection.</summary>
    public required string ContentHash { get; init; }

    /// <summary>UTC timestamp when this chunk was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
