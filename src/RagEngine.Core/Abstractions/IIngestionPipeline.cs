using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

/// <summary>
/// Central orchestrator of the ingestion pipeline.
/// Manages backpressure via Channel&lt;CodeChunk&gt; between the scanner,
/// chunker, vectorizer and Qdrant upsert stages.
/// </summary>
public interface IIngestionPipeline
{
    /// <summary>
    /// Runs the full ingestion pipeline for the given source path.
    /// </summary>
    /// <param name="sourcePath">Root directory of the codebase to ingest.</param>
    /// <param name="collectionName">Target Qdrant collection name.</param>
    /// <param name="options">Runtime options (batch size, language filters, etc.).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A summary of the ingestion run.</returns>
    Task<IngestionResult> RunAsync(
        string sourcePath,
        string collectionName,
        IngestionOptions options,
        CancellationToken cancellationToken = default);
}
