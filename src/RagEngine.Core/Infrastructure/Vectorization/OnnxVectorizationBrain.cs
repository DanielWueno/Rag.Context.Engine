using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using RagEngine.Core.Abstractions;

namespace RagEngine.Core.Infrastructure.Vectorization;

/// <summary>
/// Runs the all-MiniLM-L6-v2 ONNX model in-process to produce dense embeddings.
/// The InferenceSession is created once (Singleton) due to high initialization cost.
/// Performs Mean Pooling and L2 normalization as required by sentence-transformers.
/// Uses Task.Run to wrap CPU-intensive ONNX inference and avoid blocking async threads.
/// </summary>
public sealed class OnnxVectorizationBrain : IVectorizationBrain, IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly OnnxBrainOptions _options;
    private readonly ILogger<OnnxVectorizationBrain> _logger;
    private bool _disposed;

    public int EmbeddingDimensions => _options.EmbeddingDimensions;

    public OnnxVectorizationBrain(
        IOptions<OnnxBrainOptions> options,
        ILogger<OnnxVectorizationBrain> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (!File.Exists(_options.ModelPath))
            throw new FileNotFoundException(
                $"ONNX model not found at '{_options.ModelPath}'. " +
                "Run 'bash infra/download-model.sh' first.",
                _options.ModelPath);

        if (!File.Exists(_options.VocabPath))
            throw new FileNotFoundException(
                $"Tokenizer vocab not found at '{_options.VocabPath}'. " +
                "Run 'bash infra/download-model.sh' first.",
                _options.VocabPath);

        _logger.LogInformation("Loading ONNX model from {ModelPath}", _options.ModelPath);

        var sessionOptions = new SessionOptions
        {
            EnableCpuMemArena = true,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = Environment.ProcessorCount,
            IntraOpNumThreads = Environment.ProcessorCount
        };

        _session = new InferenceSession(_options.ModelPath, sessionOptions);

        _tokenizer = BertTokenizer.Create(_options.VocabPath, new BertOptions
        {
            LowerCaseBeforeTokenization = true
        });

        _logger.LogInformation(
            "ONNX model loaded. Embedding dimension: {Dim}, MaxSeqLen: {Seq}",
            _options.EmbeddingDimensions, _options.MaxSequenceLength);
    }

    /// <inheritdoc />
    public Task<float[]> GenerateEmbeddingAsync(
        string text, CancellationToken cancellationToken = default)
        => Task.Run(() => EmbedBatch([text])[0], cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
        => Task.Run(() => (IReadOnlyList<float[]>)EmbedBatch(texts.ToList()), cancellationToken);

    private IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts)
    {
        int batchSize = texts.Count;
        int seqLen = _options.MaxSequenceLength;

        var inputIds   = new long[batchSize * seqLen];
        var attnMask   = new long[batchSize * seqLen];
        var tokenTypes = new long[batchSize * seqLen]; // all zeros (single-sentence)

        for (int i = 0; i < batchSize; i++)
        {
            // EncodeToIds(string text, int maxTokenCount, bool addSpecialTokens,
            //             out string? normalizedText, out int charsConsumed)
            var ids = _tokenizer.EncodeToIds(
                texts[i],
                maxTokenCount: seqLen,
                addSpecialTokens: true,
                normalizedText: out _,
                charsConsumed: out _);

            int offset = i * seqLen;
            int tokenCount = Math.Min(ids.Count, seqLen);
            for (int j = 0; j < tokenCount; j++)
            {
                inputIds[offset + j] = ids[j];
                attnMask[offset + j] = 1L;
            }
            // Padding slots remain 0 (pre-initialized)
        }

        // DenseTensor<T> constructor: (Memory<T> data, ReadOnlySpan<int> dimensions)
        int[] shape = [batchSize, seqLen];

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids",
                new DenseTensor<long>(inputIds, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask",
                new DenseTensor<long>(attnMask, shape)),
            NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<long>(tokenTypes, shape)),
        };

        using var outputs = _session.Run(inputs);

        // last_hidden_state: [batch, seq, dim]
        var hiddenState = outputs
            .First(o => o.Name == "last_hidden_state")
            .AsTensor<float>();

        var result = new float[batchSize][];
        for (int i = 0; i < batchSize; i++)
            result[i] = MeanPoolAndNormalize(hiddenState, attnMask, i, seqLen);

        return result;
    }

    /// <summary>
    /// Applies Mean Pooling (average token embeddings weighted by attention mask)
    /// followed by L2 normalization, as required by sentence-transformers models.
    /// </summary>
    private float[] MeanPoolAndNormalize(
        Tensor<float> hiddenState,
        long[] attnMask,
        int batchIdx,
        int seqLen)
    {
        int dim = _options.EmbeddingDimensions;
        float[] embedding = new float[dim];
        float maskSum = 0f;

        for (int t = 0; t < seqLen; t++)
        {
            float mask = attnMask[batchIdx * seqLen + t];
            maskSum += mask;
            if (mask <= 0f) continue;

            for (int d = 0; d < dim; d++)
                embedding[d] += hiddenState[batchIdx, t, d] * mask;
        }

        if (maskSum > 0f)
            for (int d = 0; d < dim; d++)
                embedding[d] /= maskSum;

        // L2 normalize
        float norm = 0f;
        for (int d = 0; d < dim; d++)
            norm += embedding[d] * embedding[d];
        norm = MathF.Sqrt(norm);

        if (norm > 1e-8f)
            for (int d = 0; d < dim; d++)
                embedding[d] /= norm;

        return embedding;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _session.Dispose();
        _disposed = true;
    }
}
