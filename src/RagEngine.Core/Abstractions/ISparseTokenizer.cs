namespace RagEngine.Core.Abstractions;

/// <summary>
/// Represents a single term in a sparse vector, analogous to one non-zero entry
/// in a BM25 / SPLADE-style sparse representation.
/// </summary>
/// <param name="TermIndex">
///   Stable integer index for the term, derived from its MurmurHash3 fingerprint.
///   Qdrant's sparse vector format requires uint indices, so the hash is taken
///   modulo 2^20 (≈1 M buckets) to keep the index space bounded while keeping
///   collision probability negligible for typical codebases.
/// </param>
/// <param name="Weight">
///   TF-IDF-inspired weight for the term in this document:
///   <c>TF(t,d) = rawCount / totalTerms</c>  ×  log₂(1 + 1/DF_proxy)
///   where DF_proxy is a pre-computed static penalty for high-frequency stop terms.
///   Pure TF is used when IDF data is unavailable (single-document context).
/// </param>
public readonly record struct SparseEntry(uint TermIndex, float Weight);

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Converts raw text into a sparse vector suitable for Qdrant's named-vector
/// sparse index. The contract mirrors BM25 semantics but runs entirely in-process
/// without any external model or network call.
///
/// Implementations must be:
///   • Thread-safe  — called concurrently during batch ingestion.
///   • Deterministic — same input always produces the same (index, weight) pairs.
///   • Zero-allocation on the hot path as much as possible.
/// </summary>
public interface ISparseTokenizer
{
    /// <summary>
    /// Tokenizes <paramref name="text"/>, removes noise and stop-words, computes
    /// per-term TF weights, and returns the non-zero entries of the sparse vector.
    /// </summary>
    /// <param name="text">
    ///   Raw source text — may contain source code, Markdown, or natural language.
    ///   The tokenizer must handle mixed content gracefully.
    /// </param>
    /// <returns>
    ///   A read-only list of <see cref="SparseEntry"/> values, one per unique term,
    ///   sorted ascending by <see cref="SparseEntry.TermIndex"/> (required by Qdrant).
    ///   Returns an empty list for blank or trivially short input.
    /// </returns>
    IReadOnlyList<SparseEntry> Tokenize(string text);

    /// <summary>
    /// Batch variant — tokenizes multiple texts and returns one sparse vector per input.
    /// Default implementation delegates to <see cref="Tokenize"/> per element; override
    /// for a more optimized batch path if needed.
    /// </summary>
    IReadOnlyList<IReadOnlyList<SparseEntry>> TokenizeBatch(IEnumerable<string> texts)
        => texts.Select(Tokenize).ToList();
}
