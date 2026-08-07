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
    /// <param name="useReRanking">
    ///   If true, the retriever widens the candidate pool (3×topK) and re-scores it
    ///   with the Cross-Encoder before injecting the final topK into the LLM context.
    /// </param>
    /// <param name="responseMode">
    ///   Selects the system-prompt persona. <see cref="ResponseMode.Simple"/> (default)
    ///   explains in plain language with no code/citations shown, for non-technical
    ///   readers. <see cref="ResponseMode.Technical"/> keeps the code/docs-aware
    ///   prompts with fenced code blocks and file citations, for developers.
    /// </param>
    /// <param name="history">
    ///   Prior turns of the conversation, oldest first, supplied by the caller on every
    ///   call (this service holds no session state). Only <paramref name="query"/> is
    ///   used for retrieval — history is injected into the LLM prompt for conversational
    ///   continuity, not re-searched against Qdrant.
    /// </param>
    /// <param name="onStatus">
    ///   Optional callback invoked with a short human-readable status label at
    ///   intermediate pipeline transitions the caller cannot otherwise observe —
    ///   today, only right before the <see cref="ResponseMode.Simple"/> grounded
    ///   path runs its post-generation sanitizer on the fully buffered answer (see
    ///   docs/analisis-futuro/modo-respuesta-simple-codigo.md, Fase 1 punto 4).
    ///   Callers that don't need progress UX (e.g. the CLI) can leave this null.
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
        bool useReRanking = false,
        ResponseMode responseMode = ResponseMode.Simple,
        IReadOnlyList<ChatTurn>? history = null,
        Func<string, CancellationToken, Task>? onStatus = null,
        CancellationToken cancellationToken = default);
}
