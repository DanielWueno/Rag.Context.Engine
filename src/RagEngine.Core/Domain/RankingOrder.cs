namespace RagEngine.Core.Domain;

internal static class RankingOrder
{
    public static IOrderedEnumerable<T> Descending<T, TScore>(
        IEnumerable<T> candidates, Func<T, TScore> score, Func<T, string> chunkId) =>
        candidates.OrderByDescending(score).ThenBy(chunkId, StringComparer.Ordinal);
}
