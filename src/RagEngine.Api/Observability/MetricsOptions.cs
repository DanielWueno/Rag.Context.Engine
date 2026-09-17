namespace RagEngine.Api.Observability;

/// <summary>
/// Ítem 13.3: exportación configurable (apagada por defecto) de los instrumentos ya
/// definidos en <c>RagEngineMetrics</c>. <c>Enabled=false</c> (default) preserva el
/// comportamiento anterior a este ítem: el <c>Meter</c> sigue emitiendo, pero nada lo
/// recoge — cero endpoints nuevos, cero exportadores, cero overhead adicional.
/// </summary>
public sealed record MetricsOptions
{
    public const string SectionName = "Metrics";

    /// <summary>
    /// <c>false</c> (default): no se registra ningún <c>MeterProvider</c> ni exportador.
    /// <c>true</c>: habilita exactamente UN exportador (<see cref="Exporter"/>) sobre el
    /// mismo <c>Meter</c> ya existente — nunca ambos a la vez, para no duplicar conteos.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// "Prometheus" (default) expone un endpoint de scrape local en <c>/metrics</c>,
    /// sujeto a la misma política de exposición de red que el resto del host (ver
    /// <c>Transport:Published</c> en docs/operaciones.md). "Otlp" empuja las métricas a
    /// un colector externo vía <see cref="OtlpEndpoint"/>.
    /// </summary>
    public string Exporter { get; init; } = "Prometheus";

    /// <summary>
    /// Solo se lee cuando <see cref="Enabled"/> es <c>true</c> y <see cref="Exporter"/>
    /// es "Otlp". Endpoint del colector OTLP (p. ej. <c>http://localhost:4317</c>).
    /// </summary>
    public string? OtlpEndpoint { get; init; }
}
