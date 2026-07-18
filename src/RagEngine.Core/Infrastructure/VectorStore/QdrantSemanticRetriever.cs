using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Diagnostics;
using Polly;
using Polly.Registry;

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
    private readonly ResiliencePipeline _resiliencePipeline;

    public QdrantSemanticRetriever(
        QdrantClient client,
        IVectorizationBrain brain,
        ISparseTokenizer sparseTokenizer,
        ILogger<QdrantSemanticRetriever> logger,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _client = client;
        _brain = brain;
        _sparseTokenizer = sparseTokenizer;
        _logger = logger;
        _resiliencePipeline = pipelineProvider.GetPipeline("qdrant");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        string query,
        RetrievalOptions options,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var _logContext1 = Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId);
        using var _logContext2 = Serilog.Context.LogContext.PushProperty("Collection", options.CollectionName);

        try
        {
            _logger.LogInformation(
                "Semantic search: '{Query}' | Collection: {Col} | TopK: {K} | Rerank: {UseReRanking}",
                query, options.CollectionName, options.TopK, options.UseReRanking);

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
            // El pool de candidatos de cada rama (prefetch) debe ser varias veces
            // más ancho que el corte final: RRF premia el consenso entre ramas, y
            // con listas de tamaño TopK solo puede intercalar dos listas cortas.
            ulong finalLimit = (ulong)(options.UseReRanking ? options.TopK * 3 : options.TopK);
            ulong prefetchLimit = Math.Max(finalLimit * 4, 40);
            var payloadSelector = new WithPayloadSelector { Enable = true };

            // MinimumSimilarityScore se aplica SOLO al prefetch denso, donde el score
            // sigue siendo similitud coseno [0..1]. No se aplica al prefetch disperso
            // (sus scores son dot-products TF sin escala comparable) ni al score RRF
            // final (que es función del ranking, no de la similitud).
            var densePrefetch = new PrefetchQuery
            {
                Query = queryVector,
                Using = QdrantVectorStore.DenseVectorName,
                Filter = filter,
                Limit = prefetchLimit
            };
            if (options.MinimumSimilarityScore > 0f)
                densePrefetch.ScoreThreshold = options.MinimumSimilarityScore;

            var searchResults = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                return await _client.QueryAsync(
                    collectionName: options.CollectionName,
                    query: new Query { Fusion = Fusion.Rrf },
                    prefetch: new[]
                    {
                        densePrefetch,
                        new PrefetchQuery
                        {
                            Query = (sparseValues, sparseIndices),
                            Using = QdrantVectorStore.SparseVectorName,
                            Filter = filter,
                            Limit = prefetchLimit
                        }
                    },
                    limit: finalLimit,
                    payloadSelector: payloadSelector,
                    cancellationToken: ct);
            }, cancellationToken);

            // 4. Map to domain entities
            var results = searchResults
                .Select(MapToRetrievalResult)
                .ToList();

            sw.Stop();
            var bestScore = results.FirstOrDefault()?.SimilarityScore ?? 0f;

            RagEngineMetrics.SearchLatencyMs.Record(sw.ElapsedMilliseconds, 
                new KeyValuePair<string, object?>("collection", options.CollectionName));

            _logger.LogInformation("Search completed in {ElapsedMs}ms. Results: {Count}. Best fusion score: {BestScore}", 
                sw.ElapsedMilliseconds, results.Count, bestScore);
            return results.AsReadOnly();
        }
        catch (Exception ex)
        {
            RagEngineMetrics.SearchErrorsTotal.Add(1, 
                new KeyValuePair<string, object?>("collection", options.CollectionName));
            _logger.LogError(ex, "Critical failure during hybrid search for query: {Query}", query);
            throw;
        }
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
