namespace RagEngine.Core.Infrastructure.Reranking;

/// <summary>
/// Configuration options for the ONNX Cross-Encoder re-ranker.
/// Bound from the "CrossEncoder" section of appsettings.json.
///
/// El modelo por defecto (mmarco-mMiniLMv2-L12-H384-v1) es la destilación
/// multilingüe de MiniLMv2 afinada sobre mMARCO: comparte el tokenizador
/// SentencePiece/XLM-R del modelo denso, por lo que reutiliza el mismo stack
/// de inferencia sin dependencias nuevas.
/// </summary>
public sealed record CrossEncoderOptions
{
    public const string SectionName = "CrossEncoder";

    /// <summary>
    /// Path to the exported cross-encoder model.onnx file. Admite <c>~</c> y
    /// tokens <c>${RAG_MODELS_DIR}</c>; la resuelve
    /// <see cref="RagEnginePaths.ResolveModelPath"/> en un PostConfigure.
    /// </summary>
    public string ModelPath { get; set; } = "models/mmarco-mMiniLMv2-L12-H384-v1/model.onnx";

    /// <summary>Path to the SentencePiece tokenizer (sentencepiece.bpe.model).</summary>
    public string VocabPath { get; set; } = "models/mmarco-mMiniLMv2-L12-H384-v1/sentencepiece.bpe.model";

    /// <summary>
    /// Maximum token length of the joint (query + chunk) sequence. XLM-R admite
    /// hasta 512; a diferencia del bi-encoder no hay índice que re-ingestar,
    /// así que conviene el máximo — el chunk se trunca a lo que quepa tras la query.
    /// </summary>
    public int MaxSequenceLength { get; init; } = 512;

    /// <summary>
    /// Candidate pairs per ONNX inference batch. Las secuencias del re-ranker son
    /// mucho más largas que las del bi-encoder (hasta 512 tokens), de ahí un
    /// batch menor que el de ingesta.
    /// </summary>
    public int BatchSize { get; init; } = 8;
}
