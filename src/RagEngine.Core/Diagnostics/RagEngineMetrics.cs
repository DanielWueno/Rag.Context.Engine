using System.Diagnostics.Metrics;

namespace RagEngine.Core.Diagnostics;

/// <summary>
/// Centralized metrics for the Rag.Context.Engine using System.Diagnostics.Metrics.
/// These instruments can be collected by OpenTelemetry, dotnet-counters, or Prometheus.
/// </summary>
public static class RagEngineMetrics
{
    public static readonly Meter Meter = new Meter("Rag.Context.Engine", "1.0.0");

    // Ingestion Metrics
    public static readonly Counter<long> ChunksIndexedTotal = Meter.CreateCounter<long>(
        name: "rag_chunks_indexed_total",
        unit: "{chunks}",
        description: "Total number of chunks successfully indexed in Qdrant");

    public static readonly Counter<long> IngestionErrorsTotal = Meter.CreateCounter<long>(
        name: "rag_ingestion_errors_total",
        description: "Total number of errors encountered during ingestion");

    // Search Metrics
    public static readonly Histogram<double> SearchLatencyMs = Meter.CreateHistogram<double>(
        name: "rag_search_latency_ms",
        unit: "ms",
        description: "Latency of the semantic search operations in milliseconds");

    public static readonly Counter<long> SearchErrorsTotal = Meter.CreateCounter<long>(
        name: "rag_search_errors_total",
        description: "Total number of errors encountered during search");
}
