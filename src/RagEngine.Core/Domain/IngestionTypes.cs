namespace RagEngine.Core.Domain;

/// <summary>
/// Configuration for the ingestion CLI/host, bound from the "Ingestion" section
/// of appsettings.json. Separate from <see cref="ChunkingOptions"/> (per-request
/// chunking behavior) — this is host-level config for the resumen-de-negocio Fase 2.
/// </summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>
    /// Llamadas concurrentes al LLM durante la Fase 2 (generación de resúmenes).
    /// Baja por defecto: un LLM local normalmente no se beneficia de alta
    /// concurrencia y puede saturar CPU/GPU compitiendo consigo mismo.
    /// </summary>
    public int MaxConcurrentResumenCalls { get; init; } = 2;

    /// <summary>Ruta de la caché SQLite de resúmenes, compartida entre todas las colecciones.</summary>
    public string ResumenCachePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "rag-engine", "summary-cache.sqlite3");
}

/// <summary>
/// Input request for the ingestion pipeline.
/// </summary>
public sealed record IngestionRequest(
    string RepositoryPath,
    string CollectionName,
    ScanProfile Profile,
    ChunkingOptions Options,
    bool ForceReindex = false,
    bool EnableResumenLlm = false
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
    long EstimatedMemoryPeakBytes,
    int ResumenesCompleted = 0,
    int ResumenesPending = 0,
    int ResumenesSinNegocio = 0
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
    IngestionStage Stage,
    int ResumenesCompleted = 0,
    int ResumenesTotal = 0
);

public enum IngestionStage
{
    Scanning,
    Chunking,
    Vectorizing,
    Indexing,
    GeneratingResumenes
}
