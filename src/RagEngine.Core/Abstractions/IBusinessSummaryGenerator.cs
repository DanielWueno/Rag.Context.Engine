namespace RagEngine.Core.Abstractions;

using RagEngine.Core.Domain;

/// <summary>
/// Genera un resumen de negocio (lenguaje natural, no técnico) de un chunk de código,
/// vía un LLM local. Es un artefacto del índice (tercer vector "dense-resumen"), no
/// generación conversacional — ver <see cref="IRagGenerationService"/> para eso.
/// </summary>
public interface IBusinessSummaryGenerator
{
    /// <summary>
    /// Aísla fallos de contenido/conexión: si el LLM no responde o revienta,
    /// devuelve null (el chunk queda sin vector de resumen esta corrida, recuperable
    /// en una reanudación). Un fallo de AUTENTICACIÓN (401/403 — credencial ausente o
    /// inválida) NO se aísla: se propaga como
    /// <see cref="RagEngine.Core.Infrastructure.Summary.BusinessSummaryAuthenticationException"/>
    /// porque es un fallo de configuración, no algo que una reanudación arregle sola
    /// (ítem 10.6.2, porte del 17.1 local).
    /// </summary>
    Task<BusinessSummaryResult?> GenerateAsync(CodeChunk chunk, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ítem 5.b (experimental, opt-in): un único resumen de negocio para TODOS los chunks
    /// de un mismo archivo/tipo, reutilizado por cada uno de ellos en vez de llamar al LLM
    /// por chunk. <paramref name="chunks"/> debe compartir archivo (y tipo, si aplica) — el
    /// llamador es responsable de agrupar antes de invocar. Mismo contrato de aislamiento
    /// de fallos que <see cref="GenerateAsync"/>.
    /// </summary>
    Task<BusinessSummaryResult?> GenerateForGroupAsync(
        IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken = default);
}

/// <param name="Text">Texto del resumen, o el sentinel crudo si <see cref="SinContenidoDeNegocio"/> es true.</param>
/// <param name="SinContenidoDeNegocio">true si el chunk no tiene significado de negocio (imports, boilerplate, etc.) — no debe generar vector.</param>
public sealed record BusinessSummaryResult(string Text, bool SinContenidoDeNegocio);
