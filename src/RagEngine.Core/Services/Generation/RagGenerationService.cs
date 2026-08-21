using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Services.Summary;
using RagEngine.Core.Utilities;

namespace RagEngine.Core.Services.Generation;

/// <summary>
/// Implements the full Retrieval-Augmented Generation pipeline using Semantic Kernel.
///
/// Flow:
///   1. Semantic Retrieval  — QdrantSemanticRetriever finds the top-K relevant code chunks.
///   2. Context Assembly    — Chunks are ranked by score and concatenated into a single
///                            context block, each chunk prefixed with its structural header.
///   3. Prompt Construction — A strict system prompt anchors the LLM to the retrieved context
///                            only, preventing hallucinated answers.
///   4. Streaming Generation — The Kernel streams the response token-by-token via
///                             InvokePromptStreamingAsync, and each fragment is yielded
///                             immediately to the caller.
///
/// Thread-safety: stateless; safe to register as a singleton.
/// </summary>
public sealed class RagGenerationService : IRagGenerationService
{
    // ──────────────────────────────────────────────────────────────
    //  Dependencies
    // ──────────────────────────────────────────────────────────────

    private readonly Kernel _kernel;
    private readonly ISemanticRetriever _retriever;
    private readonly ILogger<RagGenerationService> _logger;
    private readonly IOptionsMonitor<RagGenerationOptions> _optionsMonitor;
    private readonly IMetaIntentDetector _metaIntentDetector;
    private readonly SummaryCache _summaryCache;

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
    //  Configuration constants
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Approximate character limit for the assembled context block.
    /// Prevents excessively large prompts when TopK is high or chunks are verbose.
    /// Qwen2.5-Coder-7B handles ~8 k tokens comfortably; 12 000 chars ≈ 3 000 tokens.
    /// </summary>
    private const int MaxContextCharacters = 12_000;

    /// <summary>
    /// Separator rendered between each retrieved chunk inside the context block.
    /// </summary>
    private const string ChunkSeparator = "\n\n---\n\n";

    // ──────────────────────────────────────────────────────────────
    //  System prompt (injected once per conversation turn)
    // ──────────────────────────────────────────────────────────────

    /// <summary>Alias de <see cref="Prompts.AnswerNotices.NoContextFallback"/>. El texto vive en Prompts/.</summary>
    public const string NoContextFallbackMessage = Prompts.AnswerNotices.NoContextFallback;

    /// <summary>Alias de <see cref="Prompts.CodeSystemPrompt.Template"/>. El texto vive en Prompts/.</summary>
    private const string CodeSystemPromptTemplate = Prompts.CodeSystemPrompt.Template;

    /// <summary>Alias de <see cref="Prompts.DocsSystemPrompt.Template"/>. El texto vive en Prompts/.</summary>
    private const string DocsSystemPromptTemplate = Prompts.DocsSystemPrompt.Template;

    /// <summary>Alias de <see cref="Prompts.SimpleSystemPrompt.Template"/>. El texto vive en Prompts/.</summary>
    private const string SimpleSystemPromptTemplate = Prompts.SimpleSystemPrompt.Template;

    /// <summary>Alias de <see cref="Prompts.LowConfidencePrompt.Addendum"/>. El texto vive en Prompts/.</summary>
    private const string LowConfidenceAddendum = Prompts.LowConfidencePrompt.Addendum;

    /// <summary>Alias de <see cref="Prompts.SelfDescription.Block"/>. El texto vive en Prompts/.</summary>
    private const string SelfDescriptionBlock = Prompts.SelfDescription.Block;

    /// <summary>Alias de <see cref="Prompts.NoGroundingSystemPrompt.Template"/>. El texto vive en Prompts/.</summary>
    private const string NoGroundingSystemPromptTemplate = Prompts.NoGroundingSystemPrompt.Template;

    // ──────────────────────────────────────────────────────────────
    //  Constructor
    // ──────────────────────────────────────────────────────────────

