namespace RagEngine.Core.Utilities;

/// <summary>
/// Fast heuristic-based token estimator for enforcing LLM context budgets.
///
/// Uses the well-known rule of thumb: ~4 characters per token for English/code,
/// which closely approximates both GPT-4/Claude tokenization for mixed
/// English + source code content without requiring a full tokenizer dependency.
///
/// Accuracy: ±15% vs. actual tokenizer count — sufficient for context budget
/// enforcement where an exact count is not required.
/// </summary>
public static class TokenEstimator
{
    private const double CharsPerToken = 4.0;

    /// <summary>
    /// Estimates the number of tokens in the given text using the 4-chars-per-token heuristic.
    /// </summary>
    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / CharsPerToken);
    }

    /// <summary>
    /// Estimates tokens for multiple text segments combined.
    /// </summary>
    public static int Estimate(params string[] texts)
        => texts.Sum(t => Estimate(t));

    /// <summary>
    /// Returns true if the given text fits within the specified token budget.
    /// </summary>
    public static bool FitsInBudget(string text, int maxTokens)
        => Estimate(text) <= maxTokens;

    /// <summary>
    /// Truncates text to approximately the given token count, preserving whole words.
    /// </summary>
    public static string TruncateToTokens(string text, int maxTokens)
    {
        if (string.IsNullOrEmpty(text)) return text;
        int maxChars = (int)(maxTokens * CharsPerToken);
        if (text.Length <= maxChars) return text;

        // Back off to the last whitespace to avoid cutting mid-word
        int cutPoint = maxChars;
        while (cutPoint > 0 && !char.IsWhiteSpace(text[cutPoint - 1]))
            cutPoint--;

        return cutPoint > 0
            ? text[..cutPoint].TrimEnd() + "..."
            : text[..maxChars] + "...";
    }
}
