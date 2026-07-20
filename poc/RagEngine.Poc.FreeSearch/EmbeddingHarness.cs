using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Infrastructure.Vectorization;

namespace RagEngine.Poc.FreeSearch;

/// <summary>
/// Fase 3 (embeddings): envuelve el MISMO OnnxVectorizationBrain del motor real,
/// construido de forma standalone (IOptions + NullLogger). Los vectores salen
/// L2-normalizados, así que la similitud coseno == producto punto.
///
/// Advertencia heredada del motor: el embedder trunca a MaxSequenceLength (256),
/// mientras que los chunkers dimensionan a 512. Los chunks grandes se truncan a la
/// mitad al vectorizar; téngalo en cuenta al interpretar el recall del baseline.
/// </summary>
public sealed class EmbeddingHarness : IDisposable
{
    private readonly OnnxVectorizationBrain _brain;

    public int Dimensions => _brain.EmbeddingDimensions;

    public EmbeddingHarness(PocSettings settings)
    {
        _brain = new OnnxVectorizationBrain(
            Options.Create(settings.OnnxBrain),
            NullLogger<OnnxVectorizationBrain>.Instance);
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => _brain.GenerateEmbeddingAsync(text, ct);

    public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = await _brain.GenerateBatchEmbeddingsAsync(texts, ct);
        return result.ToArray();
    }

    /// <summary>Similitud coseno. Como los vectores vienen L2-normalizados, es un producto punto.</summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Dimensiones distintas: {a.Length} vs {b.Length}");

        float dot = 0f;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }

    public void Dispose()
    {
        // El InferenceSession de ONNX lanza en el teardown en ARM (mutex lock failed),
        // ya con los resultados impresos. Se traga para que el proceso salga limpio.
        try { _brain.Dispose(); } catch { /* ignorar crash de finalización de ONNX */ }
    }
}
