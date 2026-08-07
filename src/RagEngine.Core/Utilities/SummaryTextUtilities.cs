using System.Text.RegularExpressions;

namespace RagEngine.Core.Utilities;

/// <summary>
/// Shared post-processing for business summaries produced by
/// <c>OllamaBusinessSummaryGenerator</c> and cached in <c>SummaryCache</c>.
/// Used both by the API's <c>SourceDto.Redacted</c> (summary shown to the user)
/// and by <c>RagGenerationService</c> (summary fed back into the LLM as context
/// for <see cref="RagEngine.Core.Domain.ResponseMode.Simple"/> — see
/// docs/analisis-futuro/modo-respuesta-simple-codigo.md, Fase 2).
/// </summary>
public static class SummaryTextUtilities
{
    /// <summary>
    /// The ingestion-time business-summary prompt always makes the model start
    /// each summary by "naming the entity, screen, or file, followed by a colon"
    /// (e.g. <c>"ServicioCliente.cs: ..."</c>) — a leftover technical hint. Strips
    /// only that first, always-present prefix — not any later colon in the
    /// sentence (<c>count: 1</c>).
    /// </summary>
    private static readonly Regex EntityPrefixPattern = new(@"^\s*[^\n:]{1,80}:\s*", RegexOptions.Compiled);

    /// <summary>Strips the leading entity-name prefix from a cached business summary, if present.</summary>
    public static string? StripEntityPrefix(string? resumen) =>
        string.IsNullOrEmpty(resumen) ? resumen : EntityPrefixPattern.Replace(resumen, string.Empty, 1);
}
