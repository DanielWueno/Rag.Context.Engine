using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.Reranking;

/// <summary>
/// Runs a multilingual Cross-Encoder ONNX model in-process to re-score the
/// candidate pool produced by the hybrid search. Each (query, chunk) pair is
/// encoded jointly with the XLM-RoBERTa pair convention and the model's single
/// logit is mapped to [0..1] via sigmoid.
///
/// Carga perezosa: la InferenceSession y el tokenizador se crean en el primer
/// ReRankAsync, no en el constructor. Así, ejecutar búsquedas sin --rerank
/// nunca exige tener el modelo cross-encoder descargado.
///
/// Tokenización: mismo esquema SentencePiece/XLM-R que OnnxVectorizationBrain
/// (IDs crudos del .spm remapeados al espacio fairseq: &lt;s&gt;=0, &lt;pad&gt;=1,
/// &lt;/s&gt;=2, &lt;unk&gt;=3, pieza_spm(i) → i+1). La secuencia par sigue la
/// convención XLM-R: &lt;s&gt; query &lt;/s&gt;&lt;/s&gt; chunk &lt;/s&gt;.
/// </summary>
public sealed class OnnxCrossEncoderReRanker : IReRanker, IDisposable
{
    // ── Convención fairseq / XLM-RoBERTa (idéntica a OnnxVectorizationBrain) ──
    private const long XlmrBosId = 0;   // <s>   (CLS)
    private const long XlmrPadId = 1;   // <pad>
    private const long XlmrEosId = 2;   // </s>  (SEP)
    private const long XlmrUnkId = 3;   // <unk>
    private const int FairseqOffset = 1;

    /// <summary>
    /// Tokens especiales de la secuencia par: &lt;s&gt; … &lt;/s&gt;&lt;/s&gt; … &lt;/s&gt;.
    /// </summary>
    private const int SpecialTokenCount = 4;

    private readonly CrossEncoderOptions _options;
    private readonly ILogger<OnnxCrossEncoderReRanker> _logger;
    private readonly Lazy<(InferenceSession Session, Tokenizer Tokenizer)> _model;
    private bool _disposed;

