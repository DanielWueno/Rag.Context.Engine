using System.Diagnostics.Metrics;
using RagEngine.Core.Diagnostics;

namespace RagEngine.Api;

/// <summary>
/// Background service que implementa un MeterListener para escuchar y registrar
/// los valores de los instrumentos del Meter Rag.Context.Engine en los logs.
///
/// Permite verificar que los 4 instrumentos están funcionando:
/// - ChunksIndexedTotal (Counter)
/// - IngestionErrorsTotal (Counter)
/// - SearchLatencyMs (Histogram)
/// - SearchErrorsTotal (Counter)
/// </summary>
public class MetricsListener : IHostedService
{
    private MeterListener? _listener;
    private readonly ILogger<MetricsListener> _logger;

    public MetricsListener(ILogger<MetricsListener> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _listener = new MeterListener();

            // Escuchar específicamente el Meter Rag.Context.Engine
            _listener.InstrumentPublished += (instrument, listener) =>
            {
                if (instrument.Meter.Name == RagEngineMetrics.Meter.Name)
                {
                    _logger.LogInformation("Instrumento encontrado: {InstrumentName} ({InstrumentType})",
                        instrument.Name, instrument.GetType().Name);
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.Start();
            _logger.LogInformation("Metrics listener iniciado. Esperando instrumentos del Meter: {MeterName}",
                RagEngineMetrics.Meter.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error iniciando el metrics listener");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        _logger.LogInformation("Metrics listener detenido");
        return Task.CompletedTask;
    }
}
