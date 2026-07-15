using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

/// <summary>
/// Performs semantic vector search against Qdrant using gRPC.
/// Supports filtered retrieval and hybrid dense+sparse (BM25) search.
/// </summary>
public interface ISemanticRetriever
{
    /// <summary>
    /// Searches for the most semantically similar chunks to the given query vector.
    /// </summary>
    /// <param name="queryVector">The embedding of the query text.</param>
    /// <param name="collectionName">The Qdrant collection to search.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="filter">Optional metadata filter (e.g., by language or file path).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Ranked list of retrieved code chunks with similarity scores.</returns>
    Task<IReadOnlyList<ScoredChunk>> SearchAsync(
        float[] queryVector,
        string collectionName,
        int topK = 10,
        RetrievalFilter? filter = null,
        CancellationToken cancellationToken = default);
}
