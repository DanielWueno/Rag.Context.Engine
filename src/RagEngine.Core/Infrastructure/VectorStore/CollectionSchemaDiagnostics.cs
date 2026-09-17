using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.VectorStore;

/// <summary>
/// Clasifica el esquema de una colección ANTES de que se le haga una consulta.
/// </summary>
public static class CollectionSchemaDiagnostics
{
    /// <summary>
    /// Función pura: del esquema observado al veredicto.
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
