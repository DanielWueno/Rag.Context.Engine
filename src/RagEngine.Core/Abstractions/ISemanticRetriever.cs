using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

/// <summary>
/// Executes semantic searches against the Qdrant vector database.
/// Vectorizes the natural-language query via IVectorizationBrain, then
/// retrieves the most semantically similar code chunks using HNSW search.
/// </summary>
public interface ISemanticRetriever
{
    /// <summary>
    /// Searches for the top-K most semantically relevant chunks for the given query.
    /// </summary>
    /// <param name="query">Natural-language question or code description.</param>
    /// <param name="options">Search parameters (TopK, score threshold, language filter).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Ranked list of retrieved results ready to inject into an LLM context.</returns>
    Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        string query,
        RetrievalOptions options,
        CancellationToken cancellationToken = default);
}
