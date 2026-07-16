using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

/// <summary>
/// Orchestrates the full ingestion pipeline: Scan → Chunk → Vectorize → Index.
/// This is the primary entry point for ingesting a repository.
/// </summary>
public interface IIngestionPipeline
{
    Task<IngestionSummary> IngestRepositoryAsync(
        IngestionRequest request,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
