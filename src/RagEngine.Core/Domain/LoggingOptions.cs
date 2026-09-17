namespace RagEngine.Core.Domain;

/// <summary>
/// Política de minimización de logs operativos (ítem 12.9), bound de la sección
/// "Logging" de appsettings.json. Por defecto, el log operativo de cada consulta
/// (QueryEvent en rag-api-*.json / rag-cli-*.json) NUNCA incluye la pregunta, la
/// respuesta generada ni las fuentes citadas — sólo metadatos (colección, TopK,
/// duración, conteo de resultados). Esto es independiente de
/// <see cref="AuditOptions"/>: la auditoría (12.11) tampoco guarda contenido, así que
/// activar diagnóstico aquí no cambia lo que se audita.
/// </summary>
public sealed class LoggingOptions
{
    public const string SectionName = "Logging";

    /// <summary>
    /// Activa el diagnóstico local acotado: incluir pregunta/respuesta/fuentes en el
    /// QueryEvent. Requiere <see cref="QueryContentDiagnosticsExpiresAt"/> configurado
    /// y no vencido — de lo contrario se ignora (ver
    /// <see cref="Diagnostics.QueryContentDiagnostics.IsActive"/>) y se registra una
    /// advertencia al iniciar el host.
    /// </summary>
    public bool EnableQueryContentDiagnostics { get; init; }

    /// <summary>
    /// Caducidad obligatoria del diagnóstico. Sin este valor (o ya vencido),
    /// <see cref="EnableQueryContentDiagnostics"/> no tiene efecto — un operador no
    /// puede dejarlo prendido indefinidamente por olvido.
    /// </summary>
    public DateTimeOffset? QueryContentDiagnosticsExpiresAt { get; init; }
}
