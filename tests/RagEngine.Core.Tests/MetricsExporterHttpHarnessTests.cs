using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using RagEngine.Core.Diagnostics;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 13.3: contrato del exportador OpenTelemetry configurable sobre el <c>Meter</c>
/// ya existente (<see cref="RagEngineMetrics"/>). Registra mediciones directamente sobre
/// el <c>Meter</c> estático — el MISMO que usa <c>DefaultIngestionPipeline</c> y
/// <c>QdrantSemanticRetriever</c> en producción — y las scrapea a través de un host HTTP
/// real construido desde <c>Program.cs</c> (<see cref="WebApplicationFactory{TEntryPoint}"/>).
/// Sin <c>/api/test-metrics</c>: la fixture es la propia grabación directa del
/// instrumento, exactamente lo que <c>_preparacion_de_verificacion</c> exige.
///
/// Cada assertion usa un valor de tag único por test (GUID) para ser inmune a otros
/// tests que graban en el mismo <c>Meter</c> estático (proceso compartido) en paralelo.
/// </summary>
public sealed class MetricsExporterHttpHarnessTests
{
    [Fact]
    public async Task Exportador_desactivado_por_defecto_no_expone_metrics_ni_cambia_el_servicio()
    {
        using var factory = new MetricsWebApplicationFactory(enabled: false, exporter: "Prometheus");
        using var http = factory.CreateClient();

        using var health = await http.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        // Metrics:Enabled=false (default): /metrics no se mapea. El servicio funciona
        // exactamente igual que antes de este ítem.
        using var metrics = await http.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.NotFound, metrics.StatusCode);
    }

    [Fact]
    public async Task Exportador_prometheus_activo_expone_los_4_instrumentos_con_nombre_tipo_y_unidad_correctos()
    {
        using var factory = new MetricsWebApplicationFactory(enabled: true, exporter: "Prometheus");
        using var http = factory.CreateClient();
        var tag = $"otel-harness-{Guid.NewGuid():N}";

        RagEngineMetrics.ChunksIndexedTotal.Add(3, new KeyValuePair<string, object?>("collection", tag));
        RagEngineMetrics.IngestionErrorsTotal.Add(2, new KeyValuePair<string, object?>("stage", tag));
        RagEngineMetrics.SearchLatencyMs.Record(42.5, new KeyValuePair<string, object?>("collection", tag));
        RagEngineMetrics.SearchErrorsTotal.Add(1, new KeyValuePair<string, object?>("collection", tag));

        var body = await ScrapeAsync(http);

        // Contrato de nombre + tipo + unidad + valor, tal como los declara RagEngineMetrics.
        Assert.Contains("# TYPE rag_chunks_indexed_total counter", body);
        Assert.Contains($"collection=\"{tag}\"}} 3", GetLineFor(body, "rag_chunks_indexed_total", tag));

        Assert.Contains("# TYPE rag_ingestion_errors_total counter", body);
        Assert.Contains($"stage=\"{tag}\"}} 2", GetLineFor(body, "rag_ingestion_errors_total", tag));

        Assert.Contains("# TYPE rag_search_latency_ms_milliseconds histogram", body);
        Assert.Contains($"rag_search_latency_ms_milliseconds_sum{{otel_scope_name=\"Rag.Context.Engine\",otel_scope_version=\"1.0.0\",collection=\"{tag}\"}} 42.5", body);

        Assert.Contains("# TYPE rag_search_errors_total counter", body);
        Assert.Contains($"collection=\"{tag}\"}} 1", GetLineFor(body, "rag_search_errors_total", tag));
    }

    [Fact]
    public async Task No_hay_doble_conteo_una_sola_medicion_produce_un_solo_valor_expuesto()
    {
        using var factory = new MetricsWebApplicationFactory(enabled: true, exporter: "Prometheus");
        using var http = factory.CreateClient();
        var tag = $"otel-harness-nodup-{Guid.NewGuid():N}";

        RagEngineMetrics.ChunksIndexedTotal.Add(5, new KeyValuePair<string, object?>("collection", tag));

        var body = await ScrapeAsync(http);
        var line = GetLineFor(body, "rag_chunks_indexed_total", tag);

        // Exactamente 5, nunca 10: si el listener viejo (3.3-exportador-otel) siguiera
        // corriendo a la vez, este valor se vería duplicado.
        Assert.EndsWith(" 5", line);
    }

    [Fact]
    public void Metrics_Enabled_true_con_Exporter_Otlp_sin_endpoint_no_deja_arrancar_el_host()
    {
        // Program.cs envuelve todo el arranque en un catch(Exception) que loguea Fatal
        // y NO relanza (comportamiento preexistente, compartido por TODOS los
        // IValidateOptions del host, no introducido por este ítem) — así que la
        // OptionsValidationException real queda registrada en logs, y lo observable
        // desde afuera es que el servidor de pruebas nunca llega a arrancar.
        Assert.ThrowsAny<Exception>(() =>
        {
            using var factory = new MetricsWebApplicationFactory(enabled: true, exporter: "Otlp");
            using var http = factory.CreateClient(); // fuerza el arranque perezoso del host
        });
    }

    private static async Task<string> ScrapeAsync(HttpClient http)
    {
        using var response = await http.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static string GetLineFor(string body, string metricName, string tag) =>
        body.Split('\n').FirstOrDefault(l => l.StartsWith(metricName + "{") && l.Contains(tag))
        ?? throw new Xunit.Sdk.XunitException($"No se encontró una línea de '{metricName}' con el tag '{tag}' en:\n{body}");

    private sealed class MetricsWebApplicationFactory(bool enabled, string exporter) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("MetricsExporterHarness");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Metrics:Enabled"] = enabled.ToString(),
                    ["Metrics:Exporter"] = exporter
                });
            });
        }
    }
}
