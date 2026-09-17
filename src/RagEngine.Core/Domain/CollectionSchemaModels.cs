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
/// Hechos crudos del esquema de una colección ya traducidos desde el backend vectorial.
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

/// <summary>
/// Estado operacional reportado por el motor de vectores (equivalente al semáforo
/// verde/amarillo/rojo de un backend vectorial típico). <c>Unknown</c> cubre cualquier valor futuro que el
/// backend concreto pueda devolver y que este puerto no conozca todavía — se colorea
/// igual que <c>Red</c> en los adaptadores primarios, nunca como sano.
/// </summary>
public enum CollectionHealthStatus
{
    Green,
    Yellow,
    Red,
    Unknown
}

/// <summary>
/// Ítem 9.2: hechos de salud/tamaño de una colección, ya traducidos desde el backend
/// concreto, para que los hosts (Api/Cli) dejen de resolver el cliente de
/// infraestructura directamente. <see cref="DenseVectorDimension"/> es null cuando el
/// backend no expone un único vector denso sin nombre (p. ej. colecciones con
/// vectores nombrados) — el mismo caso en el que el `rag status` histórico tampoco
/// mostraba la fila de dimensiones.
/// </summary>
public sealed record CollectionHealthReport(
    string Name,
    CollectionHealthStatus Status,
    ulong PointsCount,
    ulong? DenseVectorDimension);
