using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

/// <summary>
/// Orchestrates the full RAG pipeline: semantic retrieval → context assembly → LLM streaming generation.
/// Implementations must remain stateless so they can be safely registered as singletons or scoped services.
/// </summary>
public interface IRagGenerationService
{
    /// <summary>
    /// Runs the end-to-end RAG pipeline for the given <paramref name="query"/> and
    /// yields each token/chunk of the LLM response as it is generated.
    /// </summary>
    /// <param name="query">
    ///   The natural-language question or code description submitted by the user.
    /// </param>
    /// <param name="collectionName">
    ///   The Qdrant collection that holds the indexed chunks for the target codebase.
    /// </param>
    /// <param name="topK">
    ///   Maximum number of semantic neighbours to retrieve and inject into the LLM context.
    ///   Higher values produce richer context at the cost of a larger prompt.
    /// </param>
    /// <param name="minimumScore">
    ///   Minimum cosine-similarity threshold. Chunks below this score are discarded
    ///   before building the context window. Defaults to 0.10 (dense cosine noise floor for the multilingual model).
    /// </param>
    /// <param name="cancellationToken">Token to cancel the streaming operation.</param>
    /// <returns>
    ///   An async stream of text fragments produced by the LLM, suitable for
    ///   real-time display in a terminal or UI.
    /// </returns>
    IAsyncEnumerable<string> AskStreamingAsync(
        string query,
        string collectionName,
        int topK = 5,
        float minimumScore = 0.10f,
        CancellationToken cancellationToken = default);
}
