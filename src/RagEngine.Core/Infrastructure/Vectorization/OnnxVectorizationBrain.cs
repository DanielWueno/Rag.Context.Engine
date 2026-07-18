using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using RagEngine.Core.Abstractions;

namespace RagEngine.Core.Infrastructure.Vectorization;

/// <summary>
/// Runs a sentence-transformers ONNX model in-process to produce dense embeddings.
/// The InferenceSession is created once (Singleton) due to high initialization cost.
/// Performs Mean Pooling and L2 normalization as required by sentence-transformers.
/// Uses Task.Run to wrap CPU-intensive ONNX inference and avoid blocking async threads.
///
/// Soporta dos familias de tokenización (ver <see cref="OnnxTokenizerKind"/>):
///   • WordPiece (BERT uncased)   — p. ej. all-MiniLM-L6-v2 (monolingüe inglés).
///   • SentencePiece (XLM-R)      — p. ej. paraphrase-multilingual-MiniLM-L12-v2.
///
/// Para SentencePiece, los IDs crudos del modelo .spm se remapean al espacio de
/// vocabulario del modelo ONNX con la convención fairseq de XLM-RoBERTa:
///   &lt;s&gt;=0, &lt;pad&gt;=1, &lt;/s&gt;=2, &lt;unk&gt;=3, pieza_spm(i) → i+1.
/// (Verificable en tokenizer.json del modelo: las piezas comienzan en el índice 4.)
/// </summary>
public sealed class OnnxVectorizationBrain : IVectorizationBrain, IDisposable
{
    // ── Convención fairseq / XLM-RoBERTa ────────────────────────────────────
    private const long XlmrBosId = 0;   // <s>   (CLS)
    private const long XlmrPadId = 1;   // <pad>
    private const long XlmrEosId = 2;   // </s>  (SEP)
    private const long XlmrUnkId = 3;   // <unk>
    private const int FairseqOffset = 1;

    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;
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
                $"Tokenizer file not found at '{_options.VocabPath}'. " +
                "Run 'bash infra/download-model.sh' first.",
                _options.VocabPath);

        _logger.LogInformation("Loading ONNX model from {ModelPath}", _options.ModelPath);

        using var sessionOptions = new SessionOptions
        {
            EnableCpuMemArena = true,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        _session = new InferenceSession(_options.ModelPath, sessionOptions);

        _tokenizer = CreateTokenizer();

        _logger.LogInformation(
            "ONNX model loaded. Tokenizer: {Tokenizer}, Embedding dimension: {Dim}, MaxSeqLen: {Seq}",
            _options.TokenizerType, _options.EmbeddingDimensions, _options.MaxSequenceLength);
    }

    private Tokenizer CreateTokenizer()
    {
        switch (_options.TokenizerType)
        {
            case OnnxTokenizerKind.WordPiece:
                return BertTokenizer.Create(_options.VocabPath, new BertOptions
                {
                    LowerCaseBeforeTokenization = true
                });

            case OnnxTokenizerKind.SentencePiece:
                // BOS/EOS se insertan manualmente ya remapeados al espacio fairseq,
                // por lo que aquí se piden IDs crudos sin tokens especiales.
                using (var spmStream = File.OpenRead(_options.VocabPath))
                {
                    return SentencePieceTokenizer.Create(
                        spmStream,
                        addBeginningOfSentence: false,
                        addEndOfSentence: false);
                }

            default:
                throw new NotSupportedException(
                    $"Tokenizer type '{_options.TokenizerType}' is not supported.");
        }
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

        // ── Pass 1: tokenizar todo el lote (acotado por MaxSequenceLength) ──
        var encoded = new long[batchSize][];
        int seqLen = 1;
        for (int i = 0; i < batchSize; i++)
        {
            encoded[i] = _options.TokenizerType == OnnxTokenizerKind.SentencePiece
                ? EncodeSentencePiece(texts[i], _options.MaxSequenceLength)
                : EncodeWordPiece(texts[i], _options.MaxSequenceLength);

            seqLen = Math.Max(seqLen, encoded[i].Length);
        }

        // ── Pass 2: padding DINÁMICO al máximo real del lote ────────────────
        // El costo de la inferencia escala con seqLen; rellenar cada chunk hasta
        // MaxSequenceLength fijo desperdicia cómputo en secuencias mayormente
        // cortas (un chunk de código típico ronda 60-150 tokens).
        long padId = _options.TokenizerType == OnnxTokenizerKind.SentencePiece
            ? XlmrPadId
            : 0L; // [PAD] = 0 en vocabularios BERT

        var inputIds   = new long[batchSize * seqLen];
        var attnMask   = new long[batchSize * seqLen];
        var tokenTypes = new long[batchSize * seqLen]; // all zeros (single-sentence)

        for (int i = 0; i < batchSize; i++)
        {
            int offset = i * seqLen;
            var ids = encoded[i];
            for (int j = 0; j < ids.Length; j++)
            {
                inputIds[offset + j] = ids[j];
                attnMask[offset + j] = 1L;
            }
            for (int j = ids.Length; j < seqLen; j++)
                inputIds[offset + j] = padId;
        }

        // DenseTensor<T> constructor: (Memory<T> data, ReadOnlySpan<int> dimensions)
        int[] shape = [batchSize, seqLen];

        // Los exports ONNX de sentence-transformers difieren en su firma de entrada
        // (los BERT llevan token_type_ids; los RoBERTa puros no). Se construye la
        // lista dinámicamente según lo que el grafo declare.
        var inputs = new List<NamedOnnxValue>(3);
        foreach (var inputName in _session.InputMetadata.Keys)
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
    /// Ruta WordPiece/BERT: EncodeToIds ya añade [CLS]/[SEP].
    /// </summary>
    private long[] EncodeWordPiece(string text, int maxTokens)
    {
        var ids = _tokenizer.EncodeToIds(
            text,
            maxTokenCount: maxTokens,
            normalizedText: out _,
            charsConsumed: out _);

        int tokenCount = Math.Min(ids.Count, maxTokens);
        var result = new long[tokenCount];
        for (int j = 0; j < tokenCount; j++)
            result[j] = ids[j];

        return result;
    }

    /// <summary>
    /// Ruta SentencePiece/XLM-R: tokeniza sin especiales, remapea cada ID crudo
    /// al espacio fairseq (spm i → i+1; spm &lt;unk&gt;=0 → 3) y envuelve la
    /// secuencia con &lt;s&gt; … &lt;/s&gt;.
    /// </summary>
    private long[] EncodeSentencePiece(string text, int maxTokens)
    {
        var spmIds = _tokenizer.EncodeToIds(text);

        int innerCount = Math.Min(spmIds.Count, maxTokens - 2);
        var result = new long[innerCount + 2];

        result[0] = XlmrBosId;
        for (int j = 0; j < innerCount; j++)
        {
            int spmId = spmIds[j];
            result[j + 1] = spmId == 0 ? XlmrUnkId : spmId + FairseqOffset;
        }
        result[innerCount + 1] = XlmrEosId;

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
