namespace RagEngine.Api;

/// <summary>
/// Ítem 12.1: cotas de tasa y concurrencia por actor para las tres rutas de
/// recuperación/generación (<c>/api/search</c>, <c>/api/ask</c>, <c>/api/ask/stream</c>).
/// Bound from the "RateLimiting" section of appsettings.json.
///
/// Sin esto, un solo actor (o un cliente sin identidad en modo Local) podía disparar
/// consultas sin límite — cada una dispara retrieval real contra Qdrant y, en
/// <c>/api/ask</c>/<c>/api/ask/stream</c>, una generación completa contra Ollama.
/// El límite es POR ACTOR (ver <c>Program.ResolveRateLimitKey</c>), nunca global: un
/// tenant saturando su propia cuota no puede bloquear a los demás.
/// </summary>
public sealed record RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Solicitudes permitidas por actor dentro de <see cref="WindowSeconds"/>.</summary>
    public int PermitLimit { get; init; } = 30;

    /// <summary>Tamaño de la ventana deslizante, en segundos.</summary>
    public int WindowSeconds { get; init; } = 60;

    /// <summary>
    /// Solicitudes que pueden encolarse por actor una vez agotado <see cref="PermitLimit"/>
    /// antes de responder 429. 0 = sin cola: rechazo inmediato, la opción más simple de
    /// razonar y la que evita que una cola sin límite consuma memoria/hilos indefinidamente.
    /// </summary>
    public int QueueLimit { get; init; } = 0;

    /// <summary>
    /// Cota de concurrencia (no de tasa) sobre el trabajo costoso de generación
    /// (<c>/api/ask</c>, <c>/api/ask/stream</c> — no <c>/api/search</c>, que no invoca a
    /// Ollama): cuántas generaciones simultáneas admite un mismo actor.
    /// </summary>
    public int MaxConcurrentGenerationsPerActor { get; init; } = 2;

    /// <summary>
    /// Solicitudes de generación que pueden esperar en cola por actor una vez agotada
    /// <see cref="MaxConcurrentGenerationsPerActor"/>. Acotada (no ilimitada) a propósito;
    /// una solicitud en cola se cancela igual que cualquier otra si el cliente cierra la
    /// conexión — el middleware de rate limiting respeta <c>HttpContext.RequestAborted</c>.
    /// </summary>
    public int ConcurrencyQueueLimit { get; init; } = 1;
}
