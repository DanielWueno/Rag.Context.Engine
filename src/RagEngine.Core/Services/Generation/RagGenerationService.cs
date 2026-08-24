using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Services.Generation;

/// <summary>
/// Orquesta la tubería de Retrieval-Augmented Generation. Sólo eso: encadena los
/// pasos y decide el camino; el trabajo de cada paso vive en un colaborador.
///
/// Flujo:
///   0. Meta-intención     — <see cref="IMetaIntentDetector"/> corta antes de buscar.
///   1. Recuperación       — <see cref="ISemanticRetriever"/> devuelve los chunks del top-K.
///   2. Gate de confianza  — <see cref="ConfidenceGate"/> decide banda y camino.
///   3. Contexto           — <see cref="GenerationContextAssembler"/> arma el bloque inyectado.
///   4. Prompt             — <see cref="SystemPromptComposer"/> elige y compone la plantilla.
///   5. Generación         — <see cref="ChatAnswerStreamer"/> emite la respuesta.
///   6. Redacción (Simple) — <see cref="SimpleAnswerSanitizer"/> limpia lo que quedó.
///
/// La descomposición es el ítem 2.2 del plan de ingeniería: esta clase concentraba las
/// seis responsabilidades y cualquier cambio en una obligaba a leer las otras cinco.
///
/// Thread-safety: stateless; safe to register as a singleton.
/// </summary>
public sealed class RagGenerationService : IRagGenerationService
{
    // ──────────────────────────────────────────────────────────────
    //  Dependencies
    // ──────────────────────────────────────────────────────────────

    private readonly ISemanticRetriever _retriever;
    private readonly ILogger<RagGenerationService> _logger;
    private readonly IOptionsMonitor<RagGenerationOptions> _optionsMonitor;
    private readonly IMetaIntentDetector _metaIntentDetector;
    private readonly ConfidenceGate _confidenceGate;
    private readonly GenerationContextAssembler _contextAssembler;
    private readonly ChatAnswerStreamer _answerStreamer;

    /// <summary>
    /// Current snapshot of <see cref="RagGenerationOptions"/>. Read via
    /// <see cref="IOptionsMonitor{TOptions}"/> (not <c>IOptions&lt;T&gt;</c>) so that
    /// <see cref="RagGenerationOptions.EnableSimpleModeSanitizer"/> — the Fase 1
    /// rollback flag — picks up a config/env-var change on container restart (and,
    /// since the monitor also watches for live reload, potentially without one)
    /// instead of being frozen at first resolution for the process lifetime.
    /// </summary>
    private RagGenerationOptions Options => _optionsMonitor.CurrentValue;

    /// <summary>
    /// Alias de <see cref="Prompts.AnswerNotices.NoContextFallback"/>. Sigue siendo
    /// público en esta clase porque es parte del contrato hacia los hosts: la API lo
    /// compara contra la respuesta para no mostrar fuentes junto a un "no encontré
    /// nada" que eligió el propio modelo (ver <c>RagEngine.Api/Program.cs</c>).
    /// </summary>
    public const string NoContextFallbackMessage = Prompts.AnswerNotices.NoContextFallback;

    // ──────────────────────────────────────────────────────────────
    //  Constructor
    // ──────────────────────────────────────────────────────────────

