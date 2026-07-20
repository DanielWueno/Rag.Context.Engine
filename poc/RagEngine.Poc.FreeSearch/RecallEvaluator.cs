using RagEngine.Core.Domain;

namespace RagEngine.Poc.FreeSearch;

/// <summary>
/// Fase 4 — el corazón del PoC. Compara, EN MEMORIA, dos estrategias de recuperación
/// sobre la muestra:
///   • baseline  : sólo el vector de código (EnrichedContent), como hoy.
///   • fusión    : código + resumen de negocio, fusionados con RRF ponderado (Reto B).
///
/// La métrica es recall@k: ¿sobrevivió el chunk correcto dentro del top-k? Ésta es la
/// variable que valida (o refuta) la hipótesis del documento — NO la calidad subjetiva
/// del resumen. El sparse queda fuera a propósito: el PoC aísla el aporte del resumen.
/// </summary>
public sealed class RecallEvaluator
{
    private readonly PocSettings _settings;

    public RecallEvaluator(PocSettings settings) => _settings = settings;

    /// <summary>Un chunk con sus dos vectores densos (el de resumen puede faltar → sentinel/fallo).</summary>
    public sealed record IndexedChunk(CodeChunk Chunk, float[] CodeVector, float[]? SummaryVector);

    public sealed record QuestionOutcome(
        string Question,
        int TargetCount,
        Dictionary<int, bool> BaselineHitAtK,
        Dictionary<int, bool> FusionHitAtK);

    public sealed record Report(
        int Questions,
        int Chunks,
        int ChunksWithSummary,
        Dictionary<int, double> BaselineRecall,
        Dictionary<int, double> FusionRecall,
        List<QuestionOutcome> PerQuestion);

    public Report Evaluate(
        IReadOnlyList<IndexedChunk> index,
        IReadOnlyList<EvalItem> evalSet,
        IReadOnlyList<float[]> questionVectors)
    {
        var kValues = _settings.RecallAtK.Distinct().OrderBy(k => k).ToArray();
        var perQuestion = new List<QuestionOutcome>();

        // Rankings de código y de resumen se construyen por pregunta (dependen del query).
        foreach (var (item, qi) in evalSet.Select((it, i) => (it, i)))
        {
            var qVec = questionVectors[qi];

            // Índices de los chunks objetivo para esta pregunta.
            var targetIdx = new HashSet<int>();
            for (int c = 0; c < index.Count; c++)
                if (item.Matches(index[c].Chunk))
                    targetIdx.Add(c);

            // Ranking baseline: por similitud código-pregunta.
            var codeRanked = RankBySimilarity(index, qVec, useSummary: false);

            // Ranking fusión: RRF ponderado entre ranking de código y ranking de resumen.
            var fusionRanked = RankByWeightedRrf(index, qVec);

            var baseHit = new Dictionary<int, bool>();
            var fusHit = new Dictionary<int, bool>();
            foreach (var k in kValues)
            {
                baseHit[k] = HitAtK(codeRanked, targetIdx, k);
                fusHit[k] = HitAtK(fusionRanked, targetIdx, k);
            }

            perQuestion.Add(new QuestionOutcome(item.Question, targetIdx.Count, baseHit, fusHit));
        }

        // Promedios recall@k.
        var baselineRecall = kValues.ToDictionary(
            k => k, k => perQuestion.Average(q => q.BaselineHitAtK[k] ? 1.0 : 0.0));
        var fusionRecall = kValues.ToDictionary(
            k => k, k => perQuestion.Average(q => q.FusionHitAtK[k] ? 1.0 : 0.0));

        return new Report(
            Questions: evalSet.Count,
            Chunks: index.Count,
            ChunksWithSummary: index.Count(c => c.SummaryVector is not null),
            BaselineRecall: baselineRecall,
            FusionRecall: fusionRecall,
            PerQuestion: perQuestion);
    }

    /// <summary>Orden de índices de chunk por similitud descendente contra el query.</summary>
    private static int[] RankBySimilarity(
        IReadOnlyList<IndexedChunk> index, float[] qVec, bool useSummary)
    {
        var scored = new List<(int Idx, float Sim)>(index.Count);
        for (int c = 0; c < index.Count; c++)
        {
            var vec = useSummary ? index[c].SummaryVector : index[c].CodeVector;
            if (vec is null) continue; // sin resumen → no participa en el ranking de resumen
            scored.Add((c, EmbeddingHarness.CosineSimilarity(qVec, vec)));
        }
        return scored.OrderByDescending(x => x.Sim).Select(x => x.Idx).ToArray();
    }

    /// <summary>
    /// RRF ponderado (Reto B). score(doc) = w_code/(k+rank_code) + w_resumen/(k+rank_resumen).
    /// Un chunk sin resumen sólo aporta el término de código.
    /// </summary>
    private int[] RankByWeightedRrf(IReadOnlyList<IndexedChunk> index, float[] qVec)
    {
        var codeRank = ToRankMap(RankBySimilarity(index, qVec, useSummary: false));
        var summaryRank = ToRankMap(RankBySimilarity(index, qVec, useSummary: true));

        double k = _settings.RrfK;
        var scores = new Dictionary<int, double>();
        for (int c = 0; c < index.Count; c++)
        {
            double s = 0.0;
            if (codeRank.TryGetValue(c, out var rc))
                s += _settings.WeightCode / (k + rc);
            if (summaryRank.TryGetValue(c, out var rs))
                s += _settings.WeightResumen / (k + rs);
            scores[c] = s;
        }

        return scores.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToArray();
    }

    /// <summary>Mapa chunkIndex → rank (1-based) a partir de un orden ya calculado.</summary>
    private static Dictionary<int, int> ToRankMap(int[] ranked)
    {
        var map = new Dictionary<int, int>(ranked.Length);
        for (int pos = 0; pos < ranked.Length; pos++)
            map[ranked[pos]] = pos + 1;
        return map;
    }

    private static bool HitAtK(int[] ranked, HashSet<int> targets, int k)
    {
        if (targets.Count == 0) return false;
        var top = Math.Min(k, ranked.Length);
        for (int i = 0; i < top; i++)
            if (targets.Contains(ranked[i]))
                return true;
        return false;
    }
}
