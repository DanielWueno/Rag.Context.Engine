namespace RagEngine.Core.Domain;

/// <summary>
/// Configuración del store de auditoría local (ítem 12.11), bound de la sección
/// "Audit" de appsettings.json. Ruta y actor son independientes de la caché de
/// resúmenes (<see cref="IngestionOptions.ResumenCachePath"/>): comparten el patrón
/// SQLite-por-archivo pero no la base, para no mezclar historia auditable con una
/// caché que sí se puede borrar/regenerar sin pérdida de información.
/// </summary>
public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>Ruta de la base SQLite de eventos de auditoría.</summary>
    public string DbPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "rag-engine", "audit.sqlite3");

    /// <summary>
    /// Identificador local explícito del operador (ítem 12.11). Sin configurar, el
    /// evento cae en <see cref="Environment.UserName"/> — nunca en una identidad
    /// corporativa inferida del contexto de retrieval.
    /// </summary>
    public string? ActorId { get; init; }
}
