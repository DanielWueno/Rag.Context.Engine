using System.Text;
using RagEngine.Core.Domain;
using RagEngine.Core.Utilities;


namespace RagEngine.Core.Pipeline;

/// <summary>
/// Assembles a list of RetrievalResults into a structured Markdown context block
/// ready to be injected into an LLM system prompt.
///
/// Design decisions:
/// - Results are ordered by FilePath + StartLine (not by score) so the LLM
///   sees code in its natural, narrative order — easier to reason about.
/// - A token budget is enforced to avoid exceeding the target LLM's context window.
/// - Each chunk is formatted with rich metadata so the model understands
///   exactly where the code lives without reading the full file.
/// </summary>
public sealed class ContextAssembler
{
    /// <summary>
    /// Converts RetrievalResults into a Markdown context block for LLM injection.
    /// </summary>
    /// <param name="results">The semantic search results to assemble.</param>
    /// <param name="originalQuery">The original natural-language query (embedded in header).</param>
    /// <param name="maxContextTokens">
    /// Token budget. Defaults to 8,000 — safe for most LLMs.
    /// Claude 3: 200K ctx | GPT-4o: 128K | use higher values for modern models.
    /// </param>
    public string Assemble(
        IReadOnlyList<RetrievalResult> results,
        string originalQuery,
        int maxContextTokens = 8_000)
    {
        // Order by file path + start line for narrative coherence
        var ordered = results
            .OrderBy(r => r.Metadata.FilePath)
            .ThenBy(r => r.Metadata.StartLine)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# CONTEXTO DE CÓDIGO FUENTE RECUPERADO");
        sb.AppendLine($"# Query original: \"{originalQuery}\"");
        sb.AppendLine($"# Fragmentos recuperados: {ordered.Count}");
        sb.AppendLine();

        int currentTokens = TokenEstimator.Estimate(sb.ToString());
        int included = 0;

        foreach (var result in ordered)
        {
            var chunkBlock = FormatChunkAsMarkdown(result);
            var chunkTokens = TokenEstimator.Estimate(chunkBlock);

            if (currentTokens + chunkTokens > maxContextTokens)
            {
                int remaining = ordered.Count - included;
                sb.AppendLine($"<!-- {remaining} fragmento(s) adicional(es) omitido(s) por límite de contexto ({maxContextTokens} tokens) -->");
                break;
            }

            sb.Append(chunkBlock);
            currentTokens += chunkTokens;
            included++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats a single RetrievalResult as a Markdown code block with metadata header.
    /// </summary>
    private static string FormatChunkAsMarkdown(RetrievalResult result)
    {
        /*
         * Output example:
         * ════════════════════════════════════════════════════════
         * ## [src/Services/OrderService.cs] — Relevancia: 94.2%
         * **Namespace:** `MyCompany.ERP.Services`
         * **Clase:** `OrderService`
         * **Método:** `ValidateOrderAsync(Order order)`
         * **Líneas:** 87–134
         *
         * ```csharp
         * public async Task<ValidationResult> ValidateOrderAsync(Order order)
         * {
         *     if (order.Items.Count == 0)
         *         return ValidationResult.Fail("El pedido no tiene artículos.");
         *     ...
         * }
         * ```
         * ---
         * ════════════════════════════════════════════════════════
         */

        var m = result.Metadata;
        var lang = m.Language.ToString().ToLowerInvariant();
        var score = (result.SimilarityScore * 100).ToString("F1");

        var block = new StringBuilder();
        block.AppendLine($"## [{m.RelativeFilePath}] — Relevancia: {score}%");

        if (!string.IsNullOrEmpty(m.Namespace))
            block.AppendLine($"**Namespace:** `{m.Namespace}`");
        if (!string.IsNullOrEmpty(m.ClassName))
            block.AppendLine($"**Clase:** `{m.ClassName}`");
        if (!string.IsNullOrEmpty(m.MethodName))
            block.AppendLine($"**Método:** `{m.MethodName}`");

        block.AppendLine($"**Líneas:** {m.StartLine}–{m.EndLine}");
        block.AppendLine();
        block.AppendLine($"```{lang}");
        block.AppendLine(result.Content.TrimEnd());
        block.AppendLine("```");
        block.AppendLine();
        block.AppendLine("---");
        block.AppendLine();

        return block.ToString();
    }
}
