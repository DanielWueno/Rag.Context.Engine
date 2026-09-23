using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Diagnostics;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Generation;

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
    public async IAsyncEnumerable<GenerationEvent> AskStreamingAsync(
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(retrievalContext);
        cancellationToken.ThrowIfCancellationRequested();

        // ── Step 0: Meta-intent pre-filter ────────────────────────
        // Questions about the assistant itself ("who are you?", "what tech are
        // you built with?") never need retrieval or generation — answering them
        // from a fixed, factual block also sidesteps cases where they'd otherwise
        // score high by accidental lexical overlap with real corpus content.
        if (await _metaIntentDetector.IsMetaIntentAsync(query, cancellationToken))
        {
            _logger.LogInformation("[RAG] Meta-intent match for query: {Query}. Skipping retrieval.", query);
            // Ítem 13.4: los cinco pasos del turno no corren para una meta-pregunta —
            // se declara explícitamente en el span raíz (si el host abrió uno), en vez
            // de dejarlos ausentes sin explicación.
            Activity.Current.DeclareSkipped(RagEngineTracing.Steps.Retrieval, "meta_intent");
            Activity.Current.DeclareSkipped(RagEngineTracing.Steps.Gate, "meta_intent");
            Activity.Current.DeclareSkipped(RagEngineTracing.Steps.Context, "meta_intent");
            Activity.Current.DeclareSkipped(RagEngineTracing.Steps.Generation, "meta_intent");
            yield return new GenerationEvent.ContextReady([], GroundingVerdict.NotEvaluated);
            yield return new GenerationEvent.TextDelta(SystemPromptComposer.SelfDescriptionBlock);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new GenerationEvent.Completed(GenerationOutcome.Answered);
            yield break;
        }

        // ── Step 1: Semantic Retrieval ────────────────────────────
        _logger.LogInformation(
            "[RAG] Retrieving top-{TopK} chunks from '{Collection}' for query: {Query}",
            topK, collectionName, query);

        var chunks = await RetrieveChunksAsync(
            query, collectionName, retrievalContext, topK, minimumScore, useReRanking, cancellationToken);

        // ── Step 2: confidence gate ───────────────────────────────
        // Sin anclaje se conversa sin contexto y, por la garantía estructural de
        // docs/analisis-futuro/guardrail-banda-baja-conversacional.md, los chunks
        // recuperados (si los hay) no se vuelven a tocar por debajo de esta rama.
        GroundingAssessment assessment;
        using (var gateActivity = RagEngineTracing.ActivitySource.StartActivity(RagEngineTracing.Steps.Gate))
        {
            assessment = _confidenceGate.Assess(chunks, minimumScore, query);
            gateActivity?.SetTag("rag.has_grounding", assessment.HasGrounding);
            gateActivity?.SetTag("rag.verdict", assessment.Verdict.ToString());
        }
        yield return new GenerationEvent.ContextReady(
            assessment.HasGrounding ? chunks : [], assessment.Verdict);

        var answer = new StringBuilder();
        await foreach (var fragment in StreamAnswerAsync(
            query, chunks, assessment, responseMode, history, promptFamily, onStatus, cancellationToken))
        {
            answer.Append(fragment);
            yield return new GenerationEvent.TextDelta(fragment);
        }
        cancellationToken.ThrowIfCancellationRequested();
        yield return new GenerationEvent.Completed(
            answer.ToString().Trim() == Prompts.AnswerNotices.NoContextFallback
                ? GenerationOutcome.ModelDeclined : GenerationOutcome.Answered);
    }

    private async IAsyncEnumerable<string> StreamAnswerAsync(
        string query,
        IReadOnlyList<RetrievalResult> chunks,
        GroundingAssessment assessment,
        ResponseMode responseMode,
        IReadOnlyList<ChatTurn>? history,
        PromptFamily? promptFamily,
        Func<string, CancellationToken, Task>? onStatus,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!assessment.HasGrounding)
        {
            // Ítem 13.4: sin anclaje, el ensamblado de contexto (paso 3) nunca corre —
            // declarado explícitamente, no un span ausente sin explicación.
            Activity.Current.DeclareSkipped(RagEngineTracing.Steps.Context, "no_grounding");
            await foreach (var fragment in TraceGenerationAsync(
                _answerStreamer.StreamAsync(SystemPromptComposer.ComposeNoGrounding(), query, history, cancellationToken),
                hasGrounding: false, cancellationToken))
            {
                yield return fragment;
            }

            yield break;
        }

        _logger.LogInformation("[RAG] Retrieved {Count} chunks. Building context block.", chunks.Count);

        // ── Step 3: Context Assembly ──────────────────────────────
        // For ResponseMode.Simple, the assembler swaps each chunk's raw content for its
        // cached business summary when one exists (Fase 2) — see
        // docs/analisis-futuro/modo-respuesta-simple-codigo.md. SelectTemplate below
        // still inspects the ORIGINAL `chunks` (language metadata is unaffected by that
        // swap), only the context block itself uses the resolved set.
        string contextBlock;
        using (var contextActivity = RagEngineTracing.ActivitySource.StartActivity(RagEngineTracing.Steps.Context))
        {
            contextActivity?.SetTag("rag.chunk_count", chunks.Count);
            contextActivity?.SetTag("rag.response_mode", responseMode.ToString());
            try
            {
                contextBlock = await _contextAssembler.BuildAsync(chunks, responseMode, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                contextActivity?.SetStatus(ActivityStatusCode.Error, "cancelled");
                throw;
            }
            catch (Exception ex)
            {
                contextActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        // ── Step 4: Prompt Construction ───────────────────────────
        // The retrieved chunks decide which persona/rules fit the content: a repo
        // that's mostly Markdown/plain-text specs needs synthesis-across-chunks and
        // document/section citations, not code-line citations and fenced code blocks.
        var promptTemplate = SystemPromptComposer.SelectTemplate(chunks, responseMode, promptFamily);
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
            using (var genActivity = RagEngineTracing.ActivitySource.StartActivity(RagEngineTracing.Steps.Generation))
            {
                genActivity?.SetTag("rag.has_grounding", true);
                genActivity?.SetTag("rag.buffered", true);
                try
                {
                    await foreach (var fragment in _answerStreamer.StreamAsync(
                        systemPrompt, query, history, cancellationToken))
                    {
                        buffered.Append(fragment);
                    }
                }
                catch (OperationCanceledException)
                {
                    genActivity?.SetStatus(ActivityStatusCode.Error, "cancelled");
                    throw;
                }
                catch (Exception ex)
                {
                    genActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    throw;
                }
            }

            if (onStatus is not null)
                await onStatus("Verificando formato...", cancellationToken);

            yield return SimpleAnswerSanitizer.Sanitize(buffered.ToString());
        }
        else
        {
            await foreach (var fragment in TraceGenerationAsync(
                _answerStreamer.StreamAsync(systemPrompt, query, history, cancellationToken),
                hasGrounding: true, cancellationToken))
            {
                yield return fragment;
            }
        }
    }

    /// <summary>
    /// Envuelve un stream de fragmentos con el span "rag.generation" (ítem 13.4). Un
    /// método aparte porque un <c>yield return</c> no puede vivir dentro de un
    /// try-catch (sólo dentro de un try-finally) — aquí el catch que marca el span
    /// como fallido/cancelado envuelve solo <c>MoveNextAsync</c>, nunca el yield.
    /// </summary>
    private async IAsyncEnumerable<string> TraceGenerationAsync(
        IAsyncEnumerable<string> source,
        bool hasGrounding,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = RagEngineTracing.ActivitySource.StartActivity(RagEngineTracing.Steps.Generation);
        activity?.SetTag("rag.has_grounding", hasGrounding);
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
                    throw;
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    throw;
                }

                if (!moved)
                    break;

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>
    /// Logs retrieval failures without converting them into successful answers.
    /// </summary>
    private async Task<IReadOnlyList<RetrievalResult>>
        RetrieveChunksAsync(
            string query,
            string collectionName,
            RetrievalContext retrievalContext,
            int topK,
            float minimumScore,
            bool useReRanking,
            CancellationToken cancellationToken)
    {
        try
        {
            var options = new RetrievalOptions
            {
                Context                = retrievalContext,
                CollectionName         = collectionName,
                TopK                   = topK,
                MinimumSimilarityScore = minimumScore,
                UseReRanking           = useReRanking
            };

            return await _retriever.SearchAsync(query, options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation — do not swallow it.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RAG] Retrieval failed for query: {Query}", query);
            throw;
        }
    }
}