    /// <param name="kernel">
    ///   Fully configured Semantic Kernel instance with an OpenAI-compatible
    ///   chat-completion service registered (e.g., Ollama/Qwen2.5-Coder).
    /// </param>
    /// <param name="retriever">
    ///   Abstraction over Qdrant that returns ranked <see cref="RetrievalResult"/> objects.
    /// </param>
    /// <param name="logger">Structured logger injected by the DI container.</param>
    /// <param name="optionsMonitor">
    ///   Confidence-gate thresholds and the Simple-mode sanitizer rollback flag,
    ///   bound from configuration. <see cref="IOptionsMonitor{TOptions}"/> instead
    ///   of <c>IOptions&lt;T&gt;</c> so a config/env-var change takes effect on a
    ///   restart without a rebuild — see <see cref="Options"/>.
    /// </param>
    /// <param name="metaIntentDetector">Detects meta-questions about the assistant itself.</param>
    /// <param name="summaryCache">
    ///   Shared SQLite cache of ingestion-time business summaries, keyed by
    ///   <see cref="RetrievalResult.ContentHash"/>. Used only for
    ///   <see cref="ResponseMode.Simple"/> — see
    ///   <see cref="ResolveContextChunksAsync"/> and
    ///   docs/analisis-futuro/modo-respuesta-simple-codigo.md, Fase 2.
    /// </param>
    public RagGenerationService(
        Kernel kernel,
        ISemanticRetriever retriever,
        ILogger<RagGenerationService> logger,
        IOptionsMonitor<RagGenerationOptions> optionsMonitor,
        IMetaIntentDetector metaIntentDetector,
        SummaryCache summaryCache)
    {
        _kernel             = kernel             ?? throw new ArgumentNullException(nameof(kernel));
        _retriever          = retriever          ?? throw new ArgumentNullException(nameof(retriever));
        _logger             = logger             ?? throw new ArgumentNullException(nameof(logger));
        _optionsMonitor     = optionsMonitor      ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _metaIntentDetector = metaIntentDetector ?? throw new ArgumentNullException(nameof(metaIntentDetector));
        _summaryCache       = summaryCache       ?? throw new ArgumentNullException(nameof(summaryCache));
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
            yield return SelfDescriptionBlock;
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

        // ── Step 1b: unified low-grounding gate ───────────────────
        // Two situations both mean "no trustworthy domain grounding for this
        // query" and must take the exact same path: zero chunks retrieved at all,
        // or (only meaningful when useReRanking is true, since SimilarityScore is
        // then the cross-encoder sigmoid comparable across queries — without
        // rerank it's the RRF fusion score, a function of rank, not similarity,
        // and is not evaluated against this threshold) a top score below
        // LowConfidenceThreshold. See
        // docs/analisis-futuro/guardrail-banda-baja-conversacional.md — this used
        // to be two separate checks that both hard-cut to the same fixed message;
        // now both route to NoGroundingSystemPromptTemplate instead, and, per the
        // structural guarantee described there, the retrieved chunks (if any) are
        // never touched again below this branch.
        var topScore = chunks!.Count > 0 ? chunks[0].SimilarityScore : 0f;
        var noGrounding = chunks.Count == 0 || (useReRanking && topScore < Options.LowConfidenceThreshold);

        if (noGrounding)
        {
            if (chunks.Count == 0)
            {
                _logger.LogWarning("[RAG] No chunks found above score threshold {Score}.", minimumScore);
            }
            else
            {
                _logger.LogWarning(
                    "[RAG] Low confidence ({Score:F3} < {Threshold:F3}) for query: {Query}. Falling back to no-grounding conversation.",
                    topScore, Options.LowConfidenceThreshold, query);
            }

            var noGroundingPrompt = string.Format(NoGroundingSystemPromptTemplate, SelfDescriptionBlock);

            await foreach (var fragment in StreamAnswerAsync(noGroundingPrompt, query, history, cancellationToken))
            {
                yield return fragment;
            }

            yield break;
        }

        // ── Step 1c: mid-band hedge ────────────────────────────────
        string? confidenceAddendum = null;
        if (useReRanking && topScore < Options.HighConfidenceThreshold)
        {
            _logger.LogInformation(
                "[RAG] Mid confidence ({Score:F3} < {Threshold:F3}) for query: {Query}. Answering with a low-confidence hedge.",
                topScore, Options.HighConfidenceThreshold, query);
            confidenceAddendum = LowConfidenceAddendum;
        }

        _logger.LogInformation("[RAG] Retrieved {Count} chunks. Building context block.", chunks.Count);

        // ── Step 2: Context Assembly ──────────────────────────────
        // For ResponseMode.Simple, swap each chunk's raw content for its cached
        // business summary when one exists (Fase 2) — see
        // docs/analisis-futuro/modo-respuesta-simple-codigo.md. SelectSystemPromptTemplate
        // below still inspects the ORIGINAL `chunks` (language metadata is unaffected by
        // this swap), only the context block itself uses the resolved set.
        var contextChunks = await ResolveContextChunksAsync(chunks, responseMode, cancellationToken);
        var contextBlock = BuildContextBlock(contextChunks);

        // ── Step 3: Prompt Construction ───────────────────────────
        // The retrieved chunks decide which persona/rules fit the content: a repo
        // that's mostly Markdown/plain-text specs needs synthesis-across-chunks and
        // document/section citations, not code-line citations and fenced code blocks.
        var promptTemplate = SelectSystemPromptTemplate(chunks, responseMode);
        var systemPrompt = string.Format(promptTemplate, contextBlock, NoContextFallbackMessage);
        if (confidenceAddendum is not null)
            systemPrompt += confidenceAddendum;

        // ── Step 4: Streaming Generation ─────────────────────────
        // ResponseMode.Simple with the sanitizer enabled cannot stream token-by-token:
        // the post-generation filter (SanitizeSimpleAnswer) needs the full text to
        // reliably match fences/identifiers that can open and close across separate
        // stream fragments. ResponseMode.Technical, and Simple with the rollback flag
        // off (Options.EnableSimpleModeSanitizer == false), keep the original
        // unbuffered per-fragment streaming untouched.
        // See docs/analisis-futuro/modo-respuesta-simple-codigo.md, Fase 1.
        if (responseMode == ResponseMode.Simple && Options.EnableSimpleModeSanitizer)
        {
            var buffered = new StringBuilder();
            await foreach (var fragment in StreamAnswerAsync(systemPrompt, query, history, cancellationToken))
            {
                buffered.Append(fragment);
            }

            if (onStatus is not null)
                await onStatus("Verificando formato...", cancellationToken);

            yield return SanitizeSimpleAnswer(buffered.ToString());
        }
        else
        {
            await foreach (var fragment in StreamAnswerAsync(systemPrompt, query, history, cancellationToken))
            {
                yield return fragment;
            }
        }
    }

