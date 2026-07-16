using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Chunking;
using RagEngine.Core.Infrastructure.VectorStore;

namespace RagEngine.Core.Pipeline;

/// <summary>
/// Orchestrates the full ingestion pipeline:
///   Scanner → Chunker → Channel&lt;CodeChunk&gt; → ONNX Batch → Qdrant Upsert
///
/// Uses a bounded Channel as a backpressure buffer between the producer
/// (scanner + chunker) and consumer (vectorizer + indexer), preventing OOM
/// on massive repositories.
/// </summary>
public sealed class DefaultIngestionPipeline : IIngestionPipeline
{
    private const int ChannelCapacity = 512;

    private readonly IIngestionScanner _scanner;
    private readonly ChunkingStrategyRouter _chunkRouter;
    private readonly IVectorizationBrain _brain;
    private readonly QdrantVectorStore _vectorStore;
    private readonly ILogger<DefaultIngestionPipeline> _logger;

    public DefaultIngestionPipeline(
        IIngestionScanner scanner,
        ChunkingStrategyRouter chunkRouter,
        IVectorizationBrain brain,
        QdrantVectorStore vectorStore,
        ILogger<DefaultIngestionPipeline> logger)
    {
        _scanner = scanner;
        _chunkRouter = chunkRouter;
        _brain = brain;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IngestionSummary> IngestRepositoryAsync(
        IngestionRequest request,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var stats = new PipelineStats();

        _logger.LogInformation(
            "Starting ingestion: {Path} → collection '{Collection}' | ForceReindex: {Force}",
            request.RepositoryPath, request.CollectionName, request.ForceReindex);

        // ── Step 1: Prepare collection ──────────────────────────────────────────
        if (request.ForceReindex)
            await _vectorStore.RecreateCollectionAsync(
                request.CollectionName, _brain.EmbeddingDimensions, cancellationToken);
        else
            await _vectorStore.EnsureCollectionAsync(
                request.CollectionName, _brain.EmbeddingDimensions, cancellationToken);

        // ── Step 2: Producer/Consumer via bounded Channel ───────────────────────
        var channel = Channel.CreateBounded<CodeChunk>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        var producerTask = ProduceChunksAsync(
            request, channel.Writer, progress, stats, cancellationToken);

        var consumerTask = ConsumeAndIndexAsync(
            request.CollectionName, channel.Reader,
            request.Options.BatchSize, progress, stats, cancellationToken);

        // Run both tasks concurrently; propagate any exception
        await Task.WhenAll(producerTask, consumerTask);

        sw.Stop();

        var summary = new IngestionSummary(
            FilesScanned: stats.FilesScanned,
            ChunksGenerated: stats.ChunksGenerated,
            ChunksIndexed: stats.ChunksIndexed,
            FilesSkipped: stats.FilesSkipped,
            TotalDuration: sw.Elapsed,
            EstimatedMemoryPeakBytes: GC.GetTotalMemory(false));

        _logger.LogInformation(
            "Ingestion complete. Files: {Files}, Chunks: {Chunks}, Indexed: {Indexed}, Duration: {Elapsed}",
            summary.FilesScanned, summary.ChunksGenerated, summary.ChunksIndexed, summary.TotalDuration);

        return summary;
    }

    // ── Producer: Scan → Read → Chunk → Write to Channel ───────────────────────
    private async Task ProduceChunksAsync(
        IngestionRequest request,
        ChannelWriter<CodeChunk> writer,
        IProgress<IngestionProgress>? progress,
        PipelineStats stats,
        CancellationToken ct)
    {
        try
        {
            await foreach (var artifact in _scanner.ScanAsync(
                request.RepositoryPath, request.Profile, ct))
            {
                progress?.Report(new IngestionProgress(
                    FilesProcessed: stats.FilesScanned,
                    TotalFilesDiscovered: 0,
                    ChunksProduced: stats.ChunksGenerated,
                    ChunksIndexed: stats.ChunksIndexed,
                    CurrentFile: artifact.RelativePath,
                    Stage: IngestionStage.Scanning));

                string content;
                try
                {
                    content = await File.ReadAllTextAsync(artifact.AbsolutePath, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read {File}", artifact.AbsolutePath);
                    Interlocked.Increment(ref stats.FilesSkipped);
                    continue;
                }

                Interlocked.Increment(ref stats.FilesScanned);

                var strategy = _chunkRouter.GetStrategy(artifact);

                try
                {
                    await foreach (var chunk in strategy.ChunkAsync(
                        artifact, content, request.Options, ct))
                    {
                        await writer.WriteAsync(chunk, ct);
                        Interlocked.Increment(ref stats.ChunksGenerated);

                        progress?.Report(new IngestionProgress(
                            FilesProcessed: stats.FilesScanned,
                            TotalFilesDiscovered: 0,
                            ChunksProduced: stats.ChunksGenerated,
                            ChunksIndexed: stats.ChunksIndexed,
                            CurrentFile: artifact.RelativePath,
                            Stage: IngestionStage.Chunking));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to chunk {File}", artifact.AbsolutePath);
                    Interlocked.Increment(ref stats.FilesSkipped);
                }

                // Explicit GC hint after processing large C# files with Roslyn
                if (artifact.SizeBytes > 100_000)
                    GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    // ── Consumer: Read from Channel → Batch → ONNX → Qdrant Upsert ────────────
    private async Task ConsumeAndIndexAsync(
        string collectionName,
        ChannelReader<CodeChunk> reader,
        int batchSize,
        IProgress<IngestionProgress>? progress,
        PipelineStats stats,
        CancellationToken ct)
    {
        var batch = new List<CodeChunk>(batchSize);

        await foreach (var chunk in reader.ReadAllAsync(ct))
        {
            batch.Add(chunk);

            if (batch.Count >= batchSize)
            {
                await ProcessBatchAsync(collectionName, batch, progress, stats, ct);
                batch.Clear();
            }
        }

        // Flush remaining chunks
        if (batch.Count > 0)
            await ProcessBatchAsync(collectionName, batch, progress, stats, ct);
    }

    private async Task ProcessBatchAsync(
        string collectionName,
        List<CodeChunk> batch,
        IProgress<IngestionProgress>? progress,
        PipelineStats stats,
        CancellationToken ct)
    {
        // Vectorize (using EnrichedContent for better embedding quality)
        var texts = batch.Select(c => c.EnrichedContent).ToList();

        float[][] vectors;
        try
        {
            var result = await _brain.GenerateBatchEmbeddingsAsync(texts, ct);
            vectors = result.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ONNX batch embedding failed for {Count} chunks.", batch.Count);
            return;
        }

        var pairs = batch
            .Zip(vectors, (chunk, vec) => (Chunk: chunk, Vector: vec))
            .ToList();

        try
        {
            await _vectorStore.UpsertBatchAsync(collectionName, pairs, ct);
            Interlocked.Add(ref stats.ChunksIndexed, batch.Count);

            progress?.Report(new IngestionProgress(
                FilesProcessed: stats.FilesScanned,
                TotalFilesDiscovered: 0,
                ChunksProduced: stats.ChunksGenerated,
                ChunksIndexed: stats.ChunksIndexed,
                CurrentFile: string.Empty,
                Stage: IngestionStage.Indexing));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Qdrant upsert failed for batch of {Count} chunks.", batch.Count);
        }
    }

    // Mutable stats class shared between producer and consumer via Interlocked
    private sealed class PipelineStats
    {
        public int FilesScanned;
        public int ChunksGenerated;
        public int ChunksIndexed;
        public int FilesSkipped;
    }
}
