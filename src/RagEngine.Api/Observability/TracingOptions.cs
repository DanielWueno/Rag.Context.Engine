namespace RagEngine.Api.Observability;

/// <summary>
/// Ítem 13.4: exportación configurable (apagada por defecto) de las trazas ya
/// producidas por <c>RagEngineTracing.ActivitySource</c> ("rag.turn",
/// "rag.retrieval", "rag.rerank", "rag.gate", "rag.context", "rag.generation").
/// <c>Enabled=false</c> (default) preserva el comportamiento anterior a este ítem: el
/// host sigue registrando la fuente de actividad para que los <c>trace_id</c> existan
/// y correlacionen auditoría/logs, pero no exporta ningún span a ningún colector.
///
/// A diferencia de <see cref="MetricsOptions"/>, no hay exportador "Prometheus" para
/// trazas — Prometheus no scrapea spans — así que la única opción soportada es
/// "Otlp" hacia un colector local (Jaeger, otel-collector, etc.).
/// </summary>
public sealed record TracingOptions
{
    public const string SectionName = "Tracing";

    /// <summary>
    /// <c>false</c> (default): no se registra ningún exportador de spans. <c>true</c>:
    /// empuja los spans de <c>RagEngineTracing.ActivitySource</c> al colector de
    /// <see cref="OtlpEndpoint"/>.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Requerido cuando <see cref="Enabled"/> es <c>true</c>. Endpoint del colector
    /// OTLP (p. ej. <c>http://localhost:4317</c>).
    /// </summary>
    public string? OtlpEndpoint { get; init; }
}
