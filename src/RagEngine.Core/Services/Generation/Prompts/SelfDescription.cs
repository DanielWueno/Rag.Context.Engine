using System.Diagnostics.CodeAnalysis;

namespace RagEngine.Core.Services.Generation.Prompts;

/// <summary>
/// Descripción factual del asistente, devuelta literal para meta-preguntas.
///
/// El texto vive aquí y no en RagGenerationService por la Fase 1 de
/// docs/analisis-futuro/centralizacion-prompts-vault.md: consolidar los prompts
/// en un solo sitio antes de decidir si hace falta un vault externo. El servicio
/// conserva un alias de una línea, así que ningún call-site cambió y el refactor
/// es verificablemente byte-idéntico (ver PromptHashesTests).
/// </summary>
internal static class SelfDescription
{
    /// <summary>
    /// Fixed, factual self-description returned verbatim for meta-questions about
    /// the assistant itself (see <see cref="MetaIntentDetector"/>). Never generated
    /// by the LLM — the model is not asked to "recall" what it is.
    /// </summary>
    internal const string Block =
        """
        Soy Rag.Context.Engine, un asistente RAG (Retrieval-Augmented Generation) que
        corre completamente en local, sin conexión a servicios de LLM externos.

        Cómo funciono: busco en un corpus indexado en Qdrant usando búsqueda híbrida
        (embeddings densos con paraphrase-multilingual-MiniLM-L12-v2 + un vector
        disperso estilo BM25), fusiono los resultados con RRF y, cuando aplica,
        los re-rankeo con un cross-encoder (mmarco-mMiniLMv2-L12-H384-v1) antes de
        generar la respuesta con un modelo local vía Ollama (familia Qwen2.5).

        Solo respondo con base en el contenido ya indexado del corpus activo — no
        tengo acceso a internet ni a conocimiento fuera de esa colección.
        """;
}
