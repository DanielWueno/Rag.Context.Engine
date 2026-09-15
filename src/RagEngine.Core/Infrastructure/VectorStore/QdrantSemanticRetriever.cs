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
    private readonly TwoHopOptions _twoHopOptions;
    private readonly ILogger<QdrantSemanticRetriever> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public QdrantSemanticRetriever(
        QdrantClient client,
        IVectorizationBrain brain,
        ISparseTokenizer sparseTokenizer,
        IReRanker reRanker,
        QdrantVectorStore vectorStore,
        IOptions<RetrievalFusionOptions> fusionOptions,
        IOptions<TwoHopOptions> twoHopOptions,
        ILogger<QdrantSemanticRetriever> logger,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _client = client;
        _brain = brain;
        _sparseTokenizer = sparseTokenizer;
        _reRanker = reRanker;
        _vectorStore = vectorStore;
        _fusionOptions = fusionOptions.Value;
        _twoHopOptions = twoHopOptions.Value;
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
            //
            // Ítem 6.a: con re-ranking desactivado, un candidato del segundo salto por
            // símbolo NUNCA podía ganarle a un puesto primario dentro de un pool de
            // tamaño exacto TopK (incluso el peor rango primario, TopK, puntúa más que
            // el mejor rango posible del salto en la re-fusión RRF de segundo nivel de
            // ExpandBySymbolAsync). Sin este margen extra, el salto medía siempre cero
            // efecto. Con TwoHop activo se ensancha el pool primario (finalLimit) para
            // dejarle espacio real de competir; el recorte final a options.TopK se hace
            // después del salto (ver el final de este método), no aquí.
            //
            // prefetchLimit se calcula SOBRE baseFinalLimit (sin el ensanchado de 6.a),
            // no sobre finalLimit: si dependiera de finalLimit, activar TwoHop también
            // profundizaría el prefetch de CADA rama (código/sparse/resumen) y eso por
            // sí solo reordena el top-K de la fusión primaria — un confounder que
            // contaminaría cualquier comparación ON/OFF sin que el salto por símbolo
            // haya intervenido. Detectado revisando el primer A/B de este ítem, donde
            // los deltas medidos resultaron ser el efecto del prefetch más profundo, no
            // del salto (ver resultado del ítem en el ledger).
            ulong baseFinalLimit = (ulong)(options.UseReRanking ? options.TopK * 3 : options.TopK);
            ulong prefetchLimit = Math.Max(baseFinalLimit * 4, 40);
            ulong finalLimit = (!options.UseReRanking && _twoHopOptions.Enabled)
                ? baseFinalLimit + (ulong)_twoHopOptions.MaxExpansionResults
                : baseFinalLimit;
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
            // La escala del score la fija la rama de fusión que acaba de correr, y viaja
            // con cada resultado (ítem 4.9): ninguna de las dos es similitud coseno, y
            // sus magnitudes tampoco coinciden entre sí porque la ponderada no usa pesos
            // 1/1/1. Sin este dato el consumidor tenía que deducirlo del código de aquí.
            var fusionScale = hasSummaryVector
                ? RetrievalScoreScale.RankFusionWeighted
                : RetrievalScoreScale.RankFusionNative;

            IReadOnlyList<RetrievalResult> results = searchResults
                .Select(point => MapToRetrievalResult(point, fusionScale))
                .ToList();

            // 5.b Ítem 6.a: expansión por símbolo. Apagado por defecto
            // (TwoHopOptions.Enabled=false) — con el flag en false este bloque no
            // ejecuta ningún QueryAsync adicional y el comportamiento es idéntico al
            // de antes de 6.a (ver rollback de la ficha).
            if (_twoHopOptions.Enabled && searchResults.Count > 0)
            {
                results = await ExpandBySymbolAsync(
                    options, queryVector, filter, searchResults, results, cancellationToken);
            }

            // 6. Optional Cross-Encoder re-ranking over the widened pool.
            // El pool 3×TopK que dejó la fusión RRF se re-puntúa par a par
            // (query ↔ chunk) y solo entonces se corta al TopK final. Los
            // scores resultantes son sigmoides del cross-encoder [0..1], no RRF.
            if (options.UseReRanking && results.Count > 0)
            {
                results = await _reRanker.ReRankAsync(
                    query, results, options.TopK, cancellationToken);
            }
            else if (results.Count > options.TopK)
            {
                // Sin re-ranking, el corte al contrato de TopK se hace aquí y no antes:
                // el pool que llega a este punto pudo ensancharse (finalLimit, arriba)
                // para darle espacio real al salto por símbolo de 6.a a competir en la
                // re-fusión de ExpandBySymbolAsync. Con TwoHop apagado, results.Count ya
                // es exactamente TopK y este Take() es un no-op.
                results = results.Take(options.TopK).ToList();
            }

            sw.Stop();
            var best = results.FirstOrDefault();
            var bestScore = best?.SimilarityScore ?? 0f;
            var bestScale = best?.ScoreScale ?? fusionScale;

            RagEngineMetrics.SearchLatencyMs.Record(sw.ElapsedMilliseconds, 
                new KeyValuePair<string, object?>("collection", options.CollectionName));

            _logger.LogInformation("Search completed in {ElapsedMs}ms. Results: {Count}. Best {ScoreKind} score: {BestScore}",
                sw.ElapsedMilliseconds, results.Count, bestScale.ToDisplayName(), bestScore);
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

    /// <summary>
    /// Ítem 6.a: segundo salto símbolo→definición. Cosecha <c>consumed_symbols</c> de
    /// los primeros <see cref="TwoHopOptions.SeedResults"/> resultados de la fusión
    /// primaria (<paramref name="primaryPoints"/>) y relanza un <c>QueryAsync</c> denso
    /// —mismo vector de consulta, mismos filtros de <paramref name="filter"/>— pero
    /// restringido a puntos cuyo <c>defined_symbols</c> contenga alguno de esos
    /// símbolos (índice keyword de 5.d, sin escanear la colección).
    ///
    /// El resultado NO se concatena a ciegas: se re-funde con la fusión primaria en
    /// una RRF de segundo nivel (<see cref="MergeSymbolExpansionRanks"/>) con su propio
    /// <c>k</c> — <b>deliberadamente NO el <see cref="RetrievalFusionOptions.RrfK"/> de
    /// la fusión primaria (60), calibrado para ramas de ~4×TopK candidatos</b>: sobre
    /// pools del orden de TopK, ese k satura la fórmula y ningún candidato del salto
    /// puede matemáticamente entrar al resultado final por debajo de un umbral de peso
    /// muy alto (~0.87) — lo verificó una revisión externa antes de publicar 6.a. Se usa
    /// <c>options.TopK</c> como k de este segundo nivel: a esa escala, un candidato del
    /// salto en la mejor posición del segundo hop sólo desplaza a puestos primarios
    /// débiles (más allá de la mitad del pool), nunca a los primeros puestos — sin tocar
    /// los tres pesos de <see cref="RetrievalFusionOptions"/>, que calibran la fusión
    /// primaria y no se recalculan aquí.
    /// </summary>
    private async Task<IReadOnlyList<RetrievalResult>> ExpandBySymbolAsync(
        RetrievalOptions options,
        float[] queryVector,
        Filter? filter,
        IReadOnlyList<ScoredPoint> primaryPoints,
        IReadOnlyList<RetrievalResult> primaryResults,
        CancellationToken cancellationToken)
    {
        var seedCount = Math.Min(_twoHopOptions.SeedResults, primaryPoints.Count);
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < seedCount && symbols.Count < MaxSeedSymbols; i++)
        {
            if (primaryPoints[i].Payload.TryGetValue(QdrantVectorStore.ConsumedSymbolsPayloadKey, out var value)
                && value.KindCase == Value.KindOneofCase.ListValue)
            {
                foreach (var symbolValue in value.ListValue.Values)
                {
                    if (symbols.Count >= MaxSeedSymbols) break;
                    if (!string.IsNullOrWhiteSpace(symbolValue.StringValue))
                        symbols.Add(symbolValue.StringValue);
                }
            }
        }

        // Sin símbolos consumidos en la semilla no hay hacia dónde saltar: se devuelve
        // el conjunto primario tal cual, sin gastar un QueryAsync adicional.
        if (symbols.Count == 0)
        {
            _logger.LogDebug("Two-hop (6.a): sin consumed_symbols en los primeros {SeedCount} resultados; sin salto.", seedCount);
            return primaryResults;
        }

        // Filter es un mensaje protobuf: Clone() copia también MinShould (repeated Must/
        // MustNot/Should no bastan si el filtro original algún día llega a usar esa
        // condición, p.ej. un futuro filtro de tenant expresado como mínimo-N-de-M).
        var hopFilter = filter?.Clone() ?? new Filter();
        hopFilter.Must.Add(Conditions.Match(QdrantVectorStore.DefinedSymbolsPayloadKey, symbols.ToList()));

        IReadOnlyList<ScoredPoint> hopPoints;
        try
        {
            hopPoints = await _resiliencePipeline.ExecuteAsync(async ct =>
                await _client.QueryAsync(
                    collectionName: options.CollectionName,
                    query: queryVector,
                    usingVector: QdrantVectorStore.DenseVectorName,
                    filter: hopFilter,
                    limit: (ulong)_twoHopOptions.MaxExpansionResults,
                    payloadSelector: new WithPayloadSelector { Enable = true },
                    cancellationToken: ct),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // El salto por símbolo es una ampliación de recall, no el camino crítico:
            // si el segundo QueryAsync falla, la búsqueda no debe fallar por completo —
            // se degrada al resultado de la fusión primaria, igual que si Enabled=false.
            // Una cancelación real (timeout/usuario) sí debe propagarse: no es un fallo
            // del salto, es que ya nadie espera esta respuesta.
            _logger.LogWarning(ex,
                "Symbol expansion (two-hop, item 6.a) query failed for collection {Col}; falling back to the primary fusion result set.",
                options.CollectionName);
            return primaryResults;
        }

        if (hopPoints.Count == 0)
        {
            _logger.LogDebug("Two-hop (6.a): {SymbolCount} símbolos cosechados, 0 puntos definidores encontrados.", symbols.Count);
            return primaryResults;
        }

        // OJO: a diferencia de una primera versión de este método, aquí NO se descarta
        // la intersección entre primaryPoints y hopPoints antes de calcular rangos. Un
        // punto que aparece en AMBAS listas es la señal de consenso más fuerte que este
        // mecanismo puede producir (semánticamente cercano a la consulta Y define un
        // símbolo que la propia consulta ya estaba consumiendo) — descartarla antes de
        // sumar los dos términos de la RRF de segundo nivel tira esa señal a la basura.
        var primaryRank = ToRankMap(primaryPoints);
        var hopRank = ToRankMap(hopPoints);
        var k2 = options.TopK;

        var mergedIds = MergeSymbolExpansionRanks(
            primaryRank, hopRank, _twoHopOptions.Weight, k2, primaryResults.Count);

        var primaryResultByPointId = new Dictionary<string, RetrievalResult>(primaryPoints.Count);
        for (int i = 0; i < primaryPoints.Count; i++)
            primaryResultByPointId.TryAdd(primaryPoints[i].Id.Uuid, primaryResults[i]);

        var hopPointById = new Dictionary<string, ScoredPoint>(hopPoints.Count);
        foreach (var p in hopPoints)
            hopPointById.TryAdd(p.Id.Uuid, p);

        var merged = new List<RetrievalResult>(mergedIds.Count);
        var newFromHop = 0;
        foreach (var id in mergedIds)
        {
            // Un id presente en la fusión primaria conserva EXACTAMENTE su
            // RetrievalResult (score y escala originales, RankFusionNative/Weighted):
            // el salto sólo decide su POSICIÓN en la lista fusionada, no reinventa su
            // puntuación. Sólo los ids que llegaron ÚNICAMENTE por el segundo salto se
            // mapean con la escala SymbolExpansion — su score es el coseno crudo de ESE
            // QueryAsync (comparable entre consultas, a diferencia de un RRF), y es
            // honesto dejarlo así en vez de inventar un score sintético de "RRF de
            // segundo nivel" que ningún consumidor sabría interpretar. El mismo patrón
            // de lista con escalas mixtas y orden no-monótono por score ya lo estableció
            // el ítem 4.2 (ver RetrievalScoreScale, CrossEncoderStable/Batched) — no es
            // un caso nuevo para los consumidores de RetrievalResult.
            if (primaryResultByPointId.TryGetValue(id, out var existing))
            {
                merged.Add(existing);
            }
            else if (hopPointById.TryGetValue(id, out var hopPoint))
            {
                merged.Add(MapToRetrievalResult(hopPoint, RetrievalScoreScale.SymbolExpansion));
                newFromHop++;
            }
        }

        _logger.LogDebug(
            "Two-hop (6.a): {SymbolCount} símbolos, {HopCount} puntos definidores, {NewCount} nuevos en el pool final de {MergedCount}.",
            symbols.Count, hopPoints.Count, newFromHop, merged.Count);

        return merged;
    }

    /// <summary>
    /// Máximo de símbolos cosechados de <see cref="TwoHopOptions.SeedResults"/>: sin
    /// tope, un chunk con decenas de <c>consumed_symbols</c> (tipos del framework,
    /// <c>ToString</c>, <c>Where</c>...) degrada el filtro <c>Match</c> a "casi toda la
    /// colección" y el salto deja de discriminar.
    /// </summary>
    private const int MaxSeedSymbols = 50;

    /// <summary>
    /// Fusión RRF de segundo nivel, pura y testeable sin Qdrant: combina el rango que un
    /// punto tenía en la fusión primaria (peso implícito 1.0) con el rango que obtuvo en
    /// el segundo salto por símbolo (peso <paramref name="weight"/>), usando el mismo
    /// <paramref name="k"/> para ambos términos. Devuelve los ids ordenados
    /// descendentemente por ese score combinado, recortados a
    /// <paramref name="take"/>.
    /// </summary>
    internal static List<string> MergeSymbolExpansionRanks(
        IReadOnlyDictionary<string, int> primaryRank,
        IReadOnlyDictionary<string, int> hopRank,
        double weight,
        int k,
        int take)
    {
        return primaryRank.Keys.Union(hopRank.Keys)
            .Select(id =>
            {
                double score = 0.0;
                if (primaryRank.TryGetValue(id, out var pr)) score += 1.0 / (k + pr);
                if (hopRank.TryGetValue(id, out var hr)) score += weight / (k + hr);
                return (Id: id, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .Select(x => x.Id)
            .Take(take)
            .ToList();
    }

    private static Filter? BuildFilter(RetrievalOptions options)
    {
        var conditions = new List<Condition>();

        // El point reservado del manifiesto (QdrantVectorStore.ManifestPointId) vive en la
        // misma colección que los chunks; nunca debe aparecer como resultado de búsqueda.
        var excludeManifest = new Condition
        {
            Field = new FieldCondition
            {
                Key = QdrantVectorStore.IsManifestPayloadKey,
                Match = new Match { Boolean = true }
            }
        };

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

        if (!string.IsNullOrWhiteSpace(options.FilterByTenant))
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = QdrantVectorStore.TenantPayloadKey,
                    Match = new Match { Keyword = options.FilterByTenant }
                }
            });
        }

        return conditions.Count == 0
            ? new Filter { MustNot = { excludeManifest } }
            : new Filter { Must = { conditions }, MustNot = { excludeManifest } };
    }

    private static RetrievalResult MapToRetrievalResult(ScoredPoint point, RetrievalScoreScale scale)
    {
        var p = point.Payload;

        return new RetrievalResult(
            ChunkId: point.Id.ToString()!,
            Content: p["content"].StringValue,
            SimilarityScore: point.Score,
            ScoreScale: scale,
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
            ),
            ContentHash: p["content_hash"].StringValue
        );
    }
}
