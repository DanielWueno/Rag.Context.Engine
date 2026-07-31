using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly IReRanker _reRanker;
    private readonly QdrantVectorStore _vectorStore;
    private readonly RetrievalFusionOptions _fusionOptions;
    private readonly ILogger<QdrantSemanticRetriever> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public QdrantSemanticRetriever(
        QdrantClient client,
        IVectorizationBrain brain,
        ISparseTokenizer sparseTokenizer,
        IReRanker reRanker,
        QdrantVectorStore vectorStore,
        IOptions<RetrievalFusionOptions> fusionOptions,
        ILogger<QdrantSemanticRetriever> logger,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _client = client;
        _brain = brain;
        _sparseTokenizer = sparseTokenizer;
        _reRanker = reRanker;
        _vectorStore = vectorStore;
        _fusionOptions = fusionOptions.Value;
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

            // Decisión 1/6: el schema real de la colección decide la rama de fusión —
            // nunca un flag externo. Colecciones de 2 vectores siguen exactamente el
            // camino nativo de siempre (cero riesgo de regresión); solo las que tienen
            // el tercer vector "dense-resumen" pasan por la fusión ponderada manual.
            var hasSummaryVector = await _vectorStore.HasSummaryVectorAsync(options.CollectionName, cancellationToken);

            IReadOnlyList<ScoredPoint> searchResults;
            if (!hasSummaryVector)
            {
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

                searchResults = await _resiliencePipeline.ExecuteAsync(async ct =>
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
            }
            else
            {
                searchResults = await _resiliencePipeline.ExecuteAsync(async ct =>
                    await SearchWeightedFusionAsync(
                        options.CollectionName, queryVector, sparseValues, sparseIndices,
                        filter, prefetchLimit, finalLimit, payloadSelector, ct),
                    cancellationToken);
            }

            // 5. Map to domain entities
            IReadOnlyList<RetrievalResult> results = searchResults
                .Select(MapToRetrievalResult)
                .ToList();

            // 6. Optional Cross-Encoder re-ranking over the widened pool.
            // El pool 3×TopK que dejó la fusión RRF se re-puntúa par a par
            // (query ↔ chunk) y solo entonces se corta al TopK final. Los
            // scores resultantes son sigmoides del cross-encoder [0..1], no RRF.
            if (options.UseReRanking && results.Count > 0)
            {
                results = await _reRanker.ReRankAsync(
                    query, results, options.TopK, cancellationToken);
            }

            sw.Stop();
            var bestScore = results.FirstOrDefault()?.SimilarityScore ?? 0f;

            RagEngineMetrics.SearchLatencyMs.Record(sw.ElapsedMilliseconds, 
                new KeyValuePair<string, object?>("collection", options.CollectionName));

            _logger.LogInformation("Search completed in {ElapsedMs}ms. Results: {Count}. Best {ScoreKind} score: {BestScore}",
                sw.ElapsedMilliseconds, results.Count, options.UseReRanking ? "cross-encoder" : "fusion", bestScore);
            return results;
        }
        catch (Exception ex)
        {
            RagEngineMetrics.SearchErrorsTotal.Add(1, 
                new KeyValuePair<string, object?>("collection", options.CollectionName));
            _logger.LogError(ex, "Critical failure during hybrid search for query: {Query}", query);
            throw;
        }
    }

    /// <summary>
    /// Decisión 6: fusión RRF ponderada manual sobre 3 ramas independientes (código,
    /// sparse, resumen), porque la fusión nativa <c>Fusion.Rrf</c> de Qdrant no expone
    /// un peso por rama (todas entran con peso igual). Portado y adaptado (índices de
    /// array → PointId string) de <c>RecallEvaluator.RankByRrf</c>/<c>ToRankMap</c> del
    /// PoC (poc/RagEngine.Poc.FreeSearch/RecallEvaluator.cs), con los pesos calibrados
    /// ahí como default de <see cref="RetrievalFusionOptions"/>.
    /// </summary>
    private async Task<IReadOnlyList<ScoredPoint>> SearchWeightedFusionAsync(
        string collectionName,
        float[] queryVector,
        float[] sparseValues,
        uint[] sparseIndices,
        Filter? filter,
        ulong prefetchLimit,
        ulong finalLimit,
        WithPayloadSelector payloadSelector,
        CancellationToken ct)
    {
        var denseTask = _client.QueryAsync(
            collectionName, query: queryVector, usingVector: QdrantVectorStore.DenseVectorName,
            filter: filter, limit: prefetchLimit, payloadSelector: payloadSelector, cancellationToken: ct);
        var sparseTask = _client.QueryAsync(
            collectionName, query: (sparseValues, sparseIndices), usingVector: QdrantVectorStore.SparseVectorName,
            filter: filter, limit: prefetchLimit, payloadSelector: payloadSelector, cancellationToken: ct);
        var resumenTask = _client.QueryAsync(
            collectionName, query: queryVector, usingVector: QdrantVectorStore.SummaryVectorName,
            filter: filter, limit: prefetchLimit, payloadSelector: payloadSelector, cancellationToken: ct);

        await Task.WhenAll(denseTask, sparseTask, resumenTask);

        var denseResults = await denseTask;
        var sparseResults = await sparseTask;
        var resumenResults = await resumenTask;

        var denseRank = ToRankMap(denseResults);
        var sparseRank = ToRankMap(sparseResults);
        var resumenRank = ToRankMap(resumenResults);

        // El mismo punto puede salir en más de una rama con el mismo payload — basta
        // con quedarse con la primera aparición para tener el ScoredPoint completo.
        var pointsById = new Dictionary<string, ScoredPoint>();
        foreach (var point in denseResults.Concat(sparseResults).Concat(resumenResults))
            pointsById.TryAdd(point.Id.Uuid, point);

        var k = _fusionOptions.RrfK;
        var scored = pointsById.Keys.Select(id =>
        {
            double score = 0.0;
            if (denseRank.TryGetValue(id, out var rc)) score += _fusionOptions.WeightCodigo / (k + rc);
            if (sparseRank.TryGetValue(id, out var rs)) score += _fusionOptions.WeightSparse / (k + rs);
            if (resumenRank.TryGetValue(id, out var rr)) score += _fusionOptions.WeightResumen / (k + rr);
            return (Id: id, Score: score);
        })
        .OrderByDescending(x => x.Score)
        .Take((int)finalLimit)
        .ToList();

        var result = new List<ScoredPoint>(scored.Count);
        foreach (var (id, score) in scored)
        {
            var point = pointsById[id];
            point.Score = (float)score; // score RRF ponderado, no similitud coseno cruda
            result.Add(point);
        }
        return result;
    }

    private static Dictionary<string, int> ToRankMap(IReadOnlyList<ScoredPoint> ranked)
    {
        var map = new Dictionary<string, int>(ranked.Count);
        for (int pos = 0; pos < ranked.Count; pos++)
            map[ranked[pos].Id.Uuid] = pos + 1;
        return map;
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
