using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.VectorStore;

/// <summary>
/// Implements semantic search against Qdrant using the gRPC client.
/// Vectorizes the query via IVectorizationBrain, then queries Qdrant's
/// HNSW index with optional metadata filters.
/// </summary>
public sealed class QdrantSemanticRetriever : ISemanticRetriever
{
    private readonly QdrantClient _client;
    private readonly IVectorizationBrain _brain;
    private readonly ISparseTokenizer _sparseTokenizer;
    private readonly ILogger<QdrantSemanticRetriever> _logger;

    public QdrantSemanticRetriever(
        QdrantClient client,
        IVectorizationBrain brain,
        ISparseTokenizer sparseTokenizer,
        ILogger<QdrantSemanticRetriever> logger)
    {
        _client = client;
        _brain = brain;
        _sparseTokenizer = sparseTokenizer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        string query,
        RetrievalOptions options,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Semantic search: '{Query}' | Collection: {Col} | TopK: {K}",
            query, options.CollectionName, options.TopK);

        // 1. Vectorize query (Dense)
        var queryVector = await _brain.GenerateEmbeddingAsync(query, cancellationToken);

        // 2. Tokenize query (Sparse)
        var sparseEntries = _sparseTokenizer.Tokenize(query);
        float[] sparseValues = new float[sparseEntries.Count];
        uint[] sparseIndices = new uint[sparseEntries.Count];
        for (int i = 0; i < sparseEntries.Count; i++)
        {
            sparseIndices[i] = sparseEntries[i].TermIndex;
            sparseValues[i] = sparseEntries[i].Weight;
        }

        // 3. Build Qdrant filter from RetrievalOptions
        var filter = BuildFilter(options);

        // 4. Execute Hybrid Search using Prefetch and RRF Fusion
        ulong fetchLimit = (ulong)(options.UseReRanking ? options.TopK * 3 : options.TopK);
        var payloadSelector = new WithPayloadSelector { Enable = true };

        var searchResults = await _client.QueryAsync(
            collectionName: options.CollectionName,
            query: new Query { Fusion = Fusion.Rrf },
            prefetch: new[]
            {
                new PrefetchQuery
                {
                    Query = queryVector,
                    Using = QdrantVectorStore.DenseVectorName,
                    Filter = filter,
                    Limit = fetchLimit
                },
                new PrefetchQuery
                {
                    Query = (sparseValues, sparseIndices),
                    Using = QdrantVectorStore.SparseVectorName,
                    Filter = filter,
                    Limit = fetchLimit
                }
            },
            limit: fetchLimit,
            payloadSelector: payloadSelector,
            cancellationToken: cancellationToken);

        // 4. Map to domain entities
        var results = searchResults
            .Select(MapToRetrievalResult)
            .ToList();

        _logger.LogInformation("Search returned {Count} results.", results.Count);
        return results.AsReadOnly();
    }

    private static Filter? BuildFilter(RetrievalOptions options)
    {
        var conditions = new List<Condition>();

        if (options.FilterByLanguage.HasValue)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "language",
                    Match = new Match { Text = options.FilterByLanguage.Value.ToString() }
                }
            });
        }

        if (!string.IsNullOrWhiteSpace(options.FilterByNamespace))
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "namespace",
                    Match = new Match { Text = options.FilterByNamespace }
                }
            });
        }

        return conditions.Count == 0
            ? null
            : new Filter { Must = { conditions } };
    }

    private static RetrievalResult MapToRetrievalResult(ScoredPoint point)
    {
        var p = point.Payload;

        return new RetrievalResult(
            ChunkId: point.Id.ToString()!,
            Content: p["content"].StringValue,
            SimilarityScore: point.Score,
            Metadata: new CodeChunkMetadata(
                FilePath: p["file_path"].StringValue,
                RelativeFilePath: p.GetValueOrDefault("relative_path")?.StringValue ?? string.Empty,
                Language: Enum.Parse<SourceLanguage>(p["language"].StringValue),
                Namespace: p.GetValueOrDefault("namespace")?.StringValue,
                ClassName: p.GetValueOrDefault("class_name")?.StringValue,
                MethodName: p.GetValueOrDefault("method_name")?.StringValue,
                StartLine: (int)p["start_line"].IntegerValue,
                EndLine: (int)p["end_line"].IntegerValue,
                LastModified: DateTimeOffset.Parse(p["last_modified"].StringValue),
                RepositoryName: p["repository_name"].StringValue
            )
        );
    }
}
