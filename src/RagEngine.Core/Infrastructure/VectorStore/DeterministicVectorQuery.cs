using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Domain;
using RagEngine.Core.Diagnostics;

namespace RagEngine.Core.Infrastructure.VectorStore;

internal static class DeterministicVectorQuery
{
    internal const ulong MaxCandidates = 32768;

    internal static string ChunkId(ScoredPoint point) => Guid.Parse(point.Id.Uuid).ToString("D");

    internal static async Task<IReadOnlyList<ScoredPoint>> SearchAsync(
        QdrantClient client, string collection, Query query, string vector, Filter? filter,
        ulong limit, WithPayloadSelector payload, ILogger logger, CancellationToken ct,
        float? threshold = null)
    {
        var queries = 0;
        long examined = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await CompletePrefixAsync(limit, async requested =>
            {
                // Exact search makes the prefix complete, not an HNSW sample whose
                // membership can change with insertion order or a larger limit.
                queries++;
                var points = await client.QueryAsync(
                    collection, query: query, usingVector: vector, filter: filter,
                    limit: requested, payloadSelector: payload,
                    searchParams: new SearchParams { Exact = true },
                    scoreThreshold: threshold, cancellationToken: ct);
                examined += points.Count;
                return points;
            });
        }
        finally
        {
            logger.LogInformation(
                "Deterministic vector query {Vector}: {Queries} queries, {Candidates} candidates returned, limit {Limit}, {ElapsedMs}ms",
                vector, queries, examined, limit, sw.ElapsedMilliseconds);
            RankingDiagnostics.Record(queries, examined);
        }
    }

    internal static async Task<IReadOnlyList<ScoredPoint>> CompletePrefixAsync(
        ulong limit, Func<ulong, Task<IReadOnlyList<ScoredPoint>>> query)
    {
        if (limit == 0 || limit >= MaxCandidates)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Expected 0 < limit < {MaxCandidates}.");
        var requested = limit + 1;
        while (true)
        {
            var points = await query(requested);
            if (points.Any(p => !float.IsFinite(p.Score)))
                throw new InvalidOperationException("Non-finite vector ranking score.");
            var ordered = RankingOrder.Descending(points, p => p.Score, ChunkId).ToArray();
            if (ordered.Length < (long)requested ||
                ordered[(int)limit - 1].Score != ordered[^1].Score)
                return ordered.Take((int)limit).ToArray();

            // A full page ending at the cutoff score cannot prove that all ties
            // were seen. Grow until a lower score/end is observed, or fail closed.
            if (requested == MaxCandidates)
                throw new InvalidOperationException(
                    $"Unresolved ranking tie: candidate limit {MaxCandidates} reached.");
            requested = Math.Min(requested * 2, MaxCandidates);
        }
    }
}
