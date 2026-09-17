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
internal readonly record struct GroundingAssessment(bool HasGrounding, string? ConfidenceAddendum)
{
    public GroundingVerdict Verdict => !HasGrounding ? GroundingVerdict.Ungrounded
        : ConfidenceAddendum is not null ? GroundingVerdict.Medium : GroundingVerdict.High;
}

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
    /// retrieved at all, or a top score below
    /// <see cref="RagGenerationOptions.LowConfidenceThreshold"/>. See
    /// docs/analisis-futuro/guardrail-banda-baja-conversacional.md — this used to be two
    /// separate checks that both hard-cut to the same fixed message; now both route to
    /// the no-grounding conversation instead.
    ///
    /// <para>El umbral sólo se evalúa cuando el score está en una escala comparable entre
    /// consultas, y eso lo dice ahora el propio resultado
    /// (<see cref="RetrievalResult.ScoreScale"/>, ítem 4.9). Antes se aproximaba con un
    /// parámetro <c>useReRanking</c> que el llamador tenía que acertar: era el retriever
    /// quien decidía la escala y el gate quien la adivinaba desde otra capa. Con la
    /// escala en el dato, un score RRF —función del rango, no de la similitud— no puede
    /// caer por accidente contra un umbral calibrado sobre sigmoides del
    /// cross-encoder.</para>
    ///
    /// <para>Lee <c>chunks[0]</c> a propósito y sin reordenar: la lista que entrega el
    /// reranker no está ordenada monótonamente por score (ver
    /// <see cref="RetrievalScoreScale"/>), y su posición #0 es justamente la que lleva el
    /// score estable del ítem 4.2 sobre el que están calibradas las bandas.</para>
    /// </summary>
    /// <param name="minimumScore">
    ///   Sólo para el mensaje de log del caso "cero chunks": es el umbral que aplicó el
    ///   retriever, no uno que este gate evalúe.
    /// </param>
    public GroundingAssessment Assess(
        IReadOnlyList<RetrievalResult> chunks,
        float minimumScore,
        string query)
    {
        var topScore = chunks.Count > 0 ? chunks[0].SimilarityScore : 0f;
        var scoreIsAbsolute = chunks.Count > 0 && chunks[0].ScoreScale.IsComparableAcrossQueries();
        var calibration = scoreIsAbsolute ? chunks[0].GateCalibration : null;
        try
        {
            calibration?.ValidateFor(chunks[0]);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Incompatible gate calibration; generation rejected.");
            throw;
        }
        var options = Options;
        var low = calibration?.LowConfidenceThreshold ?? options.LowConfidenceThreshold;
        var high = calibration?.HighConfidenceThreshold ?? options.HighConfidenceThreshold;
        var noGrounding = chunks.Count == 0 || (scoreIsAbsolute && topScore < low);

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
                    topScore, low, query);
            }

            return new GroundingAssessment(HasGrounding: false, ConfidenceAddendum: null);
        }

        // ── Mid-band hedge ────────────────────────────────────────
        string? confidenceAddendum = null;
        if (scoreIsAbsolute && topScore < high)
        {
            _logger.LogInformation(
                "[RAG] Mid confidence ({Score:F3} < {Threshold:F3}) for query: {Query}. Answering with a low-confidence hedge.",
                topScore, high, query);
            confidenceAddendum = SystemPromptComposer.LowConfidenceAddendum;
        }

        return new GroundingAssessment(HasGrounding: true, ConfidenceAddendum: confidenceAddendum);
    }
}
