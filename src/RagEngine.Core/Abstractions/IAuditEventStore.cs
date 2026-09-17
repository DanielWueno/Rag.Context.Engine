using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

/// <summary>
/// Puerto de auditoría local durable (ítem 12.11). La capa de aplicación y los hosts
/// dependen de esta interfaz, nunca de la clase concreta SQLite
/// (<c>RagEngine.Core.Infrastructure.Audit.SqliteAuditEventStore</c>) — un backend
/// distinto se agrega como otro adaptador sin tocar consumidores.
/// </summary>
public interface IAuditEventStore
{
    /// <summary>
    /// Persiste el evento. Idempotente por <see cref="AuditEvent.EventId"/>: reintentar
    /// con el mismo EventId (misma escritura, respuesta perdida por un reinicio o un
    /// timeout) no duplica la fila. Cada evento es inmutable una vez escrito — este
    /// método nunca actualiza un evento ya persistido.
    /// </summary>
    Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default);

    /// <summary>
    /// Todos los eventos persistidos, ordenados por Timestamp ascendente. Pensado para
    /// verificación/diagnóstico (no hay paginación: la auditoría local no espera el
    /// volumen que la justificaría todavía).
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> ListAsync(CancellationToken ct = default);
}
