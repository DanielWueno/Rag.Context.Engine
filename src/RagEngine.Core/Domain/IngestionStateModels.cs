namespace RagEngine.Core.Domain;

/// <summary>
/// Estado de una corrida de ingesta completa (ítem 13.1). Corresponde 1:1 con los
/// desenlaces que ya audita <see cref="AuditOutcome"/> para la operación
/// <see cref="AuditOperations.IngestRepository"/> — el estado de corrida es la
/// contraparte "por documento" de ese mismo evento, no un registro alternativo.
/// </summary>
public enum IngestionRunStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
/// Estado de un documento individual dentro de una corrida (ítem 13.1). Un documento
/// nace <see cref="Pending"/> en cuanto el scanner lo descubre (antes de leer su
/// contenido), pasa a <see cref="Running"/> al empezar a leerlo/trocearlo, y termina
/// en <see cref="Succeeded"/> (todos sus chunks admitidos confirmados en el vector
/// store) o <see cref="Failed"/> (lectura, chunking o upsert fallaron). Nunca vuelve
/// atrás desde un estado terminal dentro de la misma corrida.
/// </summary>
public enum DocumentIngestionStatus
{
    Pending,
    Running,
    Succeeded,
    Failed
}

/// <summary>
/// Encabezado de una corrida de ingesta persistida (ítem 13.1). Una fila por
/// <see cref="RunId"/>; nunca se borra ni se sobreescribe el historial de corridas
/// anteriores al iniciar una nueva.
/// </summary>
public sealed record IngestionRunRecord
{
    public required string RunId { get; init; }
    public required string Collection { get; init; }
    public required string RepositoryPath { get; init; }
    public required string ActorId { get; init; }
    public required IngestionRunStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>
/// Estado persistido de UN documento dentro de UNA corrida (ítem 13.1). La llave de
/// identidad (<see cref="DocumentKey"/>) es la misma clave de <c>ChunkBuilder.BuildIdentityKey</c>
/// (ítem 8.f: repositorio+ruta relativa, nunca ruta absoluta), para que el estado
/// correlacione con el "file_path" del payload de Qdrant sin ambigüedad.
/// </summary>
public sealed record DocumentIngestionState
{
    public required string RunId { get; init; }
    public required string DocumentKey { get; init; }

    /// <summary>SHA-256 del contenido crudo del archivo. Null mientras el documento sigue Pending (aún no leído).</summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// Huella del contrato de chunking efectivo de esta corrida (perfil de escaneo +
    /// opciones relevantes). Permite distinguir, al comparar corridas, un documento
    /// re-marcado Succeeded porque no cambió de uno cuyo contrato de troceo cambió y
    /// por tanto necesita reprocesarse aunque el contenido sea idéntico.
    /// </summary>
    public required string Contract { get; init; }

    public required string ActorId { get; init; }
    public required DocumentIngestionStatus Status { get; init; }

    /// <summary>Chunks admitidos esperados para este documento. Null hasta que el chunking termina.</summary>
    public int? ChunksExpected { get; init; }

    /// <summary>Chunks confirmados como upserted en el vector store hasta el momento.</summary>
    public int ChunksIndexed { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Mensaje corto de diagnóstico cuando Status es Failed. Nunca contenido de fuentes.</summary>
    public string? Detail { get; init; }
}
