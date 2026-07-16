namespace RagEngine.Core.Infrastructure.Vectorization;

/// <summary>
/// Configuration options for the ONNX-based vectorization brain.
/// Bound from the "OnnxBrain" section of appsettings.json.
/// </summary>
public sealed record OnnxBrainOptions
{
    public const string SectionName = "OnnxBrain";

    /// <summary>Path to the exported model.onnx file.</summary>
    public string ModelPath { get; init; } = "models/all-MiniLM-L6-v2/model.onnx";

    /// <summary>Path to the vocab.txt file for WordPiece tokenization.</summary>
    public string VocabPath { get; init; } = "models/all-MiniLM-L6-v2/vocab.txt";

    /// <summary>Maximum token sequence length. 256 for all-MiniLM-L6-v2.</summary>
    public int MaxSequenceLength { get; init; } = 256;

    /// <summary>Number of chunks per ONNX inference batch. Higher = faster but more memory.</summary>
    public int BatchSize { get; init; } = 32;

    /// <summary>Output embedding dimensionality. 384 for all-MiniLM-L6-v2.</summary>
    public int EmbeddingDimensions { get; init; } = 384;
}
