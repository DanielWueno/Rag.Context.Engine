using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

/// <summary>
/// Re-scores a candidate pool of retrieval results against the original query
/// using a higher-precision model (e.g. a Cross-Encoder) and returns the final
/// Top-K ordered by the new relevance score.
///
/// A bi-encoder (dense embedding) compresses query and chunk independently and
/// compares them by cosine; a cross-encoder reads the (query, chunk) pair
/// jointly through full attention, so it can resolve interactions the
/// bi-encoder loses — at the cost of one inference per candidate. That is why
/// it runs only over the reduced pool that hybrid search already produced,
/// never over the whole collection.
/// </summary>
public interface IReRanker
{
    /// <summary>
    /// Scores each candidate against <paramref name="query"/> and returns the
    /// <paramref name="topK"/> best, ordered by descending relevance. The
    /// returned results carry the cross-encoder score in
    /// <see cref="RetrievalResult.SimilarityScore"/> (sigmoid, range 0..1).
    /// </summary>
    /// <param name="query">The original natural-language query.</param>
    /// <param name="candidates">Candidate pool from first-stage retrieval.</param>
    /// <param name="topK">Number of results to keep after re-scoring.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<IReadOnlyList<RetrievalResult>> ReRankAsync(
        string query,
        IReadOnlyList<RetrievalResult> candidates,
        int topK,
        CancellationToken cancellationToken = default);
}
