using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RagEngine.Core.Abstractions;

namespace RagEngine.Core.Infrastructure.Vectorization;

/// <summary>
/// Pure C# implementation of <see cref="ISparseTokenizer"/> that produces
/// BM25-style sparse vectors for hybrid search in Qdrant.
///
/// ┌─────────────────────────────────────────────────────────────────────┐
/// │  PIPELINE (per document)                                            │
/// │                                                                     │
/// │  Raw text                                                           │
/// │    │                                                                │
/// │    ▼  1. Normalize — lowercase, strip comments/strings             │
/// │    │                                                                │
/// │    ▼  2. Split — camelCase/PascalCase expansion + regex split      │
/// │    │                                                                │
/// │    ▼  3. Filter — min-length, stop-words, purely-numeric terms     │
/// │    │                                                                │
/// │    ▼  4. TF weighting — raw count / totalTerms (sublinear cap)     │
/// │    │                                                                │
/// │    ▼  5. Static IDF penalty for high-frequency programming terms   │
/// │    │                                                                │
/// │    ▼  6. Hash → uint index (MurmurHash3 mod 2²⁰)                  │
/// │    │                                                                │
/// │    ▼  7. Sort ascending by index (Qdrant requirement)              │
/// │                                                                     │
/// │  IReadOnlyList&lt;SparseEntry&gt;                                         │
/// └─────────────────────────────────────────────────────────────────────┘
///
/// Thread-safety: all mutable state is stack-local or passed as parameters.
/// The instance is safe to register as a Singleton.
/// </summary>
public sealed partial class SparseTokenizer : ISparseTokenizer
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Configuration
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sparse index space size. 2^20 = 1,048,576 buckets.
    /// Collision probability for a 10 k-term vocabulary is ~0.5 % — acceptable
    /// for retrieval. Increase to 2^24 if you observe precision degradation.
    /// </summary>
    private const uint IndexSpaceSize = 1u << 20;   // 1 048 576

    /// <summary>
    /// Terms shorter than this (after expansion) are discarded as noise.
    /// Filters out single-char identifiers that carry no semantic value.
    /// </summary>
    private const int MinTermLength = 2;

    /// <summary>
    /// Sublinear TF cap: raw counts above this are clamped to reduce the
    /// dominance of highly repeated boilerplate (e.g. "return" in every method).
    /// Mirrors BM25's k1 saturation effect without requiring corpus-level stats.
    /// </summary>
    private const int MaxRawCount = 10;

    // ─────────────────────────────────────────────────────────────────────────
    //  Compiled Regexes (compiled once, reused across all calls)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Splits on anything that is not a letter, digit, or underscore.</summary>
    [GeneratedRegex(@"[^\w]|_+", RegexOptions.Compiled)]
    private static partial Regex NonWordSplitter();

    /// <summary>
    /// Expands camelCase and PascalCase into individual words.
    /// e.g. "GetOrderAsync" → "Get", "Order", "Async"
    ///      "QdrantClient"  → "Qdrant", "Client"
    ///
    /// Pattern: insert split point between a lowercase letter followed by an
    /// uppercase letter, OR between consecutive uppercase letters before a
    /// lowercase letter (e.g. "HTTPSClient" → "HTTPS", "Client").
    /// </summary>
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled)]
    private static partial Regex CamelCaseSplitter();

    /// <summary>
    /// Matches single-line C-style comments, string literals, and numeric literals
    /// that are stripped before tokenization to reduce noise.
    /// </summary>
    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/|""[^""]*""|'[^']*'|\b\d[\d_]*\.?\d*[fFdDmMuUlL]?\b",
        RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex NoiseScrubber();

    // ─────────────────────────────────────────────────────────────────────────
    //  Stop-words
    //  Three tiers, each with a different static IDF penalty multiplier:
    //
    //    Tier 1 (Eliminated): structural keywords that are 100 % noise.
    //    Tier 2 (Heavy):      high-frequency but occasionally informative.
    //    Tier 3 (Moderate):   common but may distinguish patterns.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stop words explícitas para mitigar ruido semántico en RAG (Español e Inglés).
    /// Utiliza OrdinalIgnoreCase para máxima velocidad en lookups.
    /// </summary>
    private static readonly HashSet<string> _stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Español
        "que", "qué", "es", "un", "una", "el", "la", "los", "las", "de", "del", 
        "para", "por", "con", "como", "cómo", "en", "a", "al", "su", "sus",
        
        // Inglés
        "what", "is", "a", "an", "the", "of", "for", "by", "with", "how", 
        "in", "to", "on", "and"
    };

    /// <summary>
    /// Tier 1 — Hard-eliminated terms.
    /// These tokens appear in virtually every chunk and contribute zero
    /// discriminative power. Removing them reduces the sparse vector size
    /// and avoids index collisions on meaningless dimensions.
    /// </summary>
    private static readonly FrozenSet<string> EliminatedTerms = FrozenSet.Create(
        StringComparer.Ordinal,
        // C# / TypeScript keywords
        "var", "let", "const", "new", "this", "base", "return", "await",
        "async", "public", "private", "protected", "internal", "static",
        "void", "class", "interface", "struct", "enum", "record", "sealed",
        "abstract", "override", "virtual", "readonly", "partial", "using",
        "namespace", "get", "set", "init", "null", "true", "false", "if",
        "else", "for", "foreach", "while", "do", "switch", "case", "break",
        "continue", "throw", "try", "catch", "finally", "in", "out", "ref",
        "params", "typeof", "is", "as", "where", "from", "select", "yield",
        "value", "default", "object", "string", "int", "bool", "float",
        "double", "long", "byte", "char", "uint", "void",
        // English function-word noise
        "the", "and", "for", "with", "that", "this", "are", "was", "has",
        "have", "not", "but", "from", "its", "into", "then", "than",
        "also", "any", "can", "will", "all", "when", "used", "use"
    );

    /// <summary>
    /// Tier 2 — Heavy penalty (IDF multiplier: 0.30).
    /// Appear frequently but are occasionally meaningful (e.g. "task" in async code,
    /// "list" when you're looking for collection patterns).
    /// </summary>
    private static readonly FrozenSet<string> HeavyPenaltyTerms = FrozenSet.Create(
        StringComparer.Ordinal,
        "task", "list", "array", "type", "name", "value", "data", "item",
        "items", "result", "results", "response", "request", "context",
        "config", "options", "service", "services", "model", "models",
        "exception", "error", "message", "logger", "log", "info",
        "cancellation", "token", "index", "count", "size", "length",
        "add", "get", "set", "create", "build", "run", "start", "stop",
        "init", "load", "save", "read", "write", "send", "receive"
    );

    /// <summary>
    /// Tier 3 — Moderate penalty (IDF multiplier: 0.65).
    /// Domain-adjacent terms that are useful but appear in many chunks.
    /// </summary>
    private static readonly FrozenSet<string> ModeratePenaltyTerms = FrozenSet.Create(
        StringComparer.Ordinal,
        "async", "await", "handler", "factory", "builder", "manager",
        "provider", "repository", "controller", "middleware", "pipeline",
        "client", "server", "host", "register", "resolve", "inject",
        "scope", "singleton", "transient", "event", "callback", "action"
    );

    // ─────────────────────────────────────────────────────────────────────────
    //  ISparseTokenizer
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyList<SparseEntry> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<SparseEntry>();

        // ── Step 1: Scrub noise (comments, string literals, numeric literals) ─
        var scrubbed = NoiseScrubber().Replace(text, " ");

        // ── Step 2: Split on non-word boundaries ──────────────────────────────
        var rawTokens = NonWordSplitter().Split(scrubbed);

        // ── Step 3: CamelCase expansion + normalization + filtering ───────────
        //
        // We use a Dictionary<string, int> for raw counts keyed by the
        // normalized term. This is stack-friendly (no LINQ intermediate lists).
        var termCounts = new Dictionary<string, int>(64, StringComparer.Ordinal);

        foreach (var raw in rawTokens)
        {
            if (raw.Length < MinTermLength) continue;

            // Expand camelCase/PascalCase into sub-terms.
            // "AuthController" → ["Auth", "Controller"]
            var subTerms = CamelCaseSplitter().Split(raw);

            foreach (var sub in subTerms)
            {
                var term = sub.ToLowerInvariant();

                if (term.Length < MinTermLength) continue;

                // Filtrado de Stop Words para evitar ruido semántico
                if (_stopWords.Contains(term)) continue;

                if (EliminatedTerms.Contains(term)) continue;
                if (IsNumericNoise(term)) continue;

                termCounts[term] = termCounts.TryGetValue(term, out var c) ? c + 1 : 1;
            }
        }

        if (termCounts.Count == 0)
            return Array.Empty<SparseEntry>();

        // ── Step 4 + 5: TF weighting + static IDF penalty ────────────────────
        int totalTerms = 0;
        foreach (var kv in termCounts)
            totalTerms += Math.Min(kv.Value, MaxRawCount);

        if (totalTerms == 0)
            return Array.Empty<SparseEntry>();

        float totalTermsF = (float)totalTerms;

        // ── Step 6 + 7: Hash to uint index and collect entries ────────────────
        var entries = new List<SparseEntry>(termCounts.Count);

        foreach (var (term, rawCount) in termCounts)
        {
            // Sublinear TF: clamp raw count, then normalize.
            float tf = Math.Min(rawCount, MaxRawCount) / totalTermsF;

            // Static IDF-proxy multiplier based on tier membership.
            float idfMultiplier = GetStaticIdfMultiplier(term);

            float weight = tf * idfMultiplier;

            // Skip dimensions with negligible weight — keeps the vector compact.
            if (weight < 1e-6f) continue;

            uint index = MurmurHash3(term) % IndexSpaceSize;
            entries.Add(new SparseEntry(index, weight));
        }

        // Qdrant requires sparse entries sorted ascending by index.
        // Sort in-place; the list is small (typically < 200 entries).
        entries.Sort(static (a, b) => a.TermIndex.CompareTo(b.TermIndex));

        // Collapse index collisions (hash conflicts) by summing their weights.
        // This is rare (~0.5%) but must be handled to avoid duplicate indices.
        return MergeCollisions(entries);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the static IDF-proxy multiplier for a term based on its tier.
    /// Higher-frequency tiers receive a lower multiplier, reducing their weight
    /// in the sparse vector without fully eliminating them (unlike Tier 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float GetStaticIdfMultiplier(string term)
    {
        if (HeavyPenaltyTerms.Contains(term))    return 0.30f;
        if (ModeratePenaltyTerms.Contains(term)) return 0.65f;
        return 1.00f;
    }

    /// <summary>
    /// Returns true if the term consists entirely of digits, hex prefixes,
    /// or common numeric suffixes (0x1F, 42L, 3.14f) — all of which are
    /// stripped as structural noise rather than semantic signal.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNumericNoise(ReadOnlySpan<char> term)
    {
        if (term.IsEmpty) return true;

        // Skip "0x..." hex literals
        if (term.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return true;

        bool allNumericOrSuffix = true;
        foreach (char c in term)
        {
            if (!char.IsDigit(c) && c != '.' && c != '_' &&
                c != 'f' && c != 'd' && c != 'l' && c != 'u' &&
                c != 'F' && c != 'D' && c != 'L' && c != 'U')
            {
                allNumericOrSuffix = false;
                break;
            }
        }
        return allNumericOrSuffix;
    }

    /// <summary>
    /// Merges consecutive <see cref="SparseEntry"/> items that share the same
    /// <see cref="SparseEntry.TermIndex"/> (hash collision) by summing their weights.
    /// The input list MUST already be sorted ascending by index.
    /// </summary>
    private static IReadOnlyList<SparseEntry> MergeCollisions(List<SparseEntry> sorted)
    {
        if (sorted.Count <= 1) return sorted;

        var merged = new List<SparseEntry>(sorted.Count);
        var current = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];
            if (next.TermIndex == current.TermIndex)
            {
                // Collision: accumulate weight
                current = current with { Weight = current.Weight + next.Weight };
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);
        return merged;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MurmurHash3 (32-bit, x86)
    //  Chosen over GetHashCode() for three reasons:
    //    1. Deterministic across .NET versions and platforms (GetHashCode is not).
    //    2. Excellent avalanche effect → low collision rate for similar identifiers.
    //    3. No external dependency — implemented in ~20 lines.
    //  Reference: https://github.com/aappleby/smhasher
    // ─────────────────────────────────────────────────────────────────────────

    private const uint Murmur3Seed = 0xDEAD_C0DEu;  // fixed seed for determinism

    /// <summary>
    /// Computes a 32-bit MurmurHash3 fingerprint for <paramref name="term"/>
    /// using the project-wide fixed seed. Operates on the UTF-16 code units of
    /// the string, which is consistent (same string → same hash) on all platforms.
    /// </summary>
    private static uint MurmurHash3(string term)
    {
        ReadOnlySpan<char> span = term.AsSpan();
        uint h = Murmur3Seed;
        const uint c1 = 0xCC9E_2D51u;
        const uint c2 = 0x1B87_3593u;

        // Process 2 chars (4 bytes) per block
        int blocks = span.Length / 2;
        for (int i = 0; i < blocks; i++)
        {
            uint k = (uint)span[i * 2] | ((uint)span[i * 2 + 1] << 16);
            k *= c1;
            k = RotateLeft(k, 15);
            k *= c2;

            h ^= k;
            h = RotateLeft(h, 13);
            h = h * 5 + 0xE654_6B64u;
        }

        // Tail (odd char remaining)
        if ((span.Length & 1) != 0)
        {
            uint tail = span[span.Length - 1];
            tail *= c1;
            tail = RotateLeft(tail, 15);
            tail *= c2;
            h ^= tail;
        }

        // Finalization — fmix32
        h ^= (uint)span.Length;
        h ^= h >> 16;
        h *= 0x85EB_CA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2_AE35u;
        h ^= h >> 16;

        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int count)
        => (value << count) | (value >> (32 - count));
}
