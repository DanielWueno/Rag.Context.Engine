namespace RagEngine.Core.Abstractions;

/// <summary>
/// Puerto de la caché de resúmenes de negocio (clave = content_hash + prompt_version).
/// La capa de aplicación y los hosts dependen de esta interfaz, nunca de la clase
/// concreta SQLite (<c>RagEngine.Core.Services.Summary.SummaryCache</c>) — así un
/// backend distinto (ítem 13.2-kv-a-postgres) se agrega como otro adaptador sin
/// tocar consumidores.
/// </summary>
public interface ISummaryCache
{
    /// <summary>
    /// Hit → (true, resumen) o (true, null) si el hit fue el centinela SIN_CONTENIDO_DE_NEGOCIO.
    /// Miss → (false, null): nunca generado, o el intento anterior falló (los fallos no se cachean).
    /// </summary>
    /// <param name="promptVersionOverride">
    /// Ítem 5.b: namespace de caché distinto para el modo por archivo/tipo. Si es null,
    /// usa el prompt_version fijado al abrir la caché (modo por chunk, default).
    /// </param>
    Task<(bool Found, string? Summary)> TryGetAsync(
        string contentHash, CancellationToken ct = default, string? promptVersionOverride = null);

    /// <summary>Guarda un resumen (o null para el centinela). No llamar en fallos: un fallo debe poder reintentarse.</summary>
    Task SetAsync(
        string contentHash, string? summaryOrSentinel, CancellationToken ct = default, string? promptVersionOverride = null);
}
