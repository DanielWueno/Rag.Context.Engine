namespace RagEngine.Core.Infrastructure.VectorStore;

/// <summary>
/// Pesos de la fusión RRF ponderada manual (decisión 6), usada SOLO cuando la
/// colección consultada tiene el tercer vector "dense-resumen" — para colecciones de
/// 2 vectores, <c>QdrantSemanticRetriever</c> sigue usando la fusión nativa de Qdrant
/// sin tocar estos valores. Defaults = los pesos calibrados en el PoC
/// (poc/RagEngine.Poc.FreeSearch/RESULTADOS.md).
/// Bound from la sección "RetrievalFusion" de appsettings.json — configurable sin
/// recompilar para poder re-calibrar tras correr `rag eval` contra datos reales.
/// </summary>
public sealed record RetrievalFusionOptions
{
    public const string SectionName = "RetrievalFusion";

    public double WeightCodigo { get; init; } = 1.0;
    public double WeightSparse { get; init; } = 1.3;
    public double WeightResumen { get; init; } = 2.5;
    public int RrfK { get; init; } = 60;
}
