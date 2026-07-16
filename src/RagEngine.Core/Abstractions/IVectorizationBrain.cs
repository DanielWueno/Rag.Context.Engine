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

    /// <summary>Generates a normalized embedding vector for a single text input.</summary>
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates normalized embeddings for a batch of texts.
    /// 5-10x more efficient than individual calls due to ONNX batch inference.
    /// </summary>
    Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default);
}
