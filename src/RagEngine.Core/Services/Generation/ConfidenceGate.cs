using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Services.Generation;

/// <summary>
/// Veredicto del gate para un turno: si hay anclaje suficiente para responder con el
/// contexto recuperado, y si la respuesta debe llevar el matiz de confianza media.
/// </summary>
/// <param name="HasGrounding">
///   False cuando no hay anclaje fiable — ni chunks, o un score por debajo del umbral
///   bajo. El llamador debe ir al camino conversacional sin contexto y no volver a
///   tocar los chunks recuperados (garantía estructural de
///   docs/analisis-futuro/guardrail-banda-baja-conversacional.md).
/// </param>
/// <param name="ConfidenceAddendum">
///   Texto a añadir al prompt cuando el score cae en la banda media, o null.
///   Sólo tiene sentido si <paramref name="HasGrounding"/> es true.
/// </param>
internal readonly record struct GroundingAssessment(bool HasGrounding, string? ConfidenceAddendum);

/// <summary>
/// Decide, a partir del score del mejor chunk, en qué banda de confianza cae el turno.
///
/// Separado de <see cref="RagGenerationService"/> por el ítem 2.2 del plan, y es la
/// separación que más se va a cobrar: los ítems 4.2 y 4.3 recalibran precisamente estos
/// umbrales, y hasta ahora la regla estaba entretejida con el cuerpo de un iterador
/// asíncrono, donde no se puede probar sin levantar retrieval y kernel.
/// </summary>
internal sealed class ConfidenceGate
{
    private readonly IOptionsMonitor<RagGenerationOptions> _optionsMonitor;
    private readonly ILogger<ConfidenceGate> _logger;

    private RagGenerationOptions Options => _optionsMonitor.CurrentValue;

    public ConfidenceGate(
        IOptionsMonitor<RagGenerationOptions> optionsMonitor,
        ILogger<ConfidenceGate> logger)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger         = logger         ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Unified low-grounding gate. Two situations both mean "no trustworthy domain
    /// grounding for this query" and must take the exact same path: zero chunks
    /// retrieved at all, or (only meaningful when <paramref name="useReRanking"/> is
    /// true, since SimilarityScore is then the cross-encoder sigmoid comparable across
    /// queries — without rerank it's the RRF fusion score, a function of rank, not
    /// similarity, and is not evaluated against this threshold) a top score below
    /// <see cref="RagGenerationOptions.LowConfidenceThreshold"/>. See
    /// docs/analisis-futuro/guardrail-banda-baja-conversacional.md — this used to be two
    /// separate checks that both hard-cut to the same fixed message; now both route to
    /// the no-grounding conversation instead.
    /// </summary>
    /// <param name="minimumScore">
    ///   Sólo para el mensaje de log del caso "cero chunks": es el umbral que aplicó el
    ///   retriever, no uno que este gate evalúe.
    /// </param>
    public GroundingAssessment Assess(
        IReadOnlyList<RetrievalResult> chunks,
        bool useReRanking,
        float minimumScore,
        string query)
    {
        var topScore = chunks.Count > 0 ? chunks[0].SimilarityScore : 0f;
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

            return new GroundingAssessment(HasGrounding: false, ConfidenceAddendum: null);
        }

        // ── Mid-band hedge ────────────────────────────────────────
        string? confidenceAddendum = null;
        if (useReRanking && topScore < Options.HighConfidenceThreshold)
        {
            _logger.LogInformation(
                "[RAG] Mid confidence ({Score:F3} < {Threshold:F3}) for query: {Query}. Answering with a low-confidence hedge.",
                topScore, Options.HighConfidenceThreshold, query);
            confidenceAddendum = SystemPromptComposer.LowConfidenceAddendum;
        }

        return new GroundingAssessment(HasGrounding: true, ConfidenceAddendum: confidenceAddendum);
    }
}
