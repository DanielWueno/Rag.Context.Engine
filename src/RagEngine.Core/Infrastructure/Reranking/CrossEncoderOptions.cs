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

    /// <summary>
    /// Cuando es <c>true</c>, el score del candidato ganador se vuelve a calcular en un
    /// lote de tamaño 1 después del re-rank, y ese valor es el que queda en la posición
    /// #1 del resultado. Motivo: el paso de re-rank hace padding dinámico al máximo real
    /// de cada lote sobre un modelo int8, así que el score de un par depende de sus
    /// vecinos de lote — y como el pool es 3×TopK, el mismo par puntúa distinto según el
    /// TopK pedido. El gate de confianza lee justamente ese score (<c>chunks[0]</c>), y
    /// calibrar un umbral absoluto sobre un número que se mueve con el TopK no se
    /// sostiene. Un lote de 1 fija <c>seqLen</c> a la longitud del propio par, con lo que
    /// el score pasa a ser función únicamente de (consulta, chunk).
    ///
    /// El ORDEN de los resultados no cambia: lo sigue decidiendo la pasada por lotes. Sólo
    /// se sustituye el número de la posición #1, que puede quedar por debajo del score de
    /// la posición #2. Coste: una inferencia extra por consulta.
    ///
    /// Default <c>false</c> — es el rollback del ítem 4.2 del plan: se apaga en
    /// configuración, sin recompilar. Ver
    /// docs/analisis-futuro/gate-de-confianza-score-inestable-y-fuga-de-prompt.md.
    /// </summary>
    public bool StableGateScore { get; init; }
}
