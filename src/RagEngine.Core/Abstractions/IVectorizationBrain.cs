using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

/// <summary>
/// Runs the ONNX embedding model in-process to produce dense vector representations.
/// Supports batch processing, Mean Pooling and L2 normalization. Zero network calls.
/// </summary>
public interface IVectorizationBrain
{
    /// <summary>
    /// Dimensionality of the output vector space (384 for all-MiniLM-L6-v2).
    /// Required for Qdrant collection configuration.
    /// </summary>
    int EmbeddingDimensions { get; }

    /// <summary>
    /// M: longitud máxima de secuencia efectiva del modelo/tokenizador cargado
    /// (ítem 11.1). Es el límite bruto de entrada ONNX, no la capacidad útil de
    /// contenido (L = M - tokens especiales); ver <see cref="TokenizationStats"/>.
    /// </summary>
    int MaxSequenceLength { get; }

    /// <summary>Generates a normalized embedding vector for a single text input.</summary>
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates normalized embeddings for a batch of texts.
    /// 5-10x more efficient than individual calls due to ONNX batch inference.
    /// </summary>
    Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Igual que <see cref="GenerateBatchEmbeddingsAsync"/> pero además reporta,
    /// por cada texto, cuántos tokens reales (no especiales) tenía ANTES de
    /// truncar y cuántos se descartaron con el tokenizador efectivo (ítem 11.1).
    /// No cambia el contenido ni el embedding producido: es una medición aparte.
    /// </summary>
    Task<VectorizationBatchResult> GenerateBatchEmbeddingsWithStatsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default);
}
