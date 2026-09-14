namespace RagEngine.Core.Diagnostics;

/// <summary>
/// Percentiles nearest-rank (1-indexado) sobre una muestra de conteos de
/// tokens reales (ítem 11.1). Extraído de <c>DefaultIngestionPipeline</c>
/// para poder probarlo de forma aislada, sin levantar el pipeline completo.
/// </summary>
public static class TokenPercentileCalculator
{
    /// <summary>
    /// indice = ceil(p*n), 1-indexado, sobre los valores ordenados ascendentemente.
    /// n=0 no produce percentiles ficticios: N=0 y P50/P95 quedan null.
    /// </summary>
    public static (int N, int? P50, int? P95) Compute(IReadOnlyList<int> tokenCounts)
    {
        int n = tokenCounts.Count;
        if (n == 0) return (0, null, null);

        var sorted = tokenCounts.ToArray();
        Array.Sort(sorted);

        int Percentile(double p)
        {
            int rank = (int)Math.Ceiling(p * n);
            rank = Math.Clamp(rank, 1, n);
            return sorted[rank - 1];
        }

        return (n, Percentile(0.50), Percentile(0.95));
    }
}
