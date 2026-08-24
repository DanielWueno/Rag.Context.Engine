namespace RagEngine.Core.Infrastructure.VectorStore;

/// <summary>
/// Veredicto de compatibilidad del esquema de una colección contra el que el motor
/// crea hoy (<see cref="QdrantVectorStore.EnsureCollectionAsync"/>).
/// </summary>
public enum CollectionSchemaStatus
{
    /// <summary>Esquema actual: dense + sparse nombrados, con o sin el tercer vector opt-in.</summary>
    Current,

    /// <summary>Consultable, pero le falta alguna capacidad opcional (hoy: el vector de resumen).</summary>
    Legacy,

    /// <summary>Toda consulta contra esta colección falla. Es el caso de 'Not existing vector name error: dense'.</summary>
    Incompatible
}

/// <summary>
/// Los hechos crudos del esquema de una colección, ya traducidos desde los tipos gRPC de
/// Qdrant. Existe separado del veredicto para que la clasificación sea una función pura
/// y se pueda probar sin un Qdrant vivo — el escenario que importa (una colección rota)
/// no se puede montar en un test de integración sin recrearla a mano.
/// </summary>
/// <param name="Name">Nombre de la colección.</param>
/// <param name="UsesNamedVectors">
/// false cuando la colección se creó con un único vector anónimo (esquema pre-híbrido).
/// Ese es el caso que produce 'Not existing vector name error: dense' en la consulta.
/// </param>
/// <param name="DenseVectors">Nombre → dimensión de cada vector denso nombrado. Vacío si <paramref name="UsesNamedVectors"/> es false.</param>
/// <param name="SparseVectors">Nombres de los vectores sparse configurados.</param>
/// <param name="AnonymousVectorSize">Dimensión del vector anónimo, cuando lo hay.</param>
/// <param name="PointsCount">Puntos indexados, para poder decidir si vale la pena migrar o recrear.</param>
public sealed record CollectionSchemaSnapshot(
    string Name,
    bool UsesNamedVectors,
    IReadOnlyDictionary<string, ulong> DenseVectors,
    IReadOnlyCollection<string> SparseVectors,
    ulong? AnonymousVectorSize,
    ulong PointsCount);

/// <summary>Veredicto sobre una colección: qué está mal, qué falta y qué hacer al respecto.</summary>
/// <param name="Problems">Motivos por los que la colección no es consultable. Vacío salvo en <see cref="CollectionSchemaStatus.Incompatible"/>.</param>
/// <param name="Notes">Diferencias que no rompen la consulta pero cambian lo que se puede hacer con la colección.</param>
/// <param name="Remedy">Acción concreta, o null si no hay nada que hacer.</param>
public sealed record CollectionSchemaReport(
    string Name,
    CollectionSchemaStatus Status,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes,
    string? Remedy,
    ulong PointsCount);

/// <summary>
/// Clasifica el esquema de una colección ANTES de que se le haga una consulta.
///
/// Existe porque el modo de fallo real de este sistema no es un error al arrancar sino un
/// error en la consulta: una colección creada por una versión anterior del motor sigue
/// listándose como sana ('green', con puntos) y sólo revienta cuando alguien busca, con
/// 'Not existing vector name error: dense'. El diagnóstico tiene que nombrar la colección
/// culpable sin depender de que alguien acierte a consultarla.
/// </summary>
public static class CollectionSchemaDiagnostics
{
    /// <summary>
    /// Función pura: del esquema observado al veredicto. <paramref name="expectedDimension"/>
    /// es nullable a propósito — el doctor tiene que poder diagnosticar colecciones aunque el
    /// modelo ONNX no cargue, que es justo cuando más falta hace.
    /// </summary>
    public static CollectionSchemaReport Classify(CollectionSchemaSnapshot snapshot, int? expectedDimension)
    {
        var problems = new List<string>();
        var notes = new List<string>();

        if (!snapshot.UsesNamedVectors)
        {
            var size = snapshot.AnonymousVectorSize is { } s ? $"{s}D" : "desconocida";
            problems.Add(
                $"vector único sin nombre (dimensión {size}): el motor consulta por nombre, " +
                $"así que toda búsqueda falla con \"Not existing vector name error: {QdrantVectorStore.DenseVectorName}\"");
        }
        else if (!snapshot.DenseVectors.ContainsKey(QdrantVectorStore.DenseVectorName))
        {
            var found = snapshot.DenseVectors.Count == 0
                ? "ninguno"
                : string.Join(", ", snapshot.DenseVectors.Keys.OrderBy(k => k, StringComparer.Ordinal));
            problems.Add($"falta el vector denso '{QdrantVectorStore.DenseVectorName}' (densos presentes: {found})");
        }
        else if (expectedDimension is { } expected &&
                 snapshot.DenseVectors[QdrantVectorStore.DenseVectorName] != (ulong)expected)
        {
            problems.Add(
                $"'{QdrantVectorStore.DenseVectorName}' es de {snapshot.DenseVectors[QdrantVectorStore.DenseVectorName]}D " +
                $"pero el modelo actual produce {expected}D: los embeddings no son comparables");
        }

        if (!snapshot.SparseVectors.Contains(QdrantVectorStore.SparseVectorName))
        {
            problems.Add(
                $"falta el vector sparse '{QdrantVectorStore.SparseVectorName}': la búsqueda híbrida falla");
        }

        if (problems.Count > 0)
        {
            return new CollectionSchemaReport(
                snapshot.Name,
                CollectionSchemaStatus.Incompatible,
                problems,
                notes,
                Remedy: $"re-ingesta desde cero: 'rag ingest --collection {snapshot.Name} --force' " +
                        "(el esquema de una colección no se puede migrar en caliente en Qdrant)",
                snapshot.PointsCount);
        }

        if (!snapshot.DenseVectors.ContainsKey(QdrantVectorStore.SummaryVectorName))
        {
            notes.Add(
                $"sin el vector de resumen '{QdrantVectorStore.SummaryVectorName}' " +
                "(opt-in por colección: se busca en 2 bandas en vez de 3)");

            return new CollectionSchemaReport(
                snapshot.Name,
                CollectionSchemaStatus.Legacy,
                problems,
                notes,
                Remedy: null,
                snapshot.PointsCount);
        }

        return new CollectionSchemaReport(
            snapshot.Name,
            CollectionSchemaStatus.Current,
            problems,
            notes,
            Remedy: null,
            snapshot.PointsCount);
    }
}
