using RagEngine.Core.Domain;

namespace RagEngine.Poc.FreeSearch;

/// <summary>
/// Fase 4 — el corazón del PoC. Compara, EN MEMORIA, tres estrategias de recuperación
/// sobre la muestra, para los mismos casos de evaluación:
///   • código        : sólo el vector denso de código (EnrichedContent), como aislante.
///   • cód+sparse     : denso-código + disperso (BM25) — el BASELINE REAL de producción.
///   • cód+spa+res    : + el vector de resumen de negocio (la propuesta completa, Reto B).
///
/// La métrica es recall@k: ¿sobrevivió el chunk correcto dentro del top-k? La comparación
/// clave es cód+sparse vs cód+spa+res: mide el aporte del resumen SOBRE el híbrido real,
/// no sobre un baseline denso artificialmente débil.
/// </summary>
public sealed class RecallEvaluator
{
    public const string Codigo = "código";
    public const string CodSparse = "cód+sparse";
    public const string CodSpaRes = "cód+spa+res";
    private static readonly string[] AllConfigs = [Codigo, CodSparse, CodSpaRes];

    private readonly PocSettings _settings;
    public RecallEvaluator(PocSettings settings) => _settings = settings;

    /// <summary>Un chunk con sus tres representaciones (el vector de resumen puede faltar).</summary>
    public sealed record IndexedChunk(
        CodeChunk Chunk, float[] CodeVector, float[]? SummaryVector, Dictionary<uint, float> SparseVector);

    public sealed record Report(
        int Questions, int Chunks, int ChunksWithSummary, int MaxK,
        IReadOnlyList<string> Configs,
        Dictionary<string, Dictionary<int, double>> RecallByConfig,
        // Preguntas que cada config resuelve dentro del top-MaxK (para el diagnóstico de aporte).
        Dictionary<string, HashSet<string>> SolvedAtMaxK);

    public Report Evaluate(
        IReadOnlyList<IndexedChunk> index,
        IReadOnlyList<EvalItem> evalSet,
        IReadOnlyList<float[]> questionVectors,
        IReadOnlyList<Dictionary<uint, float>> questionSparse)
    {
        var kValues = _settings.RecallAtK.Distinct().OrderBy(k => k).ToArray();
        var maxK = kValues.Length == 0 ? 10 : kValues.Max();

        var hits = AllConfigs.ToDictionary(c => c, _ => kValues.ToDictionary(k => k, _ => 0));
        var solved = AllConfigs.ToDictionary(c => c, _ => new HashSet<string>());

        foreach (var (item, qi) in evalSet.Select((it, i) => (it, i)))
        {
            var qVec = questionVectors[qi];
            var qSparse = questionSparse[qi];

            var targets = new HashSet<int>();
            for (int c = 0; c < index.Count; c++)
                if (item.Matches(index[c].Chunk))
                    targets.Add(c);

            var codeRank = ToRankMap(RankDense(index, qVec, useSummary: false));
            var summaryRank = ToRankMap(RankDense(index, qVec, useSummary: true));
            var sparseRank = ToRankMap(RankSparse(index, qSparse));

            var ranked = new Dictionary<string, int[]>
            {
                [Codigo] = RankByRrf(index.Count, [(codeRank, _settings.WeightCode)]),
                [CodSparse] = RankByRrf(index.Count,
                    [(codeRank, _settings.WeightCode), (sparseRank, _settings.WeightSparse)]),
                [CodSpaRes] = RankByRrf(index.Count,
                    [(codeRank, _settings.WeightCode), (sparseRank, _settings.WeightSparse), (summaryRank, _settings.WeightResumen)]),
            };

            foreach (var cfg in AllConfigs)
            {
                foreach (var k in kValues)
                    if (HitAtK(ranked[cfg], targets, k)) hits[cfg][k]++;
                if (HitAtK(ranked[cfg], targets, maxK)) solved[cfg].Add(item.Question);
            }
        }

        int n = evalSet.Count;
        var recall = AllConfigs.ToDictionary(
            c => c, c => kValues.ToDictionary(k => k, k => n == 0 ? 0.0 : (double)hits[c][k] / n));

        return new Report(n, index.Count, index.Count(c => c.SummaryVector is not null),
            maxK, AllConfigs, recall, solved);
    }

    private static int[] RankDense(IReadOnlyList<IndexedChunk> index, float[] qVec, bool useSummary)
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

    private static int[] RankSparse(IReadOnlyList<IndexedChunk> index, Dictionary<uint, float> qSparse)
    {
        var scored = new List<(int Idx, float Sim)>(index.Count);
        for (int c = 0; c < index.Count; c++)
            scored.Add((c, SparseHarness.Dot(qSparse, index[c].SparseVector)));
        return scored.OrderByDescending(x => x.Sim).Select(x => x.Idx).ToArray();
    }

    /// <summary>RRF ponderado sobre N ramas: score(doc) = Σ_r w_r / (k + rank_r(doc)).</summary>
    private int[] RankByRrf(int count, (Dictionary<int, int> Rank, double Weight)[] branches)
    {
        double k = _settings.RrfK;
        var scores = new Dictionary<int, double>(count);
        for (int c = 0; c < count; c++)
        {
            double s = 0.0;
            foreach (var (rank, w) in branches)
                if (rank.TryGetValue(c, out var r))
                    s += w / (k + r);
            scores[c] = s;
        }
        return scores.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToArray();
    }

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
