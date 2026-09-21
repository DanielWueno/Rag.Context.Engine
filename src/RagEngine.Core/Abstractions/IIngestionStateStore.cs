using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

/// <summary>
/// Puerto de estado de ingesta durable (ítem 13.1). Persiste, por corrida y por
/// documento, quién (actor), qué (content_hash + contrato de chunking) y en qué
/// estado (pending/running/succeeded/failed) quedó cada archivo — para que una
/// ingesta interrumpida sea reconstruible sin inferir nada de los logs operativos.
/// La capa de aplicación y los hosts dependen de esta interfaz, nunca de la clase
/// concreta SQLite (<c>RagEngine.Core.Infrastructure.State.SqliteIngestionStateStore</c>).
/// </summary>
public interface IIngestionStateStore
{
    /// <summary>
    /// Registra el inicio de una corrida. Idempotente por <see cref="IngestionRunRecord.RunId"/>:
    /// reintentar con el mismo RunId no duplica la fila (usa el mismo patrón INSERT OR IGNORE
    /// que <see cref="IAuditEventStore.RecordAsync"/>).
    /// </summary>
    Task StartRunAsync(IngestionRunRecord run, CancellationToken ct = default);

    /// <summary>
    /// Cierra una corrida existente con su desenlace final. No-op silencioso si
    /// <paramref name="runId"/> no existe (nunca inventa una corrida al cerrarla).
    /// </summary>
    Task FinishRunAsync(
        string runId, IngestionRunStatus status, DateTimeOffset finishedAt, CancellationToken ct = default);

    /// <summary>
    /// Inserta o actualiza el estado de UN documento dentro de una corrida (llave
    /// natural: RunId + DocumentKey). A diferencia de un evento de auditoría, esta
    /// fila SÍ se actualiza en el tiempo (pending → running → succeeded/failed):
    /// nunca es un log append-only, es el estado vigente de ese documento en esa
    /// corrida. Aplicarla dos veces con los mismos valores no cambia el resultado.
    /// </summary>
    Task UpsertDocumentStateAsync(DocumentIngestionState state, CancellationToken ct = default);

    /// <summary>Todos los estados de documento de una corrida, ordenados por DocumentKey.</summary>
    Task<IReadOnlyList<DocumentIngestionState>> GetDocumentStatesAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Corridas de una colección, más recientes primero. <paramref name="take"/> acota
    /// cuántas devolver (pensado para el CLI, no para exportar historia completa).
    /// </summary>
    Task<IReadOnlyList<IngestionRunRecord>> ListRunsAsync(
        string collection, int take = 10, CancellationToken ct = default);

    /// <summary>Una corrida por Id, o null si no existe.</summary>
    Task<IngestionRunRecord?> GetRunAsync(string runId, CancellationToken ct = default);
}
