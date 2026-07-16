namespace RagEngine.Core.Domain;

/// <summary>
/// A retrieved code chunk with its relevance score and structural metadata.
/// This is the final artifact injected into the LLM prompt context.
/// </summary>
public sealed record RetrievalResult(
    string ChunkId,
    string Content,
    float SimilarityScore,
    CodeChunkMetadata Metadata
);

/// <summary>
/// Parameters for refining a semantic search query.
/// </summary>
public sealed record RetrievalOptions
{
    /// <summary>Maximum number of results to return. Default: 10.</summary>
    public int TopK { get; init; } = 10;

    /// <summary>
    /// Minimum similarity score threshold. Results below this are discarded.
    /// 0.65 is a good starting point for source code.
    /// </summary>
    public float MinimumSimilarityScore { get; init; } = 0.65f;

    /// <summary>Filter results to a specific programming language.</summary>
    public SourceLanguage? FilterByLanguage { get; init; }

    /// <summary>Filter results by namespace prefix.</summary>
    public string? FilterByNamespace { get; init; }

    /// <summary>The Qdrant collection to search.</summary>
    public required string CollectionName { get; init; }

    /// <summary>If true, applies Cross-Encoder re-ranking for higher precision.</summary>
    public bool UseReRanking { get; init; } = false;
}
