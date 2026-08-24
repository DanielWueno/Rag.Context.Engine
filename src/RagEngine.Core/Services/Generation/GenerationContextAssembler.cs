using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagEngine.Core.Domain;
using RagEngine.Core.Services.Summary;
using RagEngine.Core.Utilities;

namespace RagEngine.Core.Services.Generation;

/// <summary>
/// Convierte los chunks recuperados en el bloque de contexto que se inyecta en el
/// prompt: decide QUÉ contenido lleva cada chunk (código crudo o resumen de negocio)
/// y CÓMO se serializa (cabecera estructural + presupuesto de caracteres).
///
/// Separado de <see cref="RagGenerationService"/> por el ítem 2.2 del plan. La razón
/// concreta: esta es la pieza que consulta <see cref="SummaryCache"/> y la única que
/// conoce el presupuesto de contexto, y estaba enredada con la orquestación del
/// streaming, que no comparte ninguna de las dos cosas.
/// </summary>
internal sealed class GenerationContextAssembler
{
    private readonly SummaryCache _summaryCache;
    private readonly IOptionsMonitor<RagGenerationOptions> _optionsMonitor;
    private readonly ILogger<GenerationContextAssembler> _logger;

    private RagGenerationOptions Options => _optionsMonitor.CurrentValue;

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

    public GenerationContextAssembler(
        SummaryCache summaryCache,
        IOptionsMonitor<RagGenerationOptions> optionsMonitor,
        ILogger<GenerationContextAssembler> logger)
    {
        _summaryCache   = summaryCache   ?? throw new ArgumentNullException(nameof(summaryCache));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger         = logger         ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Resuelve el contenido de cada chunk según el modo y lo serializa en el bloque
    /// de contexto. Devuelve sólo el texto: el llamador conserva la lista ORIGINAL de
    /// chunks para decidir la plantilla (los metadatos de lenguaje no cambian con el
    /// intercambio por resumen).
    /// </summary>
    public async Task<string> BuildAsync(
        IReadOnlyList<RetrievalResult> chunks,
        ResponseMode responseMode,
        CancellationToken cancellationToken)
    {
        var contextChunks = await ResolveContextChunksAsync(chunks, responseMode, cancellationToken);
        return BuildContextBlock(contextChunks);
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
    ///
    /// <c>internal static</c> y no un detalle privado: es una función pura, y el golden
    /// de <c>GenerationContextGoldenTests</c> la fija byte a byte contra la salida
    /// medida ANTES de la descomposición del ítem 2.2.
    /// </summary>
    internal static string BuildContextBlock(IReadOnlyList<RetrievalResult> results)
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
            var label = m.Language.IsDocumentation() ? "Section" : "Method";
            sb.AppendLine($"{label,-11}: {m.MethodName}");
        }

        return sb.ToString().TrimEnd();
    }
}
