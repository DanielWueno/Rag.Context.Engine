using RagEngine.Core.Abstractions;
using RagEngine.Core.Infrastructure.Vectorization;

namespace RagEngine.Poc.FreeSearch;

/// <summary>
/// Envuelve el MISMO SparseTokenizer de Core (BM25/SPLADE-style, sin modelo ni red)
/// para reproducir la rama dispersa del híbrido de producción. La similitud dispersa
/// es el producto punto de los pesos por término compartidos — igual que la puntúa
/// Qdrant en su índice sparse.
///
/// Igual que el pipeline real, tokeniza EnrichedContent (DefaultIngestionPipeline:241).
/// </summary>
public sealed class SparseHarness
{
    private readonly ISparseTokenizer _tokenizer = new SparseTokenizer();

    public Dictionary<uint, float> Vectorize(string text)
    {
        var vec = new Dictionary<uint, float>();
        foreach (var e in _tokenizer.Tokenize(text))
            vec[e.TermIndex] = e.Weight;
        return vec;
    }

    /// <summary>Producto punto disperso: Σ_{t ∈ a∩b} a[t]·b[t]. Itera el vector más chico.</summary>
    public static float Dot(Dictionary<uint, float> a, Dictionary<uint, float> b)
    {
        if (a.Count > b.Count) (a, b) = (b, a);
        float sum = 0f;
        foreach (var (term, wa) in a)
            if (b.TryGetValue(term, out var wb))
                sum += wa * wb;
        return sum;
    }
}
