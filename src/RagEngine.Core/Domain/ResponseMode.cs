namespace RagEngine.Core.Domain;

/// <summary>
/// Selects which system-prompt persona answers the question. <see cref="Simple"/>
/// (the default) explains in plain language for non-technical readers — support,
/// QA, business stakeholders — who cannot tell whether a code snippet shown to
/// them is the answer, a citation, or an error, so it never shows raw code or
/// file/line citations. <see cref="Technical"/> keeps the existing code/docs-aware
/// prompts with fenced code blocks and file citations, for developers who
/// explicitly opt in via the toggle.
/// </summary>
public enum ResponseMode
{
    /// <summary>Plain-language answers, no code shown, sources described in words. Default.</summary>
    Simple,

    /// <summary>Developer-oriented answers: fenced code blocks, file/line citations.</summary>
    Technical
}
