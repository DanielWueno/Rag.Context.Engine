namespace RagEngine.Core.Diagnostics;

/// <summary>Per-evaluation counters; concurrent vector branches share an explicit capture scope.</summary>
public sealed class RankingDiagnostics : IDisposable
{
    private static readonly AsyncLocal<RankingDiagnostics?> Current = new();
    private readonly RankingDiagnostics? _previous;
    private long _queries;
    private long _candidates;

    public long Queries => Interlocked.Read(ref _queries);
    public long CandidatesReturned => Interlocked.Read(ref _candidates);

    public RankingDiagnostics()
    {
        _previous = Current.Value;
        Current.Value = this;
    }

    internal static void Record(int queries, long candidates)
    {
        if (Current.Value is not { } capture) return;
        Interlocked.Add(ref capture._queries, queries);
        Interlocked.Add(ref capture._candidates, candidates);
    }

    public void Dispose() => Current.Value = _previous;
}
