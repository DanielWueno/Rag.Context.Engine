using RagEngine.Core.Domain;

namespace RagEngine.Core.Diagnostics;

/// <summary>
/// Contrato puro y testeable del "diagnóstico acotado" de logs (ítem 12.9): función
/// pequeña, sin I/O, para no depender de un harness HTTP/Serilog completo al fijar
/// el oráculo de expiración. Recibe el reloj como parámetro (nunca
/// <see cref="DateTimeOffset.UtcNow"/> internamente) para que los tests controlen el
/// borde exacto del plazo sin timers reales.
/// </summary>
public static class QueryContentDiagnostics
{
    /// <summary>
    /// <c>true</c> sólo si el operador activó el diagnóstico Y configuró una
    /// caducidad Y esa caducidad todavía no llegó (comparación estricta: al instante
    /// exacto de expiración, ya está inactivo). Cualquier otra combinación (apagado,
    /// sin caducidad configurada, caducidad ya pasada) es "no incluir contenido".
    /// </summary>
    public static bool IsActive(LoggingOptions options, DateTimeOffset now) =>
        options.EnableQueryContentDiagnostics
        && options.QueryContentDiagnosticsExpiresAt is { } expiresAt
        && now < expiresAt;

    /// <summary>
    /// <c>true</c> cuando el operador activó el diagnóstico pero quedó inactivo
    /// (caducado o sin fecha) — dispara el aviso de arranque que exige la ficha, para
    /// que "se me olvidó apagarlo" no pase inadvertido.
    /// </summary>
    public static bool IsExpiredOrMisconfigured(LoggingOptions options, DateTimeOffset now) =>
        options.EnableQueryContentDiagnostics && !IsActive(options, now);
}
