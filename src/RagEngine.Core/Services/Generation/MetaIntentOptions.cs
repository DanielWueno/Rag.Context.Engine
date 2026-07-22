namespace RagEngine.Core.Services.Generation;

/// <summary>
/// Configuration options for <see cref="SemanticMetaIntentDetector"/>.
/// Bound from the "MetaIntent" section of appsettings.json.
///
/// Calibrado 2026-07-22 corriendo el embedder denso real (paraphrase-multilingual-
/// MiniLM-L12-v2) contra: 24 exemplares positivos (ver
/// <see cref="SemanticMetaIntentDetector.Exemplars"/>), 14 negativos sintéticos
/// (preguntas de negocio con superficie léxica parecida — "cómo funciona...",
/// "cuántos...", "con qué...") y 18 queries reales de
/// logs/rag-api-2026072{1,2}.json (8 meta, 10 de negocio). Resultado: separación
/// limpia, sin solape —
///   - Negocio real (contra el set de positivos): máx. similitud 0.226–0.430.
///   - Negocio sintético (adversarial, no de logs): máx. hasta 0.552.
///   - Meta-pregunta real: mín. similitud 0.862, máx. 0.995.
/// El umbral queda con margen amplio de ambos lados del hueco (0.55 → 0.86).
/// </summary>
public sealed record MetaIntentOptions
{
    public const string SectionName = "MetaIntent";

    /// <summary>
    /// Similitud coseno mínima contra el exemplar positivo más cercano para
    /// considerar la query una meta-pregunta. Ver nota de calibración arriba.
    /// </summary>
    public float SimilarityThreshold { get; init; } = 0.65f;
}
