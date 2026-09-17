namespace RagEngine.Core.Domain;

/// <summary>
/// Resultado de la operación auditada. Corresponde 1:1 con los cuatro escenarios que
/// exige el ítem 12.11: éxito, denegación (autorización de colección), fallo (excepción)
/// y cancelación (Ctrl+C local o desconexión del cliente HTTP).
/// </summary>
public enum AuditOutcome
{
    Success,
    Denied,
    Failed,
    Cancelled
}

/// <summary>
/// Evento de auditoría durable para una consulta o ingesta local. No reemplaza los logs
/// operativos (Serilog/QueryEvent, IngestionProgress) — es el registro append-only e
/// inmutable del que depende reconstruir "quién hizo qué" sin inferir un actor de logs
/// viejos. Un registro anterior a este ítem (o cualquier fila con ActorId ausente) queda
/// como actor desconocido para siempre: nunca se rellena retroactivamente.
/// </summary>
public sealed record AuditEvent
{
    /// <summary>Versión de esquema del evento vigente al escribirlo (ítem 12.11 = 1).</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Identificador único de ESTE evento. Reintentar la persistencia con el mismo
    /// EventId (p. ej. porque la respuesta de la escritura anterior se perdió) no debe
    /// duplicar la fila — ver <see cref="Abstractions.IAuditEventStore.RecordAsync"/>.
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>
    /// Identificador de la operación causal (una corrida de ingesta, una consulta) que
    /// puede agrupar más de un evento en el futuro. Hoy cada operación emite un único
    /// evento final, así que EventId y CorrelationId difieren en origen pero coinciden
    /// en cardinalidad; no se debe asumir que siempre serán iguales.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>Nombre corto y estable de la operación auditada (ver <see cref="AuditOperations"/>).</summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Siempre <see cref="AuditActor.TypeLocalOperator"/> mientras no exista un IDP
    /// corporativo real (ver AGENTS.md: identidad corporativa sigue siendo capacidad
    /// futura). Un contexto de retrieval autorizado (tenant/módulo) NO se traduce en un
    /// ActorType distinto — eso sería inventar una identidad empresarial que el sistema
    /// no puede verificar todavía.
    /// </summary>
    public required string ActorType { get; init; }

    /// <summary>Identificador explícito del operador local (configurado o, en su defecto, el usuario del SO).</summary>
    public required string ActorId { get; init; }

    /// <summary>Colección afectada. Null para operaciones que no aplican a una colección concreta.</summary>
    public string? Collection { get; init; }

    public required AuditOutcome Outcome { get; init; }

    /// <summary>Mensaje corto de diagnóstico (p. ej. Exception.Message en Failed). Nunca contenido de fuentes ni tokens.</summary>
    public string? Detail { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required int Version { get; init; }
}

/// <summary>Nombres estables de operación usados como <see cref="AuditEvent.Operation"/>.</summary>
public static class AuditOperations
{
    public const string QuerySearch = "query.search";
    public const string QueryAsk = "query.ask";
    public const string IngestRepository = "ingest.repository";
}

/// <summary>
/// Único tipo de actor disponible hoy. Ver <see cref="AuditEvent.ActorType"/>: no se
/// infiere ni se simula una identidad corporativa a partir de RetrievalContext/CollectionActor.
/// </summary>
public static class AuditActor
{
    public const string TypeLocalOperator = "local_operator";

    /// <summary>
    /// Resuelve el id explícito de <see cref="AuditOptions.ActorId"/>; sin configurar,
    /// cae al usuario del sistema operativo — nunca a un claim de identidad empresarial.
    /// </summary>
    public static string ResolveId(AuditOptions options) =>
        string.IsNullOrWhiteSpace(options.ActorId) ? Environment.UserName : options.ActorId;
}
