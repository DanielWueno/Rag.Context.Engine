using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.VectorStore;

/// <summary>
/// Wraps QdrantClient to provide collection management and bulk upsert operations.
/// Uses the gRPC client for lower latency on bulk operations.
/// All upsert operations are idempotent (Upsert, not Insert).
/// </summary>
public sealed class QdrantVectorStore
{
    private readonly QdrantClient _client;
    private readonly ILogger<QdrantVectorStore> _logger;

    public QdrantVectorStore(QdrantClient client, ILogger<QdrantVectorStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Creates the Qdrant collection if it does not already exist.
    /// Uses cosine distance as the similarity metric.
    /// </summary>
    public async Task EnsureCollectionAsync(
        string collectionName,
        int dimension,
        CancellationToken ct = default)
    {
        var collections = await _client.ListCollectionsAsync(ct);
        if (collections.Contains(collectionName))
        {
            _logger.LogInformation("Collection '{Name}' already exists.", collectionName);
            return;
        }

        // CreateCollectionAsync takes VectorParams directly (not VectorsConfig wrapper)
        await _client.CreateCollectionAsync(
            collectionName,
            new VectorParams
            {
                Size = (ulong)dimension,
                Distance = Distance.Cosine,
                OnDisk = false
            },
            cancellationToken: ct);

        _logger.LogInformation(
            "Created Qdrant collection '{Name}' with {Dim}D cosine vectors.",
            collectionName, dimension);
    }

    /// <summary>
    /// Drops and recreates the collection (used with ForceReindex flag).
    /// </summary>
    public async Task RecreateCollectionAsync(
        string collectionName,
        int dimension,
        CancellationToken ct = default)
    {
        var collections = await _client.ListCollectionsAsync(ct);
        if (collections.Contains(collectionName))
        {
            await _client.DeleteCollectionAsync(collectionName, cancellationToken: ct);
            _logger.LogInformation("Deleted existing collection '{Name}'.", collectionName);
        }

        await EnsureCollectionAsync(collectionName, dimension, ct);
    }

    /// <summary>
    /// Upserts a batch of (chunk, vector) pairs into the Qdrant collection.
    /// Each point payload includes all CodeChunkMetadata for retrieval and filtering.
    /// </summary>
    public async Task<int> UpsertBatchAsync(
        string collectionName,
        IReadOnlyList<(CodeChunk Chunk, float[] Vector)> batch,
        CancellationToken ct = default)
    {
        if (batch.Count == 0) return 0;

        var points = batch.Select(item =>
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = item.Chunk.Id.ToString() }
            };

            var vector = new Vector();
            vector.Data.AddRange(item.Vector);
            point.Vectors = new Vectors { Vector = vector };

            point.Payload["content"]          = new Value { StringValue = item.Chunk.Content };
            point.Payload["enriched_content"]  = new Value { StringValue = item.Chunk.EnrichedContent };
            point.Payload["file_path"]         = new Value { StringValue = item.Chunk.Metadata.FilePath };
            point.Payload["relative_path"]     = new Value { StringValue = item.Chunk.Metadata.RelativeFilePath };
            point.Payload["language"]          = new Value { StringValue = item.Chunk.Metadata.Language.ToString() };
            point.Payload["start_line"]        = new Value { IntegerValue = item.Chunk.Metadata.StartLine };
            point.Payload["end_line"]          = new Value { IntegerValue = item.Chunk.Metadata.EndLine };
            point.Payload["chunk_type"]        = new Value { StringValue = item.Chunk.Type.ToString() };
            point.Payload["content_hash"]      = new Value { StringValue = item.Chunk.ContentHash };
            point.Payload["last_modified"]     = new Value { StringValue = item.Chunk.Metadata.LastModified.ToString("O") };
            point.Payload["repository_name"]   = new Value { StringValue = item.Chunk.Metadata.RepositoryName };

            if (item.Chunk.Metadata.Namespace is not null)
                point.Payload["namespace"]  = new Value { StringValue = item.Chunk.Metadata.Namespace };
            if (item.Chunk.Metadata.ClassName is not null)
                point.Payload["class_name"] = new Value { StringValue = item.Chunk.Metadata.ClassName };
            if (item.Chunk.Metadata.MethodName is not null)
                point.Payload["method_name"] = new Value { StringValue = item.Chunk.Metadata.MethodName };

            return point;
        }).ToList();

        await _client.UpsertAsync(collectionName, points, cancellationToken: ct);

        _logger.LogDebug("Upserted {Count} points to collection '{Collection}'.",
            batch.Count, collectionName);

        return batch.Count;
    }
}
