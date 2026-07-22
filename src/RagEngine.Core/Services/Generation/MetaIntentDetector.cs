using System.Linq;
using System.Text.RegularExpressions;

namespace RagEngine.Core.Services.Generation;

/// <summary>
/// Detects meta-questions about the assistant itself (e.g. "who are you?", "what
/// are you built with?"). Shared between <see cref="RagGenerationService"/> (which
/// short-circuits generation for these) and any caller that needs to know ahead of
/// time whether a query will be answered from retrieved content at all — e.g. the
/// API host, so it doesn't attach unrelated retrieved sources to a self-description
/// answer.
/// </summary>
public static class MetaIntentDetector
{
    /// <summary>
    /// Closed list of keyword patterns for meta-questions about the assistant
    /// itself, taken verbatim from real phrases observed in production logs
    /// (see docs/analisis-futuro/guardrail-dominio-chat.md).
    /// </summary>
    private static readonly Regex[] Patterns =
    [
        new(@"qui[ée]n\s+(eres|sos)", RegexOptions.IgnoreCase),
        new(@"qu[ée]\s+(proyecto|tecnolog[íi]a|modelo)\s+(analizas|usas|eres|corres)", RegexOptions.IgnoreCase),
        new(@"con\s+qu[ée]\s+(est[áa]s\s+)?(entrenado|hecho|construido)", RegexOptions.IgnoreCase),
        new(@"en\s+qu[ée]\s+idioma", RegexOptions.IgnoreCase),
        new(@"alucinacion", RegexOptions.IgnoreCase),
        new(@"cu[áa]ntos\s+(archivos|documentos)", RegexOptions.IgnoreCase),
    ];

    /// <summary>True if <paramref name="query"/> matches any meta-intent pattern.</summary>
    public static bool IsMetaIntent(string query) =>
        Patterns.Any(pattern => pattern.IsMatch(query));
}
