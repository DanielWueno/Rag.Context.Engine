using System.Text.RegularExpressions;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Services.Generation;

/// <summary>
/// Filtro determinista posterior a la generación para <see cref="ResponseMode.Simple"/> —
/// la red de seguridad estructural de la Fase 1 de
/// docs/analisis-futuro/modo-respuesta-simple-codigo.md.
///
/// Vive aparte de <see cref="RagGenerationService"/> (ítem 2.2 del plan) porque no
/// comparte nada con la orquestación: no toca retrieval, ni prompts, ni el kernel.
/// Es una función pura de texto a texto, y separarla es lo que permite probarla sin
/// levantar la tubería completa (ver <c>SanitizeSimpleAnswerTests</c>).
/// </summary>
internal static class SimpleAnswerSanitizer
{
    /// <summary>Alias de <see cref="Prompts.AnswerNotices.CodeBlockOmitted"/>. El texto vive en Prompts/.</summary>
    private const string CodeBlockOmittedNotice = Prompts.AnswerNotices.CodeBlockOmitted;

    /// <summary>Alias de <see cref="Prompts.AnswerNotices.IdentifierOmitted"/>. El texto vive en Prompts/.</summary>
    private const string IdentifierOmittedNotice = Prompts.AnswerNotices.IdentifierOmitted;

    /// <summary>Matches a complete ``` fenced block, capturing its body (group 1).</summary>
    private static readonly Regex FencedCodeBlockPattern = new(
        @"```[^\n]*\n?([\s\S]*?)```", RegexOptions.Compiled);

    /// <summary>Matches a single-line inline code span, e.g. `` `GenerarPlanAuditoria` ``. Captures the inner text (group 1).</summary>
    private static readonly Regex InlineCodeSpanPattern = new(
        @"`([^`\n]+)`", RegexOptions.Compiled);

    /// <summary>
    /// An inline-code span whose inner text is ONLY identifier characters (letters,
    /// digits, underscore, dot) — no spaces, parentheses, or operators. This is the
    /// shape a resumen or the model itself produces when it backticks a single field
    /// or method name as an aside (e.g. `` `Provisionada` ``, `` `IsCancelable` ``)
    /// rather than an actual code snippet — safe to humanize instead of blacking out,
    /// since there is no risk of leaking a real expression/statement.
    /// </summary>
    private static readonly Regex SimpleIdentifierShapePattern = new(
        @"^[A-Za-z0-9_.]+$", RegexOptions.Compiled);

    /// <summary>
    /// Zero-width split point right after a lowercase letter/digit and right before
    /// an uppercase letter — used to "de-camelcase" an identifier into space-separated
    /// words. Deliberately simple (no ALLCAPS-acronym handling) — good enough for this
    /// codebase's naming convention, where compound identifiers are Spanish/English
    /// words concatenated in PascalCase (`GenerarPlanAuditoria` → "generar plan
    /// auditoria"), not technical acronyms.
    /// </summary>
    private static readonly Regex CamelBoundaryPattern = new(
        @"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    /// <summary>Matches a declarative-attribute decoration, e.g. `[SupportedEstatus(...)]`.</summary>
    private static readonly Regex AttributeDecorationPattern = new(
        @"\[[A-Z][A-Za-z0-9]*\([^\]\n]*\)\]", RegexOptions.Compiled);

    /// <summary>
    /// Matches a dotted PascalCase chain, e.g. `TipoEstatus.Completado` or
    /// `ServicioCliente.GenerarPlanAuditoria` — this shape almost never occurs in
    /// legitimate Spanish/English prose, so it is a low-false-positive signal.
    /// </summary>
    private static readonly Regex DottedIdentifierPattern = new(
        @"\b[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*){1,}\b", RegexOptions.Compiled);

    /// <summary>Matches a snake_case identifier — not a natural-language shape in Spanish or English prose.</summary>
    private static readonly Regex SnakeCaseIdentifierPattern = new(
        @"\b[a-z][a-z0-9]*(?:_[a-z0-9]+){1,}\b", RegexOptions.Compiled);

    /// <summary>
    /// Matches a bare identifier with two or more capitalized "humps" smashed
    /// together with no separators, e.g. `GenerarPlanAuditoria` or
    /// `ServicioCliente`. Deliberately conservative: a single capitalized word
    /// (a legitimate proper noun, e.g. "Auditoria") never matches — only tokens
    /// that already look like `PascalCaseCompoundWords` do, which keeps ordinary
    /// prose with capitalized proper nouns untouched.
    /// </summary>
    private static readonly Regex CamelHumpIdentifierPattern = new(
        @"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*){1,}\b", RegexOptions.Compiled);

    /// <summary>
    /// Strips anything that still looks like source code from an answer, regardless of
    /// whether the model followed the prompt's plain-language rules, before it ever
    /// reaches the caller. Order matters — fenced blocks and inline spans are removed
    /// first (they can contain identifier shapes that would otherwise get
    /// double-redacted), then the remaining bare-text heuristics run against what's left.
    ///
    /// Word-shaped identifiers (dotted/snake_case/CamelHump) are "de-camelcased"
    /// into space-separated lowercase words instead of blacked out — a real user
    /// complaint against the original all-opaque placeholder was that it destroyed
    /// even the little inferential value a raw identifier gave a reader (see
    /// docs/analisis-futuro/modo-respuesta-simple-codigo.md, Fase 2 follow-up). This
    /// is NOT a semantic explanation (it doesn't know what the field MEANS, only
    /// decodes its name) — rule 3 of <see cref="Prompts.SimpleSystemPrompt.Template"/>
    /// is the real fix (tell the model to explain the concept instead of naming it);
    /// this is the fallback for when that instruction isn't followed. Attribute
    /// decorations and fenced code blocks have no natural-language reading and stay
    /// fully opaque.
    /// </summary>
    internal static string Sanitize(string answer)
    {
        if (string.IsNullOrEmpty(answer))
            return answer;

        var sanitized = FencedCodeBlockPattern.Replace(answer, match =>
            string.IsNullOrWhiteSpace(match.Groups[1].Value) ? string.Empty : CodeBlockOmittedNotice);

        sanitized = InlineCodeSpanPattern.Replace(sanitized, match =>
        {
            var inner = match.Groups[1].Value;
            return SimpleIdentifierShapePattern.IsMatch(inner)
                ? HumanizeIdentifier(inner)
                : IdentifierOmittedNotice;
        });
        sanitized = AttributeDecorationPattern.Replace(sanitized, IdentifierOmittedNotice);
        sanitized = DottedIdentifierPattern.Replace(sanitized, match => HumanizeIdentifier(match.Value));
        sanitized = SnakeCaseIdentifierPattern.Replace(sanitized, match => HumanizeIdentifier(match.Value));
        sanitized = CamelHumpIdentifierPattern.Replace(sanitized, match => HumanizeIdentifier(match.Value));

        return sanitized;
    }

    /// <summary>
    /// De-camelcases a dotted/snake_case/PascalCase identifier into space-separated
    /// lowercase words (`GenerarPlanAuditoria` → "generar plan auditoria",
    /// `TipoEstatus.Completado` → "tipo estatus completado"). See
    /// <see cref="Sanitize"/> for why this replaces outright redaction.
    /// </summary>
    private static string HumanizeIdentifier(string token)
    {
        var segments = token.Split('.', '_');
        var words = segments.SelectMany(seg => CamelBoundaryPattern.Split(seg));
        return string.Join(" ", words).ToLowerInvariant();
    }
}
