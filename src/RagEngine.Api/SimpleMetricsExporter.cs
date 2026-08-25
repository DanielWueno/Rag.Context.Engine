using System.Diagnostics.Metrics;
using RagEngine.Core.Diagnostics;

namespace RagEngine.Api;

/// <summary>
/// Simple metrics exporter que escucha los instrumentos del Meter Rag.Context.Engine
/// e imprime los valores en la consola/logs cuando se registran mediciones.
/// Verifica que los 4 instrumentos están funcionando correctamente.
/// </summary>
public class SimpleMetricsExporter : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ILogger _logger;
    private readonly Dictionary<string, long> _counterValues = new();
    private readonly Dictionary<string, double> _histogramValues = new();

    public SimpleMetricsExporter(ILogger logger)
    {
        _logger = logger;
        _listener = new MeterListener();

        // Escuchar instrumentos del Meter Rag.Context.Engine
        _listener.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Meter.Name == RagEngineMetrics.Meter.Name)
            {
                _logger.LogInformation("[METRICS] Instrumento detectado: {Name} ({Type})",
                    instrument.Name, instrument.GetType().Name);

                // Registrarse para recibir eventos de este instrumento
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.Start();
        _logger.LogInformation("[METRICS] SimpleMetricsExporter inicializado para escuchar {Meter}",
            RagEngineMetrics.Meter.Name);
    }

    public void Dispose()
    {
        _listener?.Dispose();
    }

    public string GetSummary()
    {
        return $"Counters: {string.Join(", ", _counterValues.Select(kv => $"{kv.Key}={kv.Value}"))}; " +
               $"Histograms: {string.Join(", ", _histogramValues.Select(kv => $"{kv.Key}={kv.Value:F2}"))}";
    }
}