    /// <summary>
    /// Builds the ChatHistory (system prompt + prior turns + new query) and streams
    /// the LLM's response. Shared by every path in <see cref="AskStreamingAsync"/>
    /// that reaches generation — the grounded path (Code/Docs template + retrieved
    /// context) and the no-grounding path (<see cref="NoGroundingSystemPromptTemplate"/>,
    /// no retrieved context) differ only in which system prompt they pass in.
    /// </summary>
    private async IAsyncEnumerable<string> StreamAnswerAsync(
        string systemPrompt,
        string query,
        IReadOnlyList<ChatTurn>? history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Build a ChatHistory so the system prompt is correctly separated
        // from the conversation turns — Semantic Kernel respects this structure.
        // Prior turns (if any) come from the caller on every request — this service
        // is stateless and keeps no session, so retrieval only ever searched
        // the latest `query`, never the older turns.
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(systemPrompt);

        if (history is not null)
        {
            foreach (var turn in history)
            {
                if (turn.Role == ChatRole.User)
                    chatHistory.AddUserMessage(turn.Content);
                else
                    chatHistory.AddAssistantMessage(turn.Content);
            }
        }

        chatHistory.AddUserMessage(query);

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();

        var executionSettings = new PromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["temperature"] = 0.1,
                ["top_p"]       = 0.95,
                ["max_tokens"]  = 2048
            }
        };

        _logger.LogInformation("[RAG] Streaming LLM response for query: {Query}", query);

        await foreach (var streamChunk in chatService
            .GetStreamingChatMessageContentsAsync(
                chatHistory,
                executionSettings,
                _kernel,
                cancellationToken))
        {
            if (!string.IsNullOrEmpty(streamChunk.Content))
            {
                yield return streamChunk.Content;
            }
        }

        _logger.LogInformation("[RAG] Streaming complete for query: {Query}", query);
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

    // ──────────────────────────────────────────────────────────────
    //  Simple-mode post-generation sanitizer (Fase 1 — structural safety net)
    // ──────────────────────────────────────────────────────────────

    /// <summary>Alias de <see cref="Prompts.AnswerNotices.CodeBlockOmitted"/>. El texto vive en Prompts/.</summary>
    private const string CodeBlockOmittedNotice = Prompts.AnswerNotices.CodeBlockOmitted;

    /// <summary>Alias de <see cref="Prompts.AnswerNotices.IdentifierOmitted"/>. El texto vive en Prompts/.</summary>
    private const string IdentifierOmittedNotice = Prompts.AnswerNotices.IdentifierOmitted;

    /// <summary>Matches a complete ``` fenced block, capturing its body (group 1).</summary>
    private static readonly Regex FencedCodeBlockPattern = new(
        @"```[^\n]*\n?([\s\S]*?)```", RegexOptions.Compiled);

    /// <summary>Matches a single-line inline code span, e.g. `` `GenerarPlanAuditoria` ``. Captures the inner text (group 1).</summary>
    private static readonly Regex InlineCodeSpanPattern = new(
        @"`([^`\n]+)`", RegexOptions.Compiled);

    /// <summary>
    /// An inline-code span whose inner text is ONLY identifier characters (letters,
    /// digits, underscore, dot) — no spaces, parentheses, or operators. This is the
    /// shape a resumen or the model itself produces when it backticks a single field
    /// or method name as an aside (e.g. `` `Provisionada` ``, `` `IsCancelable` ``)
    /// rather than an actual code snippet — safe to humanize instead of blacking out,
    /// since there is no risk of leaking a real expression/statement.
    /// </summary>
    private static readonly Regex SimpleIdentifierShapePattern = new(
        @"^[A-Za-z0-9_.]+$", RegexOptions.Compiled);

    /// <summary>
    /// Zero-width split point right after a lowercase letter/digit and right before
    /// an uppercase letter — used to "de-camelcase" an identifier into space-separated
    /// words. Deliberately simple (no ALLCAPS-acronym handling) — good enough for this
    /// codebase's naming convention, where compound identifiers are Spanish/English
    /// words concatenated in PascalCase (`GenerarPlanAuditoria` → "generar plan
    /// auditoria"), not technical acronyms.
    /// </summary>
    private static readonly Regex CamelBoundaryPattern = new(
        @"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    /// <summary>Matches a declarative-attribute decoration, e.g. `[SupportedEstatus(...)]`.</summary>
    private static readonly Regex AttributeDecorationPattern = new(
        @"\[[A-Z][A-Za-z0-9]*\([^\]\n]*\)\]", RegexOptions.Compiled);

    /// <summary>
    /// Matches a dotted PascalCase chain, e.g. `TipoEstatus.Completado` or
    /// `ServicioCliente.GenerarPlanAuditoria` — this shape almost never occurs in
    /// legitimate Spanish/English prose, so it is a low-false-positive signal.
    /// </summary>
    private static readonly Regex DottedIdentifierPattern = new(
        @"\b[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*){1,}\b", RegexOptions.Compiled);

    /// <summary>Matches a snake_case identifier — not a natural-language shape in Spanish or English prose.</summary>
    private static readonly Regex SnakeCaseIdentifierPattern = new(
        @"\b[a-z][a-z0-9]*(?:_[a-z0-9]+){1,}\b", RegexOptions.Compiled);

    /// <summary>
    /// Matches a bare identifier with two or more capitalized "humps" smashed
    /// together with no separators, e.g. `GenerarPlanAuditoria` or
    /// `ServicioCliente`. Deliberately conservative: a single capitalized word
    /// (a legitimate proper noun, e.g. "Auditoria") never matches — only tokens
    /// that already look like `PascalCaseCompoundWords` do, which keeps ordinary
    /// prose with capitalized proper nouns untouched.
    /// </summary>
    private static readonly Regex CamelHumpIdentifierPattern = new(
        @"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*){1,}\b", RegexOptions.Compiled);

    /// <summary>
    /// Deterministic post-generation filter for <see cref="ResponseMode.Simple"/> —
    /// the structural safety net from
    /// docs/analisis-futuro/modo-respuesta-simple-codigo.md, Fase 1: regardless of
    /// whether the model followed the prompt's plain-language rules, this strips
    /// anything that still looks like source code before the answer ever reaches
    /// the caller. Order matters — fenced blocks and inline spans are removed
    /// first (they can contain identifier shapes that would otherwise get
    /// double-redacted), then the remaining bare-text heuristics run against
    /// what's left.
    ///
    /// Word-shaped identifiers (dotted/snake_case/CamelHump) are "de-camelcased"
    /// into space-separated lowercase words instead of blacked out — a real user
    /// complaint against the original all-opaque placeholder was that it destroyed
    /// even the little inferential value a raw identifier gave a reader (see
    /// docs/analisis-futuro/modo-respuesta-simple-codigo.md, Fase 2 follow-up). This
    /// is NOT a semantic explanation (it doesn't know what the field MEANS, only
    /// decodes its name) — rule 3 of SimpleSystemPromptTemplate is the real fix
    /// (tell the model to explain the concept instead of naming it); this is the
    /// fallback for when that instruction isn't followed. Attribute decorations and
    /// fenced code blocks have no natural-language reading and stay fully opaque.
    /// </summary>
    internal static string SanitizeSimpleAnswer(string answer)
    {
        if (string.IsNullOrEmpty(answer))
            return answer;

        var sanitized = FencedCodeBlockPattern.Replace(answer, match =>
            string.IsNullOrWhiteSpace(match.Groups[1].Value) ? string.Empty : CodeBlockOmittedNotice);

        sanitized = InlineCodeSpanPattern.Replace(sanitized, match =>
        {
            var inner = match.Groups[1].Value;
            return SimpleIdentifierShapePattern.IsMatch(inner)
                ? HumanizeIdentifier(inner)
                : IdentifierOmittedNotice;
        });
        sanitized = AttributeDecorationPattern.Replace(sanitized, IdentifierOmittedNotice);
        sanitized = DottedIdentifierPattern.Replace(sanitized, match => HumanizeIdentifier(match.Value));
        sanitized = SnakeCaseIdentifierPattern.Replace(sanitized, match => HumanizeIdentifier(match.Value));
        sanitized = CamelHumpIdentifierPattern.Replace(sanitized, match => HumanizeIdentifier(match.Value));

        return sanitized;
    }

    /// <summary>
    /// De-camelcases a dotted/snake_case/PascalCase identifier into space-separated
    /// lowercase words (`GenerarPlanAuditoria` → "generar plan auditoria",
    /// `TipoEstatus.Completado` → "tipo estatus completado"). See
    /// <see cref="SanitizeSimpleAnswer"/> for why this replaces outright redaction.
    /// </summary>
    private static string HumanizeIdentifier(string token)
    {
        var segments = token.Split('.', '_');
        var words = segments.SelectMany(seg => CamelBoundaryPattern.Split(seg));
        return string.Join(" ", words).ToLowerInvariant();
    }

    // ──────────────────────────────────────────────────────────────
    //  Private helpers
    // ──────────────────────────────────────────────────────────────

    /// <summary>Languages that represent prose documentation rather than source code.</summary>
    private static bool IsDocumentationLanguage(SourceLanguage language) =>
        language is SourceLanguage.Markdown or SourceLanguage.PlainText;

    /// <summary>
    /// Picks the system prompt for this turn. <see cref="ResponseMode.Simple"/> always
    /// wins regardless of content — the plain-language template replaces the
    /// code/docs split entirely. Only for <see cref="ResponseMode.Technical"/> does the
    /// kind of content dominating the retrieved chunks decide code-oriented vs
    /// docs-oriented: a simple majority is enough, since mixed repositories (e.g. a few
    /// README hits alongside mostly code) should still get the code prompt.
    /// </summary>
    private static string SelectSystemPromptTemplate(IReadOnlyList<RetrievalResult> chunks, ResponseMode responseMode)
    {
        if (responseMode == ResponseMode.Simple)
            return SimpleSystemPromptTemplate;

        var docChunks = chunks.Count(c => IsDocumentationLanguage(c.Metadata.Language));
        return docChunks * 2 >= chunks.Count ? DocsSystemPromptTemplate : CodeSystemPromptTemplate;
    }

    /// <summary>
    /// Fase 2 de docs/analisis-futuro/modo-respuesta-simple-codigo.md. For
    /// <see cref="ResponseMode.Technical"/> (or when the rollback flag is off), returns
    /// <paramref name="chunks"/> unchanged — no cache lookups, no added latency. For
    /// <see cref="ResponseMode.Simple"/>, delegates to
    /// <see cref="ResolveSimpleModeContextChunksAsync"/> to swap in cached business
    /// summaries where available.
    /// </summary>
    private Task<IReadOnlyList<RetrievalResult>> ResolveContextChunksAsync(
        IReadOnlyList<RetrievalResult> chunks,
        ResponseMode responseMode,
        CancellationToken cancellationToken)
    {
        if (responseMode != ResponseMode.Simple || !Options.EnableSimpleModeResumenContext)
            return Task.FromResult(chunks);

        return ResolveSimpleModeContextChunksAsync(chunks, cancellationToken);
    }

    /// <summary>
    /// Looks up each chunk's cached business summary (<see cref="SummaryCache"/>, keyed by
    /// <see cref="RetrievalResult.ContentHash"/>) and, when found, substitutes it for the
    /// chunk's raw <see cref="RetrievalResult.Content"/> — the LLM never sees the source
    /// code for that chunk, only its business-language description (structural guarantee,
    /// not just a prompt instruction; the Fase 1 sanitizer still runs as a safety net on
    /// top of this).
    ///
    /// Fallback for chunks with NO cached summary is decided per-request from the
    /// fraction of chunks in THIS retrieval that do have one — not from a fixed
    /// per-collection list, which would silently go stale the day a collection gains
    /// <c>--con-resumen</c> coverage:
    ///   - Coverage ≥ <see cref="RagGenerationOptions.SimpleModeResumenCoverageThreshold"/>:
    ///     exclude the uncovered chunks from context entirely — little is lost, and the
    ///     context stays fully code-free.
    ///   - Coverage below threshold: keep the uncovered chunks' raw content instead of
    ///     emptying the context over what may just be circumstantial low coverage for
    ///     this collection today (see the wiki-solis/rag-engine measurement in the plan
    ///     doc — both are at 0% today). The Fase 1 filter still cleans these on the way out.
    /// </summary>
    private async Task<IReadOnlyList<RetrievalResult>> ResolveSimpleModeContextChunksAsync(
        IReadOnlyList<RetrievalResult> chunks,
        CancellationToken cancellationToken)
    {
        var summaries = new string?[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            var (found, summary) = await _summaryCache.TryGetAsync(chunks[i].ContentHash, cancellationToken);
            summaries[i] = found ? SummaryTextUtilities.StripEntityPrefix(summary) : null;
        }

        var coveredCount = summaries.Count(s => s is not null);
        var coverage = (float)coveredCount / chunks.Count;
        // Guard: only exclude when there is at least one covered chunk to fall back on —
        // otherwise a misconfigured threshold of 0 would empty the context entirely.
        var excludeUncovered = coveredCount > 0 && coverage >= Options.SimpleModeResumenCoverageThreshold;

        _logger.LogInformation(
            "[RAG] Simple-mode resumen coverage {Coverage:P0} ({Covered}/{Total}) — {Strategy} chunks without a cached summary.",
            coverage, coveredCount, chunks.Count,
            excludeUncovered ? "excluding" : "degrading to raw content for");

        var resolved = new List<RetrievalResult>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (summaries[i] is { } summary)
                resolved.Add(chunks[i] with { Content = summary });
            else if (!excludeUncovered)
                resolved.Add(chunks[i]);
        }

        return resolved;
    }

    /// <summary>
    /// Iterates the ranked results and builds the context string injected into the prompt.
    /// Each chunk is prefixed with a structural header so the LLM knows its origin.
    /// Truncates the total context to <see cref="MaxContextCharacters"/> to avoid
    /// exceeding the model's context window.
    /// </summary>
    private static string BuildContextBlock(IReadOnlyList<RetrievalResult> results)
    {
        var sb = new StringBuilder(MaxContextCharacters);
        var index = 1;

        foreach (var result in results)
        {
            // Build a rich header from CodeChunkMetadata so the LLM can cite sources.
            var header = BuildChunkHeader(result, index++);
            var entry  = $"{header}\n{result.Content}";

            // Guard: skip chunks that don't fit in the remaining budget, but keep
            // trying with the following (smaller) ones. Un solo chunk gigante en
            // medio del ranking no debe truncar todos los que vienen después.
            if (sb.Length + entry.Length + ChunkSeparator.Length > MaxContextCharacters)
                continue;

            if (sb.Length > 0)
                sb.Append(ChunkSeparator);

            sb.Append(entry);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Produces a human-readable header for a single chunk that is both
    /// informative for the LLM and citable in its response.
    /// </summary>
    /// <example>
    /// [Chunk #1 | Score: 0.91]
    /// Repository : MyApp
    /// File       : src/Services/OrderService.cs (lines 42–78)
    /// Namespace  : MyApp.Services
    /// Class      : OrderService
    /// Method     : GetOrderAsync
    /// </example>
    private static string BuildChunkHeader(RetrievalResult result, int index)
    {
        var m  = result.Metadata;
        var sb = new StringBuilder();

        sb.AppendLine($"[Chunk #{index} | Score: {result.SimilarityScore:F2}]");
        sb.AppendLine($"Repository : {m.RepositoryName}");
        sb.AppendLine($"File       : {m.RelativeFilePath} (lines {m.StartLine}–{m.EndLine})");

        if (!string.IsNullOrWhiteSpace(m.Namespace))
            sb.AppendLine($"Namespace  : {m.Namespace}");

        if (!string.IsNullOrWhiteSpace(m.ClassName))
            sb.AppendLine($"Class      : {m.ClassName}");

        if (!string.IsNullOrWhiteSpace(m.MethodName))
        {
            // Markdown/plain-text chunks store the enclosing heading (e.g. "US-17.2 —
            // Finalización automática...") in MethodName; "Section" reads correctly
            // there and matches the docs prompt's citation instruction, whereas
            // "Method" only makes sense for source code.
            var label = IsDocumentationLanguage(m.Language) ? "Section" : "Method";
            sb.AppendLine($"{label,-11}: {m.MethodName}");
        }

        return sb.ToString().TrimEnd();
    }
}
