namespace RagEngine.Core.Domain;

/// <summary>
/// A retrieved code chunk with its relevance score and structural metadata.
/// This is the final artifact injected into the LLM prompt context.
/// </summary>
/// <param name="SimilarityScore">
///   Puntuación de relevancia. Su significado NO es único: depende de
///   <paramref name="ScoreScale"/>, que es obligatorio precisamente para que ningún
///   productor pueda emitir el número sin decir en qué escala está. Léelo siempre junto
///   a la escala; en particular, no lo compares contra un umbral absoluto sin comprobar
///   antes <see cref="RetrievalScoreScaleExtensions.IsComparableAcrossQueries"/>.
/// </param>
/// <param name="ScoreScale">
///   Escala en la que está expresado <paramref name="SimilarityScore"/>. Ver
///   <see cref="RetrievalScoreScale"/>, que documenta además el invariante de orden del
///   ítem 4.2: una lista re-rankeada con score de gate estable no está ordenada
///   monótonamente por score y no debe reordenarse.
/// </param>
public sealed record RetrievalResult(
    string ChunkId,
    string Content,
    float SimilarityScore,
    RetrievalScoreScale ScoreScale,
    CodeChunkMetadata Metadata,
    string ContentHash
)
{
    /// <summary>Score used for ordering, before any stable-gate replacement.</summary>
    public double RankingScore { get; init; } = SimilarityScore;
    public RetrievalScoreScale RankingScoreScale { get; init; } = ScoreScale;
}

/// <summary>
/// Parameters for refining a semantic search query.
/// </summary>
public sealed record RetrievalOptions
{
    /// <summary>
    /// Contexto de autorización ya resuelto por el adaptador llamador. Local y empresa
    /// se expresan explícitamente; null nunca significa "todo".
    /// </summary>
    public required RetrievalContext Context { get; init; }

    /// <summary>Maximum number of results to return. Default: 10.</summary>
    public int TopK { get; init; } = 10;

    /// <summary>
    /// Umbral mínimo de similitud coseno, aplicado al prefetch DENSO de la
    /// búsqueda híbrida (la rama dispersa y el score RRF final no se filtran).
    /// Con paraphrase-multilingual-MiniLM-L12-v2, la similitud pregunta↔código
    /// relevante ronda 0.12–0.25 y el ruido queda por debajo de ~0.08, por lo
    /// que 0.10 actúa como piso de ruido sin sacrificar recall.
    /// </summary>
    public float MinimumSimilarityScore { get; init; } = 0.10f;

    /// <summary>Filter results to a specific programming language.</summary>
    public SourceLanguage? FilterByLanguage { get; init; }

    /// <summary>Filter results by namespace prefix.</summary>
    public string? FilterByNamespace { get; init; }

    /// <summary>
    /// Filtra resultados por tenant explícito de payload (ítem 5.e). Null/vacío no
    /// restringe: incluye puntos con cualquier tenant y puntos sin tenant declarado
    /// (colección compartida/mixta o fuente sin mapeo). Con valor, exige coincidencia
    /// exacta — un punto sin tenant en payload NO matchea un filtro con valor, igual
    /// que <c>CollectionManifest.Tenants</c> exige coincidencia exacta cuando no está vacío.
    /// </summary>
    public string? FilterByTenant { get; init; }

    /// <summary>
    /// Filtra por módulo lógico derivado del namespace/ruta relativa ya guardados en
    /// payload. Igual que tenant: null/vacío no restringe; con valor exige coincidencia
    /// ordinal exacta o un descendiente separado por '.' (namespace) o '/' (ruta).
    /// Se comprueba dentro del puerto antes y después de two-hop; se combina con AND
    /// con el módulo del contexto autorizado. El prefiltro de Qdrant es aproximado:
    /// descartar candidatos puede devolver menos de TopK, sin rellenado adicional.
    /// </summary>
    public string? FilterByModule { get; init; }

    /// <summary>The Qdrant collection to search.</summary>
    public required string CollectionName { get; init; }

    /// <summary>If true, applies Cross-Encoder re-ranking for higher precision.</summary>
    public bool UseReRanking { get; init; } = false;
}