    /// <param name="retriever">
    ///   Abstracción sobre Qdrant que devuelve <see cref="RetrievalResult"/> rankeados.
    /// </param>
    /// <param name="logger">Structured logger injected by the DI container.</param>
    /// <param name="optionsMonitor">
    ///   Bandera de rollback del sanitizador de modo Simple, leída por turno.
    ///   <see cref="IOptionsMonitor{TOptions}"/> en vez de <c>IOptions&lt;T&gt;</c> para
    ///   que un cambio de configuración surta efecto sin recompilar — ver <see cref="Options"/>.
    ///   Los umbrales de banda los lee <see cref="ConfidenceGate"/> por su cuenta.
    /// </param>
    /// <param name="metaIntentDetector">Detects meta-questions about the assistant itself.</param>
    /// <param name="confidenceGate">Decide la banda de confianza del turno.</param>
    /// <param name="contextAssembler">Resuelve el contenido de los chunks y arma el bloque de contexto.</param>
    /// <param name="answerStreamer">Habla con el LLM y emite los fragmentos.</param>
    /// <remarks>
    /// <c>internal</c> y no público: los colaboradores son detalle de implementación
    /// (misma política que el resto del ensamblado, ver el comentario de
    /// <c>InternalsVisibleTo</c> en RagEngine.Core.csproj). Los hosts resuelven
    /// <see cref="IRagGenerationService"/>, nunca construyen esta clase; por eso
    /// <c>AddRagEngineGeneration</c> la registra con una fábrica explícita en vez de
    /// dejar que <c>ActivatorUtilities</c> busque un constructor público.
    /// </remarks>
    internal RagGenerationService(
        ISemanticRetriever retriever,
        ILogger<RagGenerationService> logger,
        IOptionsMonitor<RagGenerationOptions> optionsMonitor,
        IMetaIntentDetector metaIntentDetector,
        ConfidenceGate confidenceGate,
        GenerationContextAssembler contextAssembler,
        ChatAnswerStreamer answerStreamer)
    {
        _retriever          = retriever          ?? throw new ArgumentNullException(nameof(retriever));
        _logger             = logger             ?? throw new ArgumentNullException(nameof(logger));
        _optionsMonitor     = optionsMonitor     ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _metaIntentDetector = metaIntentDetector ?? throw new ArgumentNullException(nameof(metaIntentDetector));
        _confidenceGate     = confidenceGate     ?? throw new ArgumentNullException(nameof(confidenceGate));
        _contextAssembler   = contextAssembler   ?? throw new ArgumentNullException(nameof(contextAssembler));
        _answerStreamer     = answerStreamer     ?? throw new ArgumentNullException(nameof(answerStreamer));
    }

    // ──────────────────────────────────────────────────────────────
    //  IRagGenerationService implementation
    // ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async IAsyncEnumerable<string> AskStreamingAsync(
        string query,
        string collectionName,
        int topK = 5,
        float minimumScore = 0.10f,
        bool useReRanking = false,
        ResponseMode responseMode = ResponseMode.Simple,
        IReadOnlyList<ChatTurn>? history = null,
        Func<string, CancellationToken, Task>? onStatus = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        // ── Step 0: Meta-intent pre-filter ────────────────────────
        // Questions about the assistant itself ("who are you?", "what tech are
        // you built with?") never need retrieval or generation — answering them
        // from a fixed, factual block also sidesteps cases where they'd otherwise
        // score high by accidental lexical overlap with real corpus content.
        if (await _metaIntentDetector.IsMetaIntentAsync(query, cancellationToken))
        {
            _logger.LogInformation("[RAG] Meta-intent match for query: {Query}. Skipping retrieval.", query);
            yield return SystemPromptComposer.SelfDescriptionBlock;
            yield break;
        }

        // ── Step 1: Semantic Retrieval ────────────────────────────
        // NOTE: yield return is not allowed inside try/catch blocks in C# iterator
        // methods. RetrieveChunksAsync owns all exception handling and returns a
        // (success, chunks, errorMessage) tuple so the iterator body stays clean.
        _logger.LogInformation(
            "[RAG] Retrieving top-{TopK} chunks from '{Collection}' for query: {Query}",
            topK, collectionName, query);

        var (retrievalOk, chunks, retrievalError) = await RetrieveChunksAsync(
            query, collectionName, topK, minimumScore, useReRanking, cancellationToken);

        if (!retrievalOk)
        {
            yield return retrievalError!;
            yield break;
        }

