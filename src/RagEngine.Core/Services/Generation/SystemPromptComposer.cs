using RagEngine.Core.Domain;

namespace RagEngine.Core.Services.Generation;

/// <summary>
/// Elige la plantilla de sistema del turno y la compone con el bloque de contexto.
///
/// Separado de <see cref="RagGenerationService"/> por el ítem 2.2 del plan: la
/// decisión de "qué persona adopta el modelo" depende sólo del modo y de los
/// metadatos de los chunks, no de la tubería. Los textos siguen viviendo en
/// <c>Prompts/</c> (ítem 2.3); aquí sólo hay alias de una línea y la regla de
/// selección, así que <c>PromptHashesTests</c> los sigue cubriendo sin cambios.
/// </summary>
internal static class SystemPromptComposer
{
    /// <summary>Alias de <see cref="Prompts.CodeSystemPrompt.Template"/>. El texto vive en Prompts/.</summary>
    private const string CodeSystemPromptTemplate = Prompts.CodeSystemPrompt.Template;

    /// <summary>Alias de <see cref="Prompts.DocsSystemPrompt.Template"/>. El texto vive en Prompts/.</summary>
    private const string DocsSystemPromptTemplate = Prompts.DocsSystemPrompt.Template;

    /// <summary>Alias de <see cref="Prompts.SimpleSystemPrompt.Template"/>. El texto vive en Prompts/.</summary>
    private const string SimpleSystemPromptTemplate = Prompts.SimpleSystemPrompt.Template;

    /// <summary>Alias de <see cref="Prompts.LowConfidencePrompt.Addendum"/>. El texto vive en Prompts/.</summary>
    internal const string LowConfidenceAddendum = Prompts.LowConfidencePrompt.Addendum;

    /// <summary>Alias de <see cref="Prompts.SelfDescription.Block"/>. El texto vive en Prompts/.</summary>
    internal const string SelfDescriptionBlock = Prompts.SelfDescription.Block;

    /// <summary>Alias de <see cref="Prompts.NoGroundingSystemPrompt.Template"/>. El texto vive en Prompts/.</summary>
    private const string NoGroundingSystemPromptTemplate = Prompts.NoGroundingSystemPrompt.Template;

    /// <summary>Alias de <see cref="Prompts.AnswerNotices.NoContextFallback"/>. El texto vive en Prompts/.</summary>
    internal const string NoContextFallbackMessage = Prompts.AnswerNotices.NoContextFallback;

    /// <summary>
    /// Picks the system prompt for this turn. <see cref="ResponseMode.Simple"/> always
    /// wins regardless of content — the plain-language template replaces the
    /// code/docs split entirely. Only for <see cref="ResponseMode.Technical"/> does the
    /// kind of content dominating the retrieved chunks decide code-oriented vs
    /// docs-oriented: a simple majority is enough, since mixed repositories (e.g. a few
    /// README hits alongside mostly code) should still get the code prompt.
    /// </summary>
    internal static string SelectTemplate(IReadOnlyList<RetrievalResult> chunks, ResponseMode responseMode)
    {
        if (responseMode == ResponseMode.Simple)
            return SimpleSystemPromptTemplate;

        var docChunks = chunks.Count(c => c.Metadata.Language.IsDocumentation());
        return docChunks * 2 >= chunks.Count ? DocsSystemPromptTemplate : CodeSystemPromptTemplate;
    }

    /// <summary>
    /// Rellena la plantilla elegida con el bloque de contexto y la frase de fallback, y
    /// añade el matiz de confianza media cuando lo hay.
    /// </summary>
    internal static string ComposeGrounded(string template, string contextBlock, string? confidenceAddendum)
    {
        var systemPrompt = string.Format(template, contextBlock, NoContextFallbackMessage);
        return confidenceAddendum is null ? systemPrompt : systemPrompt + confidenceAddendum;
    }

    /// <summary>
    /// Prompt del camino sin anclaje: conversa sin contexto recuperado, en vez de
    /// cortar con una frase fija. Ver
    /// docs/analisis-futuro/guardrail-banda-baja-conversacional.md.
    /// </summary>
    internal static string ComposeNoGrounding() =>
        string.Format(NoGroundingSystemPromptTemplate, SelfDescriptionBlock);
}
