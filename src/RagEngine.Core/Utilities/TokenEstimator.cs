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
    /// <summary>
    /// Regla de 4 caracteres por token. Es pública porque los chunkers necesitan el
    /// mismo número: tenían su propia copia (<c>ApproxCharsPerToken = 4</c>) en dos
    /// archivos distintos, y un valor duplicado es un valor que se desincroniza.
    ///
    /// Ojo: los chunkers hacen división ENTERA con este número
    /// (<c>longitud / CharsPerToken</c>) mientras <see cref="Estimate"/> redondea
    /// hacia arriba. No se unificó la aritmética a propósito: cambiar un floor por un
    /// ceiling movería los límites de agrupación de los chunks, lo que invalida los
    /// hashes del índice y obliga a una re-ingesta completa (~19 h para bsuite-repo),
    /// a cambio de nada — las dos formas son la misma heurística con ±15% de error.
    /// Lo que se comparte aquí es la constante, no el redondeo.
    /// </summary>
    public const double CharsPerToken = 4.0;

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