        // ── Step 2: confidence gate ───────────────────────────────
        // Sin anclaje se conversa sin contexto y, por la garantía estructural de
        // docs/analisis-futuro/guardrail-banda-baja-conversacional.md, los chunks
        // recuperados (si los hay) no se vuelven a tocar por debajo de esta rama.
        var assessment = _confidenceGate.Assess(chunks!, useReRanking, minimumScore, query);

        if (!assessment.HasGrounding)
        {
            await foreach (var fragment in _answerStreamer.StreamAsync(
                SystemPromptComposer.ComposeNoGrounding(), query, history, cancellationToken))
            {
                yield return fragment;
            }

            yield break;
        }

        _logger.LogInformation("[RAG] Retrieved {Count} chunks. Building context block.", chunks!.Count);

        // ── Step 3: Context Assembly ──────────────────────────────
        // For ResponseMode.Simple, the assembler swaps each chunk's raw content for its
        // cached business summary when one exists (Fase 2) — see
        // docs/analisis-futuro/modo-respuesta-simple-codigo.md. SelectTemplate below
        // still inspects the ORIGINAL `chunks` (language metadata is unaffected by that
        // swap), only the context block itself uses the resolved set.
        var contextBlock = await _contextAssembler.BuildAsync(chunks, responseMode, cancellationToken);

        // ── Step 4: Prompt Construction ───────────────────────────
        // The retrieved chunks decide which persona/rules fit the content: a repo
        // that's mostly Markdown/plain-text specs needs synthesis-across-chunks and
        // document/section citations, not code-line citations and fenced code blocks.
        var promptTemplate = SystemPromptComposer.SelectTemplate(chunks, responseMode);
        var systemPrompt = SystemPromptComposer.ComposeGrounded(
            promptTemplate, contextBlock, assessment.ConfidenceAddendum);

        // ── Step 5: Streaming Generation ─────────────────────────
        // ResponseMode.Simple with the sanitizer enabled cannot stream token-by-token:
        // the post-generation filter (SimpleAnswerSanitizer.Sanitize) needs the full text
        // to reliably match fences/identifiers that can open and close across separate
        // stream fragments. ResponseMode.Technical, and Simple with the rollback flag
        // off (Options.EnableSimpleModeSanitizer == false), keep the original
        // unbuffered per-fragment streaming untouched.
        // See docs/analisis-futuro/modo-respuesta-simple-codigo.md, Fase 1.
        if (responseMode == ResponseMode.Simple && Options.EnableSimpleModeSanitizer)
        {
            var buffered = new StringBuilder();
            await foreach (var fragment in _answerStreamer.StreamAsync(
                systemPrompt, query, history, cancellationToken))
            {
                buffered.Append(fragment);
            }

            if (onStatus is not null)
                await onStatus("Verificando formato...", cancellationToken);

            yield return SimpleAnswerSanitizer.Sanitize(buffered.ToString());
        }
        else
        {
            await foreach (var fragment in _answerStreamer.StreamAsync(
                systemPrompt, query, history, cancellationToken))
            {
                yield return fragment;
            }
        }
    }

    /// <summary>
    /// Non-iterator helper that wraps the retrieval call in a try/catch.
    /// Returns a tuple so the caller (an async iterator) never needs a catch block.
    /// </summary>
    private async Task<(bool Ok, IReadOnlyList<RetrievalResult>? Chunks, string? Error)>
        RetrieveChunksAsync(
            string query,
            string collectionName,
            int topK,
            float minimumScore,
            bool useReRanking,
            CancellationToken cancellationToken)
    {
        try
        {
            var options = new RetrievalOptions
            {
                CollectionName         = collectionName,
                TopK                   = topK,
                MinimumSimilarityScore = minimumScore,
                UseReRanking           = useReRanking
            };

            var chunks = await _retriever.SearchAsync(query, options, cancellationToken);
            return (true, chunks, null);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation — do not swallow it.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RAG] Retrieval failed for query: {Query}", query);
            return (false, null, "⚠️ An error occurred while searching the codebase. Please ensure Qdrant is running.");
        }
    }
}
