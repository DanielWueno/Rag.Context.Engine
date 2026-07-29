using RagEngine.Core.Abstractions;
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
    public const string CodSpaResRerank = "cód+spa+res+rerank";

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
        Dictionary<string, HashSet<string>> SolvedAtMaxK,
        // Detalle por pregunta bajo cód+sparse+resumen: qué se recuperó y en qué puesto cae el objetivo.
        IReadOnlyList<QuestionDetail> Details);

    public sealed record RetrievedChunk(
        int Rank, bool IsTarget, string Path, int StartLine, int EndLine, string Type, bool HasSummary);

    public sealed record QuestionDetail(
        string Question, string[] TargetPaths, int? BestTargetRank, IReadOnlyList<RetrievedChunk> Top);

    /// <summary>
    /// Rankings denso-código/disperso/denso-resumen de una pregunta, YA calculados
    /// (embeddings + coseno/dot-product) y SIN pesos aplicados. Separar esto de la
    /// fusión RRF es lo que permite barrer decenas de combinaciones de pesos en
    /// milisegundos — el costo real (ONNX, Ollama) no se repite por combinación.
    /// </summary>
    public sealed record QuestionRankData(
        string Question,
        HashSet<int> Targets,
        Dictionary<int, int> CodeRank,
        Dictionary<int, int> SparseRank,
        Dictionary<int, int> SummaryRank);

    public sealed record WeightSweepResult(
        double WeightCode, double WeightSparse, double WeightResumen,
        IReadOnlyDictionary<int, double> RecallByK, int Solved, int Questions);

    /// <summary>
    /// Precalcula, una sola vez, los tres rankings por pregunta (código/sparse/resumen).
    /// Llamar antes de barrer pesos con <see cref="EvaluateWeights"/>.
    /// </summary>
    public List<QuestionRankData> PrepareRankData(
        IReadOnlyList<IndexedChunk> index,
        IReadOnlyList<EvalItem> evalSet,
        IReadOnlyList<float[]> questionVectors,
        IReadOnlyList<Dictionary<uint, float>> questionSparse)
    {
        var result = new List<QuestionRankData>(evalSet.Count);
        foreach (var (item, qi) in evalSet.Select((it, i) => (it, i)))
        {
            var qVec = questionVectors[qi];
            var qSparse = questionSparse[qi];

            var targets = new HashSet<int>();
            for (int c = 0; c < index.Count; c++)
                if (item.Matches(index[c].Chunk))
                    targets.Add(c);

            result.Add(new QuestionRankData(
                item.Question,
                targets,
                ToRankMap(RankDense(index, qVec, useSummary: false)),
                ToRankMap(RankSparse(index, qSparse)),
                ToRankMap(RankDense(index, qVec, useSummary: true))));
        }
        return result;
    }

    /// <summary>
    /// Recall@k de la fusión de 3 bandas (cód+spa+res) para UNA combinación de pesos,
    /// sobre rankings ya precalculados. Barato: sólo Σ w/(k+rank) y un sort por
    /// pregunta — pensado para llamarse en un bucle sobre una grilla de pesos.
    /// </summary>
    public WeightSweepResult EvaluateWeights(
        IReadOnlyList<QuestionRankData> rankData,
        int chunkCount,
        double weightCode, double weightSparse, double weightResumen,
        int rrfK, IReadOnlyList<int> kValues)
    {
        var hits = kValues.ToDictionary(k => k, _ => 0);
        int maxK = kValues.Count == 0 ? 10 : kValues.Max();
        int solved = 0;

        foreach (var q in rankData)
        {
            var fused = RankByRrf(chunkCount, rrfK,
                [(q.CodeRank, weightCode), (q.SparseRank, weightSparse), (q.SummaryRank, weightResumen)]);

            foreach (var k in kValues)
                if (HitAtK(fused, q.Targets, k)) hits[k]++;
            if (HitAtK(fused, q.Targets, maxK)) solved++;
        }

        int n = rankData.Count;
        return new WeightSweepResult(weightCode, weightSparse, weightResumen,
            kValues.ToDictionary(k => k, k => n == 0 ? 0.0 : (double)hits[k] / n), solved, n);
    }

    /// <param name="reranker">
    /// Si no es null, añade la config <see cref="CodSpaResRerank"/>: re-puntúa el pool
    /// fusionado de cód+spa+res (widened a <paramref name="rerankTopK"/>×3, igual que
    /// producción) con el Cross-Encoder ONNX real y recorta a <paramref name="rerankTopK"/>.
    /// Responde la pregunta abierta del documento: si el recall ya es bueno, ¿el rerank
    /// reduce la necesidad de calibrar los pesos de RRF con precisión?
    /// </param>
    public async Task<Report> EvaluateAsync(
        IReadOnlyList<IndexedChunk> index,
        IReadOnlyList<EvalItem> evalSet,
        IReadOnlyList<float[]> questionVectors,
        IReadOnlyList<Dictionary<uint, float>> questionSparse,
        IReRanker? reranker,
        int rerankTopK,
        CancellationToken ct = default)
    {
        var kValues = _settings.RecallAtK.Distinct().OrderBy(k => k).ToArray();
        var maxK = kValues.Length == 0 ? 10 : kValues.Max();

        var configs = new List<string> { Codigo, CodSparse, CodSpaRes };
        if (reranker is not null) configs.Add(CodSpaResRerank);

        var hits = configs.ToDictionary(c => c, _ => kValues.ToDictionary(k => k, _ => 0));
        var solved = configs.ToDictionary(c => c, _ => new HashSet<string>());
        var details = new List<QuestionDetail>();
        const int topN = 6;

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

            if (reranker is not null)
            {
                var pool = BuildPool(ranked[CodSpaRes], index, rerankTopK * 3);
                var rerankedResults = await reranker.ReRankAsync(item.Question, pool, rerankTopK, ct);
                ranked[CodSpaResRerank] = rerankedResults
                    .Select(res => int.Parse(res.ChunkId, System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray();
            }

            foreach (var cfg in configs)
            {
                foreach (var k in kValues)
                    if (HitAtK(ranked[cfg], targets, k)) hits[cfg][k]++;
                if (HitAtK(ranked[cfg], targets, maxK)) solved[cfg].Add(item.Question);
            }

            // Detalle bajo la config completa: top-N recuperado + puesto del mejor objetivo.
            var full = ranked[CodSpaRes];
            int? bestRank = null;
            for (int pos = 0; pos < full.Length; pos++)
                if (targets.Contains(full[pos])) { bestRank = pos + 1; break; }

            var top = new List<RetrievedChunk>();
            for (int pos = 0; pos < Math.Min(topN, full.Length); pos++)
            {
                var ic = index[full[pos]];
                top.Add(new RetrievedChunk(
                    pos + 1, targets.Contains(full[pos]),
                    ic.Chunk.Metadata.RelativeFilePath, ic.Chunk.Metadata.StartLine,
                    ic.Chunk.Metadata.EndLine, ic.Chunk.Type.ToString(), ic.SummaryVector is not null));
            }
            details.Add(new QuestionDetail(item.Question, [.. targets.Select(t => index[t].Chunk.Metadata.RelativeFilePath).Distinct()], bestRank, top));
        }

        int n = evalSet.Count;
        var recall = configs.ToDictionary(
            c => c, c => kValues.ToDictionary(k => k, k => n == 0 ? 0.0 : (double)hits[c][k] / n));

        return new Report(n, index.Count, index.Count(c => c.SummaryVector is not null),
            maxK, configs, recall, solved, details);
    }

    /// <summary>
    /// Toma el pool ancho (top <paramref name="poolSize"/> del fusor RRF) y lo empaqueta
    /// como <see cref="RetrievalResult"/> para el Cross-Encoder — mismo contrato que
    /// <c>QdrantSemanticRetriever.SearchAsync</c> usa antes de llamar a <c>IReRanker</c>.
    /// ChunkId es el índice del chunk en <paramref name="index"/> (no el Guid real): el
    /// PoC no persiste en Qdrant, así que basta con un id estable dentro de la corrida.
    /// </summary>
    private static List<RetrievalResult> BuildPool(int[] fusedOrder, IReadOnlyList<IndexedChunk> index, int poolSize)
    {
        var take = Math.Min(poolSize, fusedOrder.Length);
        var pool = new List<RetrievalResult>(take);
        for (int i = 0; i < take; i++)
        {
            var idx = fusedOrder[i];
            var chunk = index[idx].Chunk;
            pool.Add(new RetrievalResult(
                idx.ToString(System.Globalization.CultureInfo.InvariantCulture),
                chunk.Content,
                0f,
                chunk.Metadata));
        }
        return pool;
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
        => RankByRrf(count, _settings.RrfK, branches);

    private static int[] RankByRrf(int count, double k, (Dictionary<int, int> Rank, double Weight)[] branches)
    {
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
