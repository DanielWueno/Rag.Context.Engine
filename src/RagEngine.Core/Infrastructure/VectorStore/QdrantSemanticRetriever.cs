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
/// vectors with exact, tie-complete prefixes and metadata filters.
/// </summary>
public sealed class QdrantSemanticRetriever : ISemanticRetriever
{
    private readonly QdrantClient _client;
    private readonly IVectorizationBrain _brain;
    private readonly ISparseTokenizer _sparseTokenizer;
    private readonly IReRanker _reRanker;
    private readonly IVectorStoreAdmin _vectorStoreAdmin;
    private readonly RetrievalFusionOptions _fusionOptions;
    private readonly TwoHopOptions _twoHopOptions;
    private readonly IRetrievalProfileResolver _profileResolver;
    private readonly ILogger<QdrantSemanticRetriever> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public QdrantSemanticRetriever(
        QdrantClient client,
        IVectorizationBrain brain,
        ISparseTokenizer sparseTokenizer,
        IReRanker reRanker,
        IVectorStoreAdmin vectorStoreAdmin,
        IOptions<RetrievalFusionOptions> fusionOptions,
        IOptions<TwoHopOptions> twoHopOptions,
        IRetrievalProfileResolver profileResolver,
        ILogger<QdrantSemanticRetriever> logger,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _client = client;
        _brain = brain;
        _sparseTokenizer = sparseTokenizer;
        _reRanker = reRanker;
        _vectorStoreAdmin = vectorStoreAdmin;
        _fusionOptions = fusionOptions.Value;
        _twoHopOptions = twoHopOptions.Value;
        _profileResolver = profileResolver;
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

            // Ítem 7.a: perfil por colección. Null (sin CollectionManifest.Profile
            // declarado, o nombre ausente del catálogo) preserva EXACTAMENTE el
            // comportamiento global de siempre — ver los operadores `?? _twoHopOptions...`
            // y `?? _fusionOptions...` de abajo, ninguno cambia sin un perfil resuelto.
            var profile = await _profileResolver.ResolveAsync(options.CollectionName, cancellationToken);
            var twoHopEnabled = profile?.TwoHopEnabled ?? _twoHopOptions.Enabled;

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
            ulong finalLimit = (!options.UseReRanking && twoHopEnabled)
                ? baseFinalLimit + (ulong)_twoHopOptions.MaxExpansionResults
                : baseFinalLimit;
            var payloadSelector = new WithPayloadSelector { Enable = true };

            // The schema selects the formula: native RRF semantics for two vectors,
            // calibrated weighted RRF for collections with dense-resumen.
            var hasSummaryVector = await _vectorStoreAdmin.HasSummaryVectorAsync(options.CollectionName, cancellationToken);

            IReadOnlyList<RankedPoint> rankedPoints;
            if (!hasSummaryVector)
            {
                rankedPoints = await _resiliencePipeline.ExecuteAsync(async ct =>
                {
                    var dense = QueryOrderedAsync(options.CollectionName, queryVector,
                        QdrantVectorStore.DenseVectorName, filter, prefetchLimit, payloadSelector, ct,
                        options.MinimumSimilarityScore > 0f ? options.MinimumSimilarityScore : null);
                    var sparse = QueryOrderedAsync(options.CollectionName, (sparseValues, sparseIndices),
                        QdrantVectorStore.SparseVectorName, filter, prefetchLimit, payloadSelector, ct);
                    await Task.WhenAll(dense, sparse);
                    return FuseNativeRanks(await dense, await sparse, finalLimit);
                }, cancellationToken);
            }
            else
            {
                // Ítem 7.a: los overrides de pesos/RrfK del perfil (si los hay) solo se
                // aplican a esta rama — la fusión nativa de Qdrant (rama !hasSummaryVector,
                // arriba) no expone pesos por rama y no los necesita.
                var effectiveFusion = ApplyFusionOverrides(_fusionOptions, profile?.Fusion);
                rankedPoints = await _resiliencePipeline.ExecuteAsync(async ct =>
                    await SearchWeightedFusionAsync(
                        options.CollectionName, queryVector, sparseValues, sparseIndices,
                        filter, prefetchLimit, finalLimit, payloadSelector, effectiveFusion, ct),
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

            var activeModules = GetActiveModules(options).ToArray();
            var authorizedPrimary = rankedPoints
                .Select(ranked => (ranked.Point, Result: MapToRetrievalResult(ranked.Point, fusionScale) with
                {
                    RankingScore = ranked.Score
                }))
                .Where(candidate => activeModules.All(module => MatchesModuleBoundary(candidate.Result.Metadata, module)))
                .ToArray();
            // Keep points/results aligned: rejected payloads must not seed symbol expansion.
            IReadOnlyList<ScoredPoint> searchResults = authorizedPrimary.Select(candidate => candidate.Point).ToArray();
            IReadOnlyList<RetrievalResult> results = authorizedPrimary.Select(candidate => candidate.Result).ToArray();

            // 5.b Ítem 6.a: expansión por símbolo. Apagado por defecto
            // (TwoHopOptions.Enabled=false), salvo que el perfil de la colección
            // (ítem 7.a) lo encienda/apague explícitamente vía twoHopEnabled — con el
            // flag efectivo en false este bloque no ejecuta ningún QueryAsync
            // adicional y el comportamiento es idéntico al de antes de 6.a (ver
            // rollback de la ficha).
            if (twoHopEnabled && searchResults.Count > 0)
            {
                results = await ExpandBySymbolAsync(
                    options, queryVector, filter, searchResults, results, cancellationToken);
                results = results
                    .Where(result => activeModules.All(module => MatchesModuleBoundary(result.Metadata, module)))
                    .ToArray();
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

    private sealed record RankedPoint(ScoredPoint Point, double Score);

    private Task<IReadOnlyList<ScoredPoint>> QueryOrderedAsync(
        string collection, Query query, string vector, Filter? filter, ulong limit,
        WithPayloadSelector payload, CancellationToken ct, float? threshold = null) =>
        DeterministicVectorQuery.SearchAsync(
            _client, collection, query, vector, filter, limit, payload, _logger, ct, threshold);

    private static IReadOnlyList<RankedPoint> FuseNativeRanks(
        IReadOnlyList<ScoredPoint> dense, IReadOnlyList<ScoredPoint> sparse, ulong limit)
    {
        // Qdrant RRF: f32 accumulation in branch order, k=2 with zero-based ranks.
        // Server-side prefetch cannot express a secondary UUID order before truncation.
        var points = new Dictionary<string, (ScoredPoint Point, float Score)>(StringComparer.Ordinal);
        foreach (var branch in new[] { dense, sparse })
        {
            for (var rank = 0; rank < branch.Count; rank++)
            {
                var point = branch[rank];
                var id = DeterministicVectorQuery.ChunkId(point);
                points.TryGetValue(id, out var previous);
                points[id] = (previous.Point ?? point, previous.Score + 1f / (rank + 2));
            }
        }
        return RankingOrder.Descending(points.Values, p => p.Score,
                p => DeterministicVectorQuery.ChunkId(p.Point))
            .Take((int)limit).Select(p =>
            {
                p.Point.Score = p.Score;
                return new RankedPoint(p.Point, p.Score);
            }).ToArray();
    }

    /// <summary>
    /// Weighted RRF over three independently ranked branches; weights and k remain
    /// those calibrated in RetrievalFusionOptions, with optional collection overrides.
    /// </summary>
    private async Task<IReadOnlyList<RankedPoint>> SearchWeightedFusionAsync(
        string collectionName,
        float[] queryVector,
        float[] sparseValues,
        uint[] sparseIndices,
        Filter? filter,
        ulong prefetchLimit,
        ulong finalLimit,
        WithPayloadSelector payloadSelector,
        RetrievalFusionOptions fusionOptions,
        CancellationToken ct)
    {
        var denseTask = QueryOrderedAsync(collectionName, queryVector,
            QdrantVectorStore.DenseVectorName, filter, prefetchLimit, payloadSelector, ct);
        var sparseTask = QueryOrderedAsync(collectionName, (sparseValues, sparseIndices),
            QdrantVectorStore.SparseVectorName, filter, prefetchLimit, payloadSelector, ct);
        var resumenTask = QueryOrderedAsync(collectionName, queryVector,
            QdrantVectorStore.SummaryVectorName, filter, prefetchLimit, payloadSelector, ct);

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
            pointsById.TryAdd(DeterministicVectorQuery.ChunkId(point), point);

        // Ítem 9.7: el mismo núcleo de fusión (RankFusion.Fuse) que calibra el sweep de
        // pesos en poc/RecallEvaluator.RankByRrf. No reintroducir aquí una copia de la
        // fórmula: cualquier cambio de k, pesos o desempate debe pasar por ese único
        // punto para que calibración y producción sigan midiendo el mismo algoritmo.
        var k = fusionOptions.RrfK;
        var branches = new[]
        {
            new RankFusion.Branch<string>(denseRank, fusionOptions.WeightCodigo),
            new RankFusion.Branch<string>(sparseRank, fusionOptions.WeightSparse),
            new RankFusion.Branch<string>(resumenRank, fusionOptions.WeightResumen),
        };
        var scored = RankFusion.Fuse(pointsById.Keys, k, branches, StringComparer.Ordinal)
        .Take((int)finalLimit)
        .ToList();

        var result = new List<RankedPoint>(scored.Count);
        foreach (var (id, score) in scored)
        {
            var point = pointsById[id];
            point.Score = (float)score; // score RRF ponderado, no similitud coseno cruda
            result.Add(new RankedPoint(point, score));
        }
        return result;
    }

    /// <summary>
    /// Ítem 7.a: aplica los overrides opcionales de <paramref name="overrides"/> sobre
    /// <paramref name="baseline"/> (los pesos globales de "RetrievalFusion" en
    /// appsettings.json). Cada campo null de <paramref name="overrides"/> conserva el
    /// valor de <paramref name="baseline"/> — un perfil sin overrides de fusión
    /// (<paramref name="overrides"/> null) devuelve <paramref name="baseline"/> tal
    /// cual, sin crear una copia, preservando el baseline byte a byte.
    /// </summary>
    internal static RetrievalFusionOptions ApplyFusionOverrides(
        RetrievalFusionOptions baseline, RetrievalProfileFusionWeights? overrides)
    {
        if (overrides is null) return baseline;

        return baseline with
        {
            WeightCodigo = overrides.WeightCodigo ?? baseline.WeightCodigo,
            WeightSparse = overrides.WeightSparse ?? baseline.WeightSparse,
            WeightResumen = overrides.WeightResumen ?? baseline.WeightResumen,
            RrfK = overrides.RrfK ?? baseline.RrfK
        };
    }

    private static Dictionary<string, int> ToRankMap(IReadOnlyList<ScoredPoint> ranked)
    {
        var map = new Dictionary<string, int>(ranked.Count);
        for (int pos = 0; pos < ranked.Count; pos++)
            map[DeterministicVectorQuery.ChunkId(ranked[pos])] = pos + 1;
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
                await QueryOrderedAsync(options.CollectionName, queryVector,
                    QdrantVectorStore.DenseVectorName, hopFilter,
                    (ulong)_twoHopOptions.MaxExpansionResults,
                    new WithPayloadSelector { Enable = true }, ct),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Symbol expansion query failed for collection {Col}; incomplete retrieval is not a successful empty expansion.",
                options.CollectionName);
            throw;
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

        var mergedScores = ScoreSymbolExpansionRanks(
            primaryRank, hopRank, _twoHopOptions.Weight, k2).Take(primaryResults.Count).ToArray();

        var primaryResultByPointId = new Dictionary<string, RetrievalResult>(primaryPoints.Count);
        for (int i = 0; i < primaryPoints.Count; i++)
            primaryResultByPointId.TryAdd(DeterministicVectorQuery.ChunkId(primaryPoints[i]), primaryResults[i]);

        var hopPointById = new Dictionary<string, ScoredPoint>(hopPoints.Count);
        foreach (var p in hopPoints)
            hopPointById.TryAdd(DeterministicVectorQuery.ChunkId(p), p);

        var merged = new List<RetrievalResult>(mergedScores.Length);
        var newFromHop = 0;
        foreach (var (id, score) in mergedScores)
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
                merged.Add(existing with { RankingScore = score, RankingScoreScale = RetrievalScoreScale.SymbolExpansion });
            }
            else if (hopPointById.TryGetValue(id, out var hopPoint))
            {
                merged.Add(MapToRetrievalResult(hopPoint, RetrievalScoreScale.SymbolExpansion) with
                {
                    RankingScore = score,
                    RankingScoreScale = RetrievalScoreScale.SymbolExpansion
                });
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
        => ScoreSymbolExpansionRanks(primaryRank, hopRank, weight, k)
            .Take(take).Select(x => x.Id).ToList();

    private static IOrderedEnumerable<(string Id, double Score)> ScoreSymbolExpansionRanks(
        IReadOnlyDictionary<string, int> primaryRank, IReadOnlyDictionary<string, int> hopRank,
        double weight, int k)
    {
        var scores = primaryRank.Keys.Union(hopRank.Keys)
            .Select(id =>
            {
                double score = 0.0;
                if (primaryRank.TryGetValue(id, out var pr)) score += 1.0 / (k + pr);
                if (hopRank.TryGetValue(id, out var hr)) score += weight / (k + hr);
                return (Id: id, Score: score);
            });
        return RankingOrder.Descending(scores, x => x.Score, x => x.Id);
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
            conditions.Add(BuildTenantFilter(options.FilterByTenant));
        }

        if (options.Context.Mode == RetrievalContextMode.Authorized &&
            !string.IsNullOrWhiteSpace(options.Context.Tenant))
        {
            conditions.Add(BuildTenantFilter(options.Context.Tenant));
        }

        foreach (var module in GetActiveModules(options))
            conditions.Add(BuildModuleFilter(module));

        return conditions.Count == 0
            ? new Filter { MustNot = { excludeManifest } }
            : new Filter { Must = { conditions }, MustNot = { excludeManifest } };
    }

    private static Condition BuildTenantFilter(string tenant) => new()
    {
        Field = new FieldCondition
        {
            Key = QdrantVectorStore.TenantPayloadKey,
            Match = new Match { Keyword = tenant }
        }
    };

    private static IEnumerable<string> GetActiveModules(RetrievalOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.FilterByModule))
            yield return options.FilterByModule;

        if (options.Context.Mode == RetrievalContextMode.Authorized &&
            !string.IsNullOrWhiteSpace(options.Context.Module))
            yield return options.Context.Module;
    }

    internal static bool MatchesModuleBoundary(CodeChunkMetadata metadata, string module)
    {
        if (string.IsNullOrWhiteSpace(module))
            return false;

        return MatchesBoundary(metadata.Namespace, module, '.') ||
               MatchesBoundary(metadata.RelativeFilePath, module, '/');
    }

    private static bool MatchesBoundary(string? value, string module, char separator) =>
        value is not null &&
        value.StartsWith(module, StringComparison.Ordinal) &&
        (value.Length == module.Length || value[module.Length] == separator);

    // Match.Text is only a candidate prefilter, not an authorization boundary.
    // MatchesModuleBoundary enforces exact roots/descendants without re-ingestion.
    internal static Condition BuildModuleFilter(string module) => new()
    {
        Filter = new Filter
        {
            Should =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "namespace",
                        Match = new Match { Text = module }
                    }
                },
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "relative_path",
                        Match = new Match { Text = module }
                    }
                }
            }
        }
    };

    private static RetrievalResult MapToRetrievalResult(ScoredPoint point, RetrievalScoreScale scale)
    {
        var p = point.Payload;

        return new RetrievalResult(
            ChunkId: DeterministicVectorQuery.ChunkId(point),
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
