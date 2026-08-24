using RagEngine.Core.Domain;

namespace RagEngine.Core.Tests;

/// <summary>
/// Casos de entrada compartidos por los tests de ensamblado de contexto y de
/// selección de plantilla (ítem 2.2 del plan: descomponer RagGenerationService).
///
/// Existen para que la descomposición sea verificablemente byte-idéntica: la
/// salida esperada en <c>GoldenMaster/generacion-contexto.json</c> se capturó
/// ejecutando estos mismos casos contra el código PREVIO al refactor, vía
/// reflexión sobre los estáticos privados de <c>RagGenerationService</c>. Si un
/// cambio de estructura altera una sola coma del bloque de contexto o cambia qué
/// plantilla se elige, el test falla.
/// </summary>
internal static class GenerationGoldenCasos
{
    private static CodeChunkMetadata Meta(
        SourceLanguage lenguaje,
        string? ns = null,
        string? clase = null,
        string? miembro = null,
        int inicio = 1,
        int fin = 20,
        string repo = "bsuite-repo",
        string ruta = "src/Services/AuditoriaService.cs") =>
        new(
            FilePath:         "/abs/" + ruta,
            RelativeFilePath: ruta,
            Language:         lenguaje,
            Namespace:        ns,
            ClassName:        clase,
            MethodName:       miembro,
            StartLine:        inicio,
            EndLine:          fin,
            LastModified:     new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            RepositoryName:   repo);

    private static RetrievalResult Chunk(string contenido, float score, CodeChunkMetadata meta) =>
        new(ChunkId: "id-" + contenido.GetHashCode().ToString("x8"),
            Content: contenido,
            SimilarityScore: score,
            Metadata: meta,
            ContentHash: "hash-" + contenido.Length);

    private static readonly RetrievalResult CodigoCompleto = Chunk(
        "public Task<Plan> GenerarPlanAuditoria(int id) => _repo.PlanAsync(id);",
        0.9123f,
        Meta(SourceLanguage.CSharp, ns: "BSuite.Auditorias", clase: "AuditoriaService",
             miembro: "GenerarPlanAuditoria", inicio: 42, fin: 78));

    private static readonly RetrievalResult CodigoSinMetadatos = Chunk(
        "var x = 1;",
        0.4f,
        Meta(SourceLanguage.CSharp, inicio: 3, fin: 5, ruta: "src/Program.cs"));

    private static readonly RetrievalResult DocMarkdown = Chunk(
        "La auditoría se finaliza automáticamente al cerrar el último hallazgo.",
        0.7777f,
        Meta(SourceLanguage.Markdown, miembro: "US-17.2 — Finalización automática",
             inicio: 10, fin: 25, repo: "innovapp-docs", ruta: "docs/us-17.md"));

    private static readonly RetrievalResult DocTextoPlano = Chunk(
        "Nota operativa: el cierre requiere firma del auditor.",
        0.61f,
        Meta(SourceLanguage.PlainText, inicio: 1, fin: 2,
             repo: "innovapp-docs", ruta: "docs/notas.txt"));

    /// <summary>Chunk que por sí solo excede MaxContextCharacters (12 000).</summary>
    private static readonly RetrievalResult Gigante = Chunk(
        new string('A', 13_000), 0.8f,
        Meta(SourceLanguage.CSharp, clase: "Gigante", inicio: 1, fin: 900,
             ruta: "src/Gigante.cs"));

    /// <summary>Casos de <c>BuildContextBlock</c>: nombre → chunks en orden de ranking.</summary>
    internal static IReadOnlyList<(string Nombre, IReadOnlyList<RetrievalResult> Chunks)> Contexto { get; } =
    [
        ("vacio", Array.Empty<RetrievalResult>()),
        ("un-chunk-de-codigo-completo", new[] { CodigoCompleto }),
        ("codigo-sin-namespace-ni-clase", new[] { CodigoSinMetadatos }),
        ("markdown-usa-etiqueta-section", new[] { DocMarkdown }),
        ("mezcla-ordenada", new[] { CodigoCompleto, DocMarkdown, CodigoSinMetadatos, DocTextoPlano }),
        // El guardia de presupuesto salta el gigante y SIGUE con los siguientes:
        // un chunk enorme a mitad del ranking no debe truncar la cola.
        ("gigante-en-medio-no-trunca-la-cola", new[] { CodigoCompleto, Gigante, DocMarkdown }),
    ];

    /// <summary>Casos de <c>SelectSystemPromptTemplate</c>.</summary>
    internal static IReadOnlyList<(string Nombre, IReadOnlyList<RetrievalResult> Chunks, ResponseMode Modo)> Seleccion { get; } =
    [
        ("simple-gana-aunque-sea-todo-codigo", new[] { CodigoCompleto, CodigoSinMetadatos }, ResponseMode.Simple),
        ("simple-gana-aunque-sea-todo-docs",   new[] { DocMarkdown, DocTextoPlano },         ResponseMode.Simple),
        ("tecnico-mayoria-codigo",             new[] { CodigoCompleto, CodigoSinMetadatos, DocMarkdown }, ResponseMode.Technical),
        // Empate exacto: la regla es docChunks * 2 >= total, así que el empate va a docs.
        ("tecnico-empate-va-a-docs",           new[] { CodigoCompleto, DocMarkdown },        ResponseMode.Technical),
        ("tecnico-todo-docs",                  new[] { DocMarkdown, DocTextoPlano },         ResponseMode.Technical),
        ("tecnico-todo-codigo",                new[] { CodigoCompleto, CodigoSinMetadatos }, ResponseMode.Technical),
    ];
}