    public OnnxCrossEncoderReRanker(
        IOptions<CrossEncoderOptions> options,
        ILogger<OnnxCrossEncoderReRanker> logger)
    {
        _options = options.Value;
        _logger = logger;
        _model = new Lazy<(InferenceSession, Tokenizer)>(
            LoadModel, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> ReRankAsync(
        string query,
        IReadOnlyList<RetrievalResult> candidates,
        int topK,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
            return candidates;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var scores = await Task.Run(
            () => ScorePairs(query, candidates, cancellationToken),
            cancellationToken);

        var reranked = candidates
            .Zip(scores, (candidate, score) => candidate with { SimilarityScore = score })
            .OrderByDescending(r => r.SimilarityScore)
            .Take(topK)
            .ToList();

        sw.Stop();
        _logger.LogInformation(
            "Cross-encoder re-ranked {PoolSize} candidates → top {TopK} in {ElapsedMs}ms. Best score: {Best:F3}",
            candidates.Count, reranked.Count, sw.ElapsedMilliseconds,
            reranked.Count > 0 ? reranked[0].SimilarityScore : 0f);

        return reranked.AsReadOnly();
    }

    private (InferenceSession, Tokenizer) LoadModel()
    {
        if (!File.Exists(_options.ModelPath))
            throw new FileNotFoundException(
                $"Cross-encoder ONNX model not found at '{_options.ModelPath}'. " +
                "Run 'bash infra/download-model.sh reranker' first.",
                _options.ModelPath);

        if (!File.Exists(_options.VocabPath))
            throw new FileNotFoundException(
                $"Cross-encoder tokenizer not found at '{_options.VocabPath}'. " +
                "Run 'bash infra/download-model.sh reranker' first.",
                _options.VocabPath);

        _logger.LogInformation("Loading cross-encoder ONNX model from {ModelPath}", _options.ModelPath);

        using var sessionOptions = new SessionOptions
        {
            EnableCpuMemArena = true,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        var session = new InferenceSession(_options.ModelPath, sessionOptions);

        using var spmStream = File.OpenRead(_options.VocabPath);
        var tokenizer = SentencePieceTokenizer.Create(
            spmStream,
            addBeginningOfSentence: false,
            addEndOfSentence: false);

        _logger.LogInformation(
            "Cross-encoder loaded. MaxSeqLen: {Seq}, BatchSize: {Batch}",
            _options.MaxSequenceLength, _options.BatchSize);

        return (session, tokenizer);
    }

    private float[] ScorePairs(
        string query,
        IReadOnlyList<RetrievalResult> candidates,
        CancellationToken cancellationToken)
    {
        var (session, tokenizer) = _model.Value;

        // La query se tokeniza una sola vez y se le reserva como máximo la mitad
        // de la ventana; el resto queda para el chunk, que se trunca a lo que quepa.
        var queryIds = RemapToFairseq(tokenizer.EncodeToIds(query));
        int maxQueryTokens = (_options.MaxSequenceLength - SpecialTokenCount) / 2;
        if (queryIds.Count > maxQueryTokens)
            queryIds = queryIds.Take(maxQueryTokens).ToList();

        int maxChunkTokens = _options.MaxSequenceLength - SpecialTokenCount - queryIds.Count;

        var scores = new float[candidates.Count];
        for (int start = 0; start < candidates.Count; start += _options.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int batchSize = Math.Min(_options.BatchSize, candidates.Count - start);
            var batch = new long[batchSize][];
            int seqLen = 1;

            for (int i = 0; i < batchSize; i++)
            {
                batch[i] = EncodePair(tokenizer, queryIds, candidates[start + i].Content, maxChunkTokens);
                seqLen = Math.Max(seqLen, batch[i].Length);
            }

            var batchScores = RunBatch(session, batch, batchSize, seqLen);
            Array.Copy(batchScores, 0, scores, start, batchSize);
        }

        return scores;
    }

    /// <summary>
    /// Builds the XLM-R pair sequence: &lt;s&gt; query &lt;/s&gt;&lt;/s&gt; chunk &lt;/s&gt;.
    /// </summary>
    private static long[] EncodePair(
        Tokenizer tokenizer,
        IReadOnlyList<long> queryIds,
        string chunkText,
        int maxChunkTokens)
    {
        var chunkIds = RemapToFairseq(tokenizer.EncodeToIds(chunkText));
        int chunkCount = Math.Min(chunkIds.Count, maxChunkTokens);

        var result = new long[queryIds.Count + chunkCount + SpecialTokenCount];
        int pos = 0;

        result[pos++] = XlmrBosId;
        for (int i = 0; i < queryIds.Count; i++)
            result[pos++] = queryIds[i];
        result[pos++] = XlmrEosId;
        result[pos++] = XlmrEosId;
        for (int i = 0; i < chunkCount; i++)
            result[pos++] = chunkIds[i];
        result[pos] = XlmrEosId;

        return result;
    }

    private static List<long> RemapToFairseq(IReadOnlyList<int> spmIds)
    {
        var remapped = new List<long>(spmIds.Count);
        foreach (var spmId in spmIds)
            remapped.Add(spmId == 0 ? XlmrUnkId : spmId + FairseqOffset);
        return remapped;
    }

    private float[] RunBatch(InferenceSession session, long[][] batch, int batchSize, int seqLen)
    {
        // Padding dinámico al máximo real del lote, igual que el bi-encoder:
        // el costo de inferencia escala con seqLen.
        var inputIds   = new long[batchSize * seqLen];
        var attnMask   = new long[batchSize * seqLen];
        var tokenTypes = new long[batchSize * seqLen]; // XLM-R: siempre 0, incluso en pares

        for (int i = 0; i < batchSize; i++)
        {
            int offset = i * seqLen;
            var ids = batch[i];
            for (int j = 0; j < ids.Length; j++)
            {
                inputIds[offset + j] = ids[j];
                attnMask[offset + j] = 1L;
            }
            for (int j = ids.Length; j < seqLen; j++)
                inputIds[offset + j] = XlmrPadId;
        }

        int[] shape = [batchSize, seqLen];

        var inputs = new List<NamedOnnxValue>(3);
        foreach (var inputName in session.InputMetadata.Keys)
        {
            inputs.Add(inputName switch
            {
                "input_ids" => NamedOnnxValue.CreateFromTensor(
                    inputName, new DenseTensor<long>(inputIds, shape)),
                "attention_mask" => NamedOnnxValue.CreateFromTensor(
                    inputName, new DenseTensor<long>(attnMask, shape)),
                "token_type_ids" => NamedOnnxValue.CreateFromTensor(
                    inputName, new DenseTensor<long>(tokenTypes, shape)),
                _ => throw new NotSupportedException(
                    $"Unexpected ONNX model input '{inputName}'.")
            });
        }

        using var outputs = session.Run(inputs);

        // Cabezal de clasificación: logits [batch, 1]. Sigmoide → score [0..1].
        var logits = outputs
            .First(o => o.Name == "logits")
            .AsTensor<float>();

        var scores = new float[batchSize];
        for (int i = 0; i < batchSize; i++)
            scores[i] = Sigmoid(logits[i, 0]);

        return scores;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    public void Dispose()
    {
        if (_disposed) return;
        if (_model.IsValueCreated)
            _model.Value.Session.Dispose();
        _disposed = true;
    }
}
