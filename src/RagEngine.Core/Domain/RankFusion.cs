namespace RagEngine.Core.Domain;

/// <summary>
/// Ítem 9.7 — núcleo puro y único de Reciprocal Rank Fusion ponderada, compartido entre
/// producción (<c>QdrantSemanticRetriever.SearchWeightedFusionAsync</c>) y calibración
/// (<c>poc/RagEngine.Poc.FreeSearch/RecallEvaluator.RankByRrf</c>). Antes de este ítem
/// cada camino tenía su propia copia de <c>score(id) = Σ peso_rama / (k + rango_rama(id))</c>,
/// y sólo el de producción desempataba de forma determinista — el sweep de pesos del PoC
/// dependía del orden de enumeración de un <c>Dictionary</c>, no del contenido. Ahora
/// ambos llaman a <see cref="Fuse{TId}"/>: mismo <c>k</c>, mismos pesos y mismo desempate
/// producen el mismo ranking y el mismo score, sea cual sea el tipo de identificador
/// (string de chunk en producción, índice entero en el PoC).
/// </summary>
public static class RankFusion
{
    /// <summary>Una rama de ranking: mapa id→rango 1-based y su peso en la fusión.</summary>
    public readonly record struct Branch<TId>(IReadOnlyDictionary<TId, int> Rank, double Weight)
        where TId : notnull;

    /// <summary>
    /// Fusiona <paramref name="branches"/> sobre <paramref name="candidateIds"/> y
    /// devuelve los IDs con su score, ordenados por score descendente. Un id ausente en
    /// una rama simplemente no suma el término de esa rama (no es un cero de similitud).
    /// <paramref name="tieBreak"/> es obligatorio: sin un desempate explícito, el orden
    /// de un empate exacto de score queda a merced del orden de enumeración de
    /// <paramref name="candidateIds"/>, que no es un contrato estable.
    /// </summary>
    public static IReadOnlyList<(TId Id, double Score)> Fuse<TId>(
        IEnumerable<TId> candidateIds,
        double k,
        IReadOnlyList<Branch<TId>> branches,
        IComparer<TId> tieBreak)
        where TId : notnull
    {
        var scored = new List<(TId Id, double Score)>();
        foreach (var id in candidateIds)
        {
            var score = 0.0;
            foreach (var branch in branches)
                if (branch.Rank.TryGetValue(id, out var rank))
                    score += branch.Weight / (k + rank);
            scored.Add((id, score));
        }
        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Id, tieBreak)
            .ToArray();
    }
}
