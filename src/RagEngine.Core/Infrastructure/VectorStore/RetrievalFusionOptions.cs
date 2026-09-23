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

/// <summary>
/// Configuración del segundo salto por símbolo (ítem 6.a): tras la fusión primaria
/// (nativa o ponderada, según la colección), se cosechan los <c>consumed_symbols</c>
/// de los primeros <see cref="SeedResults"/> resultados y se relanza un
/// <c>QueryAsync</c> filtrado por <c>defined_symbols</c> ∈ ese conjunto (ver
/// <c>QdrantSemanticRetriever.ExpandBySymbolAsync</c>).
///
/// Deliberadamente NO es un peso más dentro de <see cref="RetrievalFusionOptions"/>:
/// esos tres pesos (WeightCodigo/Sparse/Resumen) calibran cómo de bien encaja un chunk
/// con la consulta dentro de la fusión primaria y no se tocan aquí. El salto por
/// símbolo entra como una fusión RRF de <b>segundo nivel</b>, separada, entre el rango
/// que cada punto ya tenía en la fusión primaria y el rango que obtuvo en el segundo
/// salto — con su propio peso (<see cref="Weight"/>), para no reescalar los tres
/// originales al calibrar este.
///
/// <see cref="Enabled"/> es el único interruptor de producción (ítem 6.a, rollback):
/// en false (default), <c>QdrantSemanticRetriever</c> no ejecuta ningún segundo
/// QueryAsync — cero riesgo de regresión ni de coste extra sobre el camino existente.
/// Bound from la sección "TwoHop" de appsettings.json.
/// </summary>
public sealed record TwoHopOptions
{
    public const string SectionName = "TwoHop";

    /// <summary>Interruptor maestro. False = comportamiento idéntico al de antes de 6.a.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Cuántos de los primeros resultados de la fusión primaria se inspeccionan para
    /// cosechar <c>consumed_symbols</c>. Un valor bajo evita que símbolos de resultados
    /// ya débiles (cola de la lista) arrastren ruido al segundo salto.
    /// </summary>
    public int SeedResults { get; init; } = 5;

    /// <summary>Límite de resultados del segundo <c>QueryAsync</c> (filtrado por símbolo).</summary>
    public int MaxExpansionResults { get; init; } = 20;

    /// <summary>
    /// Peso del segundo salto en la re-fusión RRF de segundo nivel (rango primario
    /// peso implícito 1.0, mismo <c>k</c> para ambos términos = <c>options.TopK</c> de
    /// la petición — ver <c>QdrantSemanticRetriever.ExpandBySymbolAsync</c>, NO el
    /// <see cref="RetrievalFusionOptions.RrfK"/> de 60 de la fusión primaria: a esa
    /// escala (calibrada para ramas de ~4×TopK) ningún candidato del salto entraría
    /// jamás al resultado final salvo con un peso &gt;0.87, algo detectado y corregido
    /// antes de publicar 6.a).
    ///
    /// Con <c>k=TopK</c> y este peso, un candidato del salto en su mejor posición (rango
    /// 1 del segundo hop) sólo desplaza a puestos primarios más allá de la posición
    /// <c>~⌈TopK·(1/Weight−1)⌉ + 1</c> — con el default (0.8) y TopK=10, eso son los
    /// puestos primarios 4 en adelante: los tres mejores resultados de la fusión
    /// primaria quedan siempre intocables, y el salto compite sólo por la cola.
    /// </summary>
    public double Weight { get; init; } = 0.8;
}
