namespace RagEngine.Core.Domain;

/// <summary>
/// Configuration for the ingestion CLI/host, bound from the "Ingestion" section
/// of appsettings.json. Separate from <see cref="ChunkingOptions"/> (per-request
/// chunking behavior) — this is host-level config for admission and resumen generation.
/// </summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>
    /// Conserva declaraciones de tipos de menos de 60 caracteres. Experimental:
    /// mejora cobertura, pero el A/B de 5.h perdió recall en dos bandas.
    /// Desactivado hasta medir su uso con símbolos y la fusión de tres bandas.
    /// </summary>
    public bool IndexShortTypeDeclarations { get; init; } = false;

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
/// Estadísticas de tokenización de un chunk individual, calculadas con el
/// tokenizador efectivo del <see cref="IVectorizationBrain"/> ANTES de truncar
/// (ítem 11.1). T=TotalTokens cuenta solo tokens no-especiales del texto
/// completo (encabezado enriquecido incluido); L=MaxUsableTokens es la
/// capacidad útil (MaxSequenceLength - tokens especiales del tokenizer
/// efectivo, verificados dinámicamente, no asumidos por nombre de familia).
/// </summary>
public readonly record struct TokenizationStats(
    int TotalTokens,
    int MaxUsableTokens,
    int Discarded,
    bool Truncated);

/// <summary>
/// Resultado de vectorizar un lote junto con las estadísticas de tokenización
/// por chunk (11.1), separado del método simple para no obligar a los demás
/// consumidores de <see cref="IVectorizationBrain"/> a cargar con ellas.
/// </summary>
public sealed record VectorizationBatchResult(
    IReadOnlyList<float[]> Embeddings,
    IReadOnlyList<TokenizationStats> Stats);

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
    int ResumenesSinNegocio = 0,
    /// <summary>n: chunks admitidos con estadística de tokens observada (fase de embedding, sin reintentos).</summary>
    int TokensObserved = 0,
    /// <summary>Percentil 50 (nearest-rank) de T sobre los chunks admitidos de esta corrida. Null si n=0.</summary>
    int? TokensP50 = null,
    /// <summary>Percentil 95 (nearest-rank) de T sobre los chunks admitidos de esta corrida. Null si n=0.</summary>
    int? TokensP95 = null,
    /// <summary>L: capacidad útil de tokens (MaxSequenceLength - especiales) del tokenizador efectivo de esta corrida.</summary>
    int TokensMaxUsable = 0,
    /// <summary>Delta de rag_chunks_truncated_total en esta corrida (chunks con T > L).</summary>
    long ChunksTruncatedTotal = 0,
    /// <summary>Suma de tokens descartados (max(0,T-L)) en esta corrida.</summary>
    long TokensDiscardedTotal = 0
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
