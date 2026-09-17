namespace RagEngine.Core.Domain;

/// <summary>
/// Escala en la que está expresado <see cref="RetrievalResult.SimilarityScore"/>.
///
/// Existe porque ese campo es un <c>float</c> que ha transportado cuatro números con
/// significados incompatibles según el camino que produjo el resultado, sin que nada en
/// el tipo lo dijera: los consumidores lo deducían del código del retriever o de una
/// bandera lateral (<c>useReRanking</c>). Ítem 4.9 del plan; grieta 3.2 de
/// docs/analisis-futuro/arquitectura-puertos-y-adaptadores.md.
///
/// La distinción que decide casi todo uso es
/// <see cref="RetrievalScoreScaleExtensions.IsComparableAcrossQueries"/>: sólo dos de
/// estas escalas producen un número que significa lo mismo en dos consultas distintas y
/// puede, por tanto, compararse contra un umbral absoluto.
///
/// <para><b>Invariante de orden (ítem 4.2).</b> Una lista devuelta por
/// <see cref="Abstractions.IReRanker.ReRankAsync"/> con la re-puntuación estable activada
/// lleva <see cref="CrossEncoderStable"/> en la posición #0 y
/// <see cref="CrossEncoderBatched"/> en el resto. Los dos números están en el mismo rango
/// pero NO salen del mismo cálculo, así que la lista <b>no está ordenada monótonamente por
/// score</b>: el resultado #0 puede puntuar por debajo del #1. El orden es el del ranking
/// por lotes y es el bueno. Reordenar por <see cref="RetrievalResult.SimilarityScore"/>
/// "para normalizar" revierte el ítem 4.2 en silencio.</para>
/// </summary>
public enum RetrievalScoreScale
{
    /// <summary>
    /// Similitud coseno cruda entre el vector de la consulta y el del chunk, en [0..1] y
    /// comparable entre consultas. Es la escala de
    /// <see cref="RetrievalOptions.MinimumSimilarityScore"/>, que se aplica al prefetch
    /// denso; ningún camino de producción devuelve hoy este número al llamador, porque la
    /// búsqueda siempre sale por una fusión. Se declara porque es la escala de referencia
    /// que el umbral de configuración da por supuesta.
    /// </summary>
    CosineSimilarity,

    /// <summary>
    /// Semantica Reciprocal Rank Fusion de Qdrant (<c>Fusion.Rrf</c>) sobre las ramas densa y
    /// dispersa, todas con peso igual. Es una <b>función del ranking, no de la
    /// similitud</b>: su magnitud (1/(2+rango base cero), sumada por rama) depende de
    /// en qué puesto quedó el chunk dentro de esta consulta y no significa nada al
    /// compararla con la de otra consulta. Camino de las colecciones de 2 vectores;
    /// calculado en el adaptador para desempatar antes de cada corte.
    /// </summary>
    RankFusionNative,

    /// <summary>
    /// RRF ponderada manual sobre tres ramas (código, dispersa, resumen) con los pesos de
    /// <c>RetrievalFusionOptions</c>. Mismo carácter que <see cref="RankFusionNative"/> —
    /// función del ranking — pero además de otra magnitud, porque los pesos no suman 3×1:
    /// dos colecciones con distinta calibración producen números que tampoco son
    /// comparables entre sí. Camino de las colecciones con el tercer vector de resumen.
    /// </summary>
    RankFusionWeighted,

    /// <summary>
    /// Sigmoide del logit del cross-encoder para el par (consulta, chunk), en [0..1] y
    /// comparable entre consultas. Calculada en la pasada por lotes de
    /// <c>ReRankAsync</c>, y es la que <b>decide el orden final</b> de la lista.
    ///
    /// Con el modelo cuantizado a int8, el padding dinámico del lote mueve este número en
    /// el orden de 1e-4 según qué vecinos le tocaron — y como el pool de rerank es 3×TopK,
    /// eso lo hace depender del TopK pedido. Sirve para ordenar; no para comparar contra
    /// un umbral absoluto calibrado (ver <see cref="CrossEncoderStable"/>).
    /// </summary>
    CrossEncoderBatched,

    /// <summary>
    /// La misma sigmoide del cross-encoder, pero recalculada en un lote de tamaño 1 sobre
    /// un único chunk (ítem 4.2, <c>CrossEncoderOptions.StableGateScore</c>). Sin vecinos,
    /// la longitud de secuencia es la del propio par y el resultado es función únicamente
    /// de (consulta, chunk): invariante al tamaño del pool y por tanto al TopK.
    ///
    /// Es el número que el gate de confianza compara contra los umbrales de banda
    /// calibrados en el ítem 4.3, y sólo lo lleva la posición #0 de la lista — ver el
    /// invariante de orden en la documentación de este enum.
    /// </summary>
    CrossEncoderStable,

    /// <summary>
    /// Candidato incorporado por el segundo salto por símbolo (ítem 6.a,
    /// <c>TwoHopOptions</c>): no salió del vector de la consulta original contra este
    /// chunk, sino de un <c>QueryAsync</c> filtrado por <c>defined_symbols</c> ∈ los
    /// <c>consumed_symbols</c> de los primeros resultados de la fusión primaria. El
    /// score es, igual que <see cref="RankFusionNative"/>/<see cref="RankFusionWeighted"/>,
    /// función del rango combinado de una re-fusión RRF de segundo nivel entre la
    /// fusión primaria y este salto — no de la similitud, y no comparable entre
    /// consultas.
    /// </summary>
    SymbolExpansion,
}

/// <summary>
/// Preguntas que un consumidor de <see cref="RetrievalResult"/> necesita responder sobre
/// el score sin tener que leer el código del retriever ni del reranker.
/// </summary>
public static class RetrievalScoreScaleExtensions
{
    /// <summary>
    /// True si el número significa lo mismo en dos consultas distintas y puede compararse
    /// contra un umbral absoluto (banda de confianza, corte de calidad, color en la CLI).
    ///
    /// False para las escalas de fusión: ahí el score sale del <i>puesto</i> que ocupó el
    /// chunk, así que el mejor resultado de una consulta trivial y el de una consulta sin
    /// respuesta en el corpus puntúan casi igual. Un umbral sobre ese número no mide
    /// confianza, mide cuántas ramas coincidieron.
    /// </summary>
    public static bool IsComparableAcrossQueries(this RetrievalScoreScale scale) => scale switch
    {
        RetrievalScoreScale.CosineSimilarity    => true,
        RetrievalScoreScale.CrossEncoderBatched => true,
        RetrievalScoreScale.CrossEncoderStable  => true,
        RetrievalScoreScale.RankFusionNative    => false,
        RetrievalScoreScale.RankFusionWeighted  => false,
        RetrievalScoreScale.SymbolExpansion     => false,
        _ => false,
    };

    /// <summary>
    /// Etiqueta corta para logs y para la salida de la CLI, donde mostrar un número
    /// desnudo invita a leerlo como un porcentaje de similitud que no siempre es.
    /// </summary>
    public static string ToDisplayName(this RetrievalScoreScale scale) => scale switch
    {
        RetrievalScoreScale.CosineSimilarity    => "coseno",
        RetrievalScoreScale.RankFusionNative    => "RRF",
        RetrievalScoreScale.RankFusionWeighted  => "RRF ponderado",
        RetrievalScoreScale.CrossEncoderBatched => "cross-encoder",
        RetrievalScoreScale.CrossEncoderStable  => "cross-encoder estable",
        RetrievalScoreScale.SymbolExpansion     => "expansión por símbolo",
        _ => scale.ToString(),
    };
}
