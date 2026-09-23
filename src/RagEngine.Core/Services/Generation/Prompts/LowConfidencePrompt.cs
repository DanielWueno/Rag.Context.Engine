using System.Diagnostics.CodeAnalysis;

namespace RagEngine.Core.Services.Generation.Prompts;

/// <summary>
/// Cláusula que se concatena cuando el contexto tiene score bajo.
///
/// La rama (b) DESCRIBE la forma de respuesta prohibida en vez de citarla, y trae un
/// ejemplo del texto que sí se quiere. No es estilo: la versión anterior citaba la
/// frase mala literalmente como "example of what NOT to do" y qwen2.5-coder la copiaba
/// tal cual en 8 de 22 consultas de banda media (ítem 4.4 del plan,
/// docs/eval/quality/4.4-antes-media.json). Al añadir un ejemplo aquí, que sea del texto
/// deseado — un modelo de 7B imita lo concreto que ve, no la etiqueta que lo envuelve.
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

        (b) It does NOT address the question → do not use the hedge of branch (a).
            Your whole reply is the absence itself, and then you STOP. Do not relay
            the fragment, do not summarise it, do not name its topic, and do not
            comment on how relevant it is or on how the search went.
            A fragment that talks about the retrieval rather than about the subject
            matter — its relevance, its score, how well it fits the question — never
            counts as addressing the question, so it is always this branch (b). A
            remark about the search is not content: the reader cannot act on it, and
            wrapping it in the hedge of branch (a) produces a sentence that reads
            like an answer while carrying no information. That is the single worst
            outcome here, worse than answering nothing.
            EXAMPLE of the reply that IS wanted, complete — nothing before it and
            nothing after it: "No hay nada relevante sobre eso en el contenido
            indexado. Si me precisas el término o el módulo, lo busco de nuevo."
            Follow that shape: one short sentence stating the absence, and at most
            one invitation to reformulate.

        Rule 2 of the rules above still applies in full: never fill the gap with
        knowledge that did not come from the context.
        """;
}
