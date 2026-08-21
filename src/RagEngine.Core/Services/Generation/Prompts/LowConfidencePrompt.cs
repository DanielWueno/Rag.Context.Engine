using System.Diagnostics.CodeAnalysis;

namespace RagEngine.Core.Services.Generation.Prompts;

/// <summary>
/// Cláusula que se concatena cuando el contexto tiene score bajo.
///
/// El texto vive aquí y no en RagGenerationService por la Fase 1 de
/// docs/analisis-futuro/centralizacion-prompts-vault.md: consolidar los prompts
/// en un solo sitio antes de decidir si hace falta un vault externo. El servicio
/// conserva un alias de una línea, así que ningún call-site cambió y el refactor
/// es verificablemente byte-idéntico (ver PromptHashesTests).
/// </summary>
internal static class LowConfidencePrompt
{
    /// <summary>
    /// Short instruction appended to whichever system prompt was already selected
    /// when the top chunk's score falls in the mid confidence band
    /// (<see cref="RagGenerationOptions.LowConfidenceThreshold"/> ≤ score &lt;
    /// <see cref="RagGenerationOptions.HighConfidenceThreshold"/>). Does not replace
    /// the template — it is concatenated after it, so the grounding rules above
    /// still apply in full.
    /// </summary>
    internal const string Addendum =
        """


        ═══════════════════════════════════════════════
        LOW-CONFIDENCE CONTEXT — ADDITIONAL RULE:
        ═══════════════════════════════════════════════
        The retrieved context above has a low relevance score for this question —
        it may not actually contain the answer. Do NOT present your answer as a
        confirmed fact.

        First decide whether the closest fragment actually says something about
        what was asked:

        (a) It does address the question, if only partially → hedge explicitly
            ("No encontré una coincidencia clara en el contenido indexado, pero el
            fragmento más cercano dice...") and then offer that content as a
            tentative lead, never as a definitive answer.

        (b) It does NOT address the question → say only that there is nothing
            relevant in the indexed content, and STOP. Do not relay the fragment,
            do not describe it, and do not comment on its relevance.
            EXAMPLE of what NOT to do (an answer of exactly this shape was
            produced and is useless to the reader): "No encontré una coincidencia
            clara en el contenido indexado, pero el fragmento más cercano dice que
            el contexto proporcionado no tiene una relevancia alta para la
            pregunta." That sentence relays a statement ABOUT the context instead
            of content, so it says nothing while looking like an answer. In that
            situation the whole reply should be a short, plain "no hay nada
            relevante sobre eso en el contenido indexado", optionally suggesting a
            more specific question.

        Rule 2 of the rules above still applies in full: never fill the gap with
        knowledge that did not come from the context.
        """;
}
