namespace RagEngine.Core.Abstractions;

/// <summary>
/// Detects whether a query is a meta-question about the assistant itself (e.g.
/// "who are you?", "what are you built with?") rather than a domain question.
/// Backed by semantic similarity against a small set of exemplar phrases instead
/// of a hand-maintained regex list, so new phrasings of the same intent don't
/// require a code change — see <c>docs/analisis-futuro/guardrail-dominio-chat.md</c>.
/// </summary>
public interface IMetaIntentDetector
{
    /// <summary>True if <paramref name="query"/> is a meta-question about the assistant.</summary>
    Task<bool> IsMetaIntentAsync(string query, CancellationToken cancellationToken = default);
}
