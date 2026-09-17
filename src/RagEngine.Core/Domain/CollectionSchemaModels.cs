namespace RagEngine.Core.Domain;

/// <summary>
/// Veredicto de compatibilidad del esquema de una colección contra el que el motor
/// crea hoy.
/// </summary>
public enum CollectionSchemaStatus
{
    Current,
    Legacy,
    Incompatible
}

/// <summary>
/// Hechos crudos del esquema de una colección ya traducidos desde Qdrant.
/// </summary>
public sealed record CollectionSchemaSnapshot(
    string Name,
    bool UsesNamedVectors,
    IReadOnlyDictionary<string, ulong> DenseVectors,
    IReadOnlyCollection<string> SparseVectors,
    ulong? AnonymousVectorSize,
    ulong PointsCount);

/// <summary>
/// Veredicto sobre una colección: qué está mal, qué falta y qué hacer al respecto.
/// </summary>
public sealed record CollectionSchemaReport(
    string Name,
    CollectionSchemaStatus Status,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes,
    string? Remedy,
    ulong PointsCount);
