namespace RagEngine.Core.Domain;

/// <summary>
/// Input request for the ingestion pipeline.
/// </summary>
public sealed record IngestionRequest(
    string RepositoryPath,
    string CollectionName,
    ScanProfile Profile,
    ChunkingOptions Options,
    bool ForceReindex = false
);

/// <summary>
/// Summary statistics produced at the end of an ingestion run.
/// </summary>
public sealed record IngestionSummary(
    int FilesScanned,
    int ChunksGenerated,
    int ChunksIndexed,
    int FilesSkipped,
    TimeSpan TotalDuration,
    long EstimatedMemoryPeakBytes
);

/// <summary>
/// Real-time progress reported during ingestion, consumed by the CLI progress display.
/// </summary>
public sealed record IngestionProgress(
    int FilesProcessed,
    int TotalFilesDiscovered,
    int ChunksProduced,
    int ChunksIndexed,
    string CurrentFile,
    IngestionStage Stage
);

public enum IngestionStage
{
    Scanning,
    Chunking,
    Vectorizing,
    Indexing
}
