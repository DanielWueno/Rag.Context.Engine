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
    /// yields the grounding decision and sources, each text fragment as it is
    /// generated, and the final outcome. Retrieval runs once (zero for meta-intent).
    /// </summary>
    /// <param name="query">
    ///   The natural-language question or code description submitted by the user.
    /// </param>
    /// <param name="collectionName">
    ///   The vector-store collection that holds the indexed chunks for the target codebase.
    /// </param>
    /// <param name="retrievalContext">Explicit authorization context for this turn.</param>
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
    ///   continuity, not re-searched against the vector store.
    /// </param>
    /// <param name="promptFamily">
    ///   Ítem 7.a: familia de prompt forzada por el perfil de la colección (resuelta por
    ///   el llamador, típicamente la API, vía <c>IRetrievalProfileResolver</c>). Null
    ///   (el default) conserva la heurística de contenido de siempre — ver
    ///   <c>SystemPromptComposer.SelectTemplate</c>.
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
    ///   An ordered stream of <see cref="GenerationEvent"/>. Hosts display TextDelta
    ///   without buffering and use the supplied sources/verdict, never a second
    ///   retrieval or gate evaluation. Completed is absent on error/cancellation.
    /// </returns>
    IAsyncEnumerable<GenerationEvent> AskStreamingAsync(
        string query,
        string collectionName,
        RetrievalContext retrievalContext,
        int topK = 5,
        float minimumScore = 0.10f,
        bool useReRanking = false,
        ResponseMode responseMode = ResponseMode.Simple,
        IReadOnlyList<ChatTurn>? history = null,
        PromptFamily? promptFamily = null,
        Func<string, CancellationToken, Task>? onStatus = null,
        CancellationToken cancellationToken = default);
}
