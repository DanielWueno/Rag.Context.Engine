namespace RagEngine.Core.Abstractions;

/// <summary>
/// Runs the ONNX embedding model in-process to produce dense vector representations.
/// Supports batch processing, Mean Pooling and L2 normalization.
/// </summary>
public interface IVectorizationBrain
{
    /// <summary>
    /// Generates a normalized embedding vector for a single text input.
    /// </summary>
    /// <param name="text">The input text to embed.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A normalized float array of dimension 384 (all-MiniLM-L6-v2).</returns>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates normalized embeddings for a batch of texts.
    /// More efficient than calling EmbedAsync in a loop.
    /// </summary>
    /// <param name="texts">The collection of input texts to embed.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A list of normalized float arrays, one per input text.</returns>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The dimensionality of the output vector. Derived from the loaded ONNX model.
    /// </summary>
    int EmbeddingDimension { get; }
}
