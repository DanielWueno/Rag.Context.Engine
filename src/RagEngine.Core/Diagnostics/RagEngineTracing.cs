using System.Diagnostics;

namespace RagEngine.Core.Diagnostics;

/// <summary>
/// Ítem 13.4: fuente de trazas (<see cref="System.Diagnostics.ActivitySource"/>) que
/// correlaciona un turno de <c>/api/ask</c>/<c>/api/ask/stream</c> con los spans de sus
/// pasos: retrieval, rerank (cuando corre), gate de confianza, ensamblado de contexto y
/// generación.
///
/// El span raíz (<see cref="Steps.Turn"/>) lo crea el host (<c>Program.cs</c>) ANTES de
/// invocar <c>IRagGenerationService.AskStreamingAsync</c> — de ahí que su
/// <c>TraceId</c> pueda reusarse como <c>CorrelationId</c> de auditoría y en el
/// <c>QueryEvent</c> de Serilog sin plumbing adicional: <see cref="Activity.Current"/>
/// fluye automáticamente (vía <c>AsyncLocal</c>/<c>ExecutionContext</c>) hacia
/// <c>RagGenerationService</c> y <c>QdrantSemanticRetriever</c>, que solo necesitan
/// llamar <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> para
/// anidarse correctamente sin conocer al llamador.
///
/// Sin ningún <c>ActivityListener</c>/exportador registrado, <c>StartActivity</c> NO
/// asigna memoria y devuelve <c>null</c> en todos los niveles — comportamiento idéntico
/// al de antes de este ítem. Un turno servido por un consumidor que no arranque el span
/// raíz (p. ej. el CLI, fuera del alcance de esta ficha) simplemente produce spans sin
/// padre común, nunca spans inventados.
/// </summary>
public static class RagEngineTracing
{
    public const string SourceName = "Rag.Context.Engine";

    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");

    /// <summary>Nombres de span de los cinco pasos que exige el ítem 13.4, más el turno raíz.</summary>
    public static class Steps
    {
        public const string Turn = "rag.turn";
        public const string Retrieval = "rag.retrieval";
        public const string Rerank = "rag.rerank";
        public const string Gate = "rag.gate";
        public const string Context = "rag.context";
        public const string Generation = "rag.generation";
    }

    /// <summary>
    /// Registra en <paramref name="activity"/> que un paso NO se ejecutó, con su motivo
    /// — nunca se crea un span de duración cero para simular un paso que no corrió.
    /// </summary>
    public static void DeclareSkipped(this Activity? activity, string step, string reason) =>
        activity?.AddEvent(new ActivityEvent($"{step}.skipped",
            tags: new ActivityTagsCollection { { "reason", reason } }));
}
