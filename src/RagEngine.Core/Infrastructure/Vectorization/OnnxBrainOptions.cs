namespace RagEngine.Core.Infrastructure.Vectorization;

/// <summary>
/// Tipo de tokenizador que alimenta al modelo ONNX de embeddings.
/// </summary>
public enum OnnxTokenizerKind
{
    /// <summary>
    /// WordPiece estilo BERT (vocab.txt). Usado por all-MiniLM-L6-v2 y demás
    /// modelos monolingües de la familia BERT uncased.
    /// </summary>
    WordPiece,

    /// <summary>
    /// SentencePiece Unigram estilo XLM-RoBERTa (sentencepiece.bpe.model).
    /// Usado por los modelos multilingües de sentence-transformers
    /// (p. ej. paraphrase-multilingual-MiniLM-L12-v2). Los IDs de SentencePiece
    /// se remapean al espacio del modelo con el offset fairseq (+1).
    /// </summary>
    SentencePiece
}

/// <summary>
/// Configuration options for the ONNX-based vectorization brain.
/// Bound from the "OnnxBrain" section of appsettings.json.
/// </summary>
public sealed record OnnxBrainOptions
{
    public const string SectionName = "OnnxBrain";

    /// <summary>Path to the exported model.onnx file.</summary>
    public string ModelPath { get; init; } = "models/paraphrase-multilingual-MiniLM-L12-v2/model.onnx";

    /// <summary>
    /// Path to the tokenizer file: vocab.txt (WordPiece) or
    /// sentencepiece.bpe.model (SentencePiece).
    /// </summary>
    public string VocabPath { get; init; } = "models/paraphrase-multilingual-MiniLM-L12-v2/sentencepiece.bpe.model";

    /// <summary>Tokenizer family required by the model.</summary>
    public OnnxTokenizerKind TokenizerType { get; init; } = OnnxTokenizerKind.SentencePiece;

    /// <summary>Maximum token sequence length (256 balances quality vs. CPU cost).</summary>
    public int MaxSequenceLength { get; init; } = 256;

    /// <summary>Number of chunks per ONNX inference batch. Higher = faster but more memory.</summary>
    public int BatchSize { get; init; } = 32;

    /// <summary>Output embedding dimensionality. 384 for both MiniLM variants.</summary>
    public int EmbeddingDimensions { get; init; } = 384;
}
