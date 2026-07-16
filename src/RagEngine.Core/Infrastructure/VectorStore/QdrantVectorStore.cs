using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.VectorStore;

/// <summary>
/// Wraps QdrantClient to provide collection management and bulk upsert operations.
/// Uses the gRPC client for lower latency on bulk operations.
/// All upsert operations are idempotent (Upsert, not Insert).
/// </summary>
public sealed class QdrantVectorStore
{
    public const string DenseVectorName = "dense";
    public const string SparseVectorName = "sparse-code";

    private readonly QdrantClient _client;
    private readonly ILogger<QdrantVectorStore> _logger;

    public QdrantVectorStore(QdrantClient client, ILogger<QdrantVectorStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Creates the Qdrant collection if it does not already exist.
    /// Configures a dual schema: dense (cosine) and sparse (TF).
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

        await CreateHybridCollectionAsync(collectionName, dimension, ct);
    }

    private async Task CreateHybridCollectionAsync(
        string collectionName,
        int dimension,
        CancellationToken ct)
    {
        var vectorsConfig = new VectorParamsMap();
        vectorsConfig.Map[DenseVectorName] = new VectorParams
        {
            Size = (ulong)dimension,
            Distance = Distance.Cosine,
            OnDisk = false
        };

        var sparseConfig = new SparseVectorConfig();
        sparseConfig.Map[SparseVectorName] = new SparseVectorParams
        {
            Index = new SparseIndexConfig { FullScanThreshold = 5000 }
        };

        await _client.CreateCollectionAsync(
            collectionName,
            vectorsConfig: vectorsConfig,
            sparseVectorsConfig: sparseConfig,
            cancellationToken: ct);

        _logger.LogInformation(
            "Created Qdrant hybrid collection '{Name}' with {Dim}D dense + sparse vectors.",
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

        await CreateHybridCollectionAsync(collectionName, dimension, ct);
    }

    /// <summary>
    /// Upserts a batch of points containing both dense and sparse vectors.
    /// </summary>
    public async Task<int> UpsertBatchAsync(
        string collectionName,
        IReadOnlyList<(CodeChunk Chunk, float[] DenseVector, IReadOnlyList<SparseEntry> SparseVector)> batch,
        CancellationToken ct = default)
    {
        if (batch.Count == 0) return 0;

        var points = batch.Select(item =>
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = item.Chunk.Id.ToString() }
            };

            var denseVec = new Vector();
            denseVec.Data.AddRange(item.DenseVector);

            float[] sparseValues = new float[item.SparseVector.Count];
            uint[] sparseIndices = new uint[item.SparseVector.Count];
            for (int i = 0; i < item.SparseVector.Count; i++)
            {
                sparseIndices[i] = item.SparseVector[i].TermIndex;
                sparseValues[i] = item.SparseVector[i].Weight;
            }

            Vector sparseVec = (sparseValues, sparseIndices);

            var namedVectors = new NamedVectors();
            namedVectors.Vectors[DenseVectorName] = denseVec;
            namedVectors.Vectors[SparseVectorName] = sparseVec;

            point.Vectors = new Vectors { Vectors_ = namedVectors };

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
