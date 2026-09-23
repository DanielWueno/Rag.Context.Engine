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

    // Truncation Metrics (11.1) — fase de embedding de chunks admitidos, no
    // resúmenes ni reintentos. Delta por corrida, nunca reseteado a mano.
    public static readonly Counter<long> ChunksTruncatedTotal = Meter.CreateCounter<long>(
        name: "rag_chunks_truncated_total",
        unit: "{chunks}",
        description: "Total number of admitted chunks whose real token count exceeded the tokenizer's usable capacity (MaxSequenceLength minus special tokens) and were truncated");

    public static readonly Counter<long> TokensDiscardedTotal = Meter.CreateCounter<long>(
        name: "rag_tokens_discarded_total",
        unit: "{tokens}",
        description: "Total number of real (non-special) tokens discarded by truncation across admitted chunks during embedding");

    // Search Metrics
    public static readonly Histogram<double> SearchLatencyMs = Meter.CreateHistogram<double>(
        name: "rag_search_latency_ms",
        unit: "ms",
        description: "Latency of the semantic search operations in milliseconds");

    public static readonly Counter<long> SearchErrorsTotal = Meter.CreateCounter<long>(
        name: "rag_search_errors_total",
        description: "Total number of errors encountered during search");
}
