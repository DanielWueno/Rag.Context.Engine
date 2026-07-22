namespace RagEngine.Core.Services.Generation;

/// <summary>
/// Configuration options for the 3-band confidence gate applied to the top
/// retrieved chunk's <see cref="RagEngine.Core.Domain.RetrievalResult.SimilarityScore"/>.
/// Bound from the "RagGeneration" section of appsettings.json.
///
/// Estos umbrales solo tienen sentido cuando el score proviene del cross-encoder
/// (rerank activado) — es una sigmoide [0..1] comparable entre queries. El score
/// RRF (sin rerank) es función del ranking, no de similitud, y no se evalúa contra
/// estos umbrales (ver <see cref="RagGenerationService.AskStreamingAsync"/>).
///
/// Calibrado 2026-07-22 contra el pipeline real (colección innovapp-docs), ya con
/// el fix de reproducibilidad del reranker aplicado (reproducibilidad confirmada:
/// 3 corridas idénticas de "Procesos que influyen en la innovapp?" → mismo score y
/// mismo orden de los 10 candidatos, a 8 decimales). Rango observado corriendo
/// `rag search --rerank --output json` sobre una muestra representativa de los
/// queries reales de logs/rag-api-2026072{1,2}.json:
///   - Off-topic puro ("que hora es", "guerra de los pasteles", "traduce al
///     alemán", "pirámide de asteriscos en PowerShell"): 0.0009–0.034.
///   - Fabricación confirmada (mouse, sin historial de conversación): 0.016 — cae
///     en el MISMO rango que el ruido puro de arriba, no se separa de él. El doc
///     de origen (guardrail-dominio-chat.md) anticipaba esto como "banda
///     baja-media"; en la práctica, dado el solape real, ambos casos caen en
///     banda baja (se cortan) — eso sigue cumpliendo el objetivo real ("no
///     fabricación"), un corte también es una no-fabricación válida.
///   - Caso ambiguo legítimo ("Procesos que influyen en la innovapp?"): 0.072 —
///     por encima del rango anterior, banda media (se genera con hedge).
///   - Dominio con recall débil pero contenido real ("EsPermiteCancelarTraslado
///     que hace esta regla?": 0.276, "Como se gestionan las notificaciones?":
///     0.362): banda media — se responde con hedge en vez de afirmar con
///     confianza plena, aceptable dado que el score no distingue bien recall
///     débil de irrelevancia real (problema de ranking fuera de alcance de este
///     gate, ver guardrail-dominio-chat.md).
///   - Dominio con recall fuerte ("cerrar ticket": 0.957, "encuesta de
///     satisfacción": 0.909, "validación al cerrar ticket": 0.9999): banda
///     alta, sin cambios de comportamiento.
/// LowConfidenceThreshold y HighConfidenceThreshold quedan con margen dentro de
/// las brechas observadas (0.034→0.072 y 0.362→0.909 respectivamente), no en un
/// punto exacto de la muestra.
/// </summary>
public sealed record RagGenerationOptions
{
    public const string SectionName = "RagGeneration";

    /// <summary>
    /// Por debajo de este score (con rerank activado), se corta antes de generar y
    /// se responde con <see cref="RagGenerationService.NoContextFallbackMessage"/> —
    /// mismo camino que el caso de "0 chunks". Ver nota de calibración arriba.
    /// </summary>
    public float LowConfidenceThreshold { get; init; } = 0.05f;

    /// <summary>
    /// Por debajo de este score (y por encima de <see cref="LowConfidenceThreshold"/>),
    /// se genera igual pero con un addendum de baja confianza en el system prompt.
    /// En o por encima de este score, el flujo es el actual sin cambios. Ver nota de
    /// calibración arriba.
    /// </summary>
    public float HighConfidenceThreshold { get; init; } = 0.60f;
}
