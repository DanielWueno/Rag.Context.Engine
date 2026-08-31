using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagEngine.Core.Domain;
using RagEngine.Core.Services.Generation;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 4.5 del plan — regresión permanente de las tres bandas de confianza del gate
/// (<see cref="ConfidenceGate"/>) y de la frase fugada del ítem 4.4.
///
/// Por qué existe: hasta este ítem, ni <see cref="ConfidenceGate"/> ni el addendum de
/// banda media tenían un test que instanciara el gate de verdad — <c>PromptHashesTests</c>
/// fija el hash del prompt completo, pero un hash que cambia no dice SI el motivo del
/// cambio fue justamente que la frase mala volvió a colarse (podría cambiar por
/// cualquier otro motivo legítimo, y entonces habría que reescribir el hash sin que
/// esto sirva de nada). Este test complementa a <c>PromptHashesTests</c> buscando el
/// texto en sí, no el hash: sigue detectando la fuga aunque el hash cambie por una
/// razón ajena a esta frase.
///
/// Cubre las dos regresiones que documenta
/// docs/analisis-futuro/gate-de-confianza-score-inestable-y-fuga-de-prompt.md:
///   - 4.4: la rama (a) del addendum de banda media volvía a citar literalmente la
///     frase que el modelo de 7B imitaba tal cual en producción.
///   - La calibración de 4.3 (LowConfidenceThreshold=0.05, HighConfidenceThreshold=0.60)
///     sigue determinando las tres bandas — no se tocan aquí, sólo se verifican.
///
/// La invarianza de score frente al TopK (regresión de 4.2,
/// <see cref="RagEngine.Core.Infrastructure.Reranking.CrossEncoderOptions.StableGateScore"/>)
/// vive en <see cref="CrossEncoderStableGateScoreTests"/> — ese sí necesita el modelo
/// ONNX real y este no, así que se mantienen en archivos separados.
/// </summary>
public class ConfidenceGateBandTests
{
    /// <summary>
    /// Núcleo distintivo de la frase fugada del ítem 4.4 (ver
    /// docs/analisis-futuro/gate-de-confianza-score-inestable-y-fuga-de-prompt.md,
    /// líneas ~30-32 y ~461-462). Se corta ANTES de "dice..." a propósito: la rama (a)
    /// vigente sí usa "el fragmento más cercano dice" como arranque legítimo del hedge
    /// (ver LowConfidencePrompt.Addendum), así que el string a buscar es lo que viene
    /// DESPUÉS — la continuación que hace que la frase hable del retrieval en vez del
    /// contenido, y que es exactamente lo que 4.4 prohibió citar.
    /// </summary>
    private const string NucleoFraseFugada =
        "que el contexto proporcionado no tiene una relevancia alta para la pregunta";

    private static ConfidenceGate ConstruirGate() =>
        new(new OpcionesFijas(new RagGenerationOptions()), NullLogger<ConfidenceGate>.Instance);

    private static RetrievalResult Chunk(float score) => new(
        ChunkId: "chunk-1",
        Content: "contenido de prueba para el gate de confianza",
        SimilarityScore: score,
        Metadata: new CodeChunkMetadata(
            FilePath: "/repo/docs/archivo.md",
            RelativeFilePath: "docs/archivo.md",
            Language: SourceLanguage.Markdown,
            Namespace: null,
            ClassName: null,
            MethodName: null,
            StartLine: 1,
            EndLine: 5,
            LastModified: DateTimeOffset.UtcNow,
            RepositoryName: "repo-prueba"),
        ContentHash: "hash-de-prueba");

    /// <summary>
    /// Barre las tres bandas: alta (sin addendum), media (con addendum) y sin-grounding
    /// (score bajo el umbral bajo). Los umbrales (0.05 / 0.60) son los mismos que fijó
    /// 4.3 — si alguien los mueve por error, este test también lo detecta.
    /// </summary>
    public static TheoryData<float, bool, bool> Bandas => new()
    {
        // score,   esperaGrounding, esperaAddendum
        { 0.90f,    true,            false }, // banda alta
        { 0.30f,    true,            true  }, // banda media
        { 0.01f,    false,           false }, // sin grounding
    };

    [Theory]
    [MemberData(nameof(Bandas))]
    public void LasTresBandasDelGateDevuelvenElVeredictoEsperado(
        float score, bool esperaGrounding, bool esperaAddendum)
    {
        var gate = ConstruirGate();

        var resultado = gate.Assess(
            new[] { Chunk(score) },
            useReRanking: true,
            minimumScore: 0.10f,
            query: "pregunta de prueba");

        Assert.Equal(esperaGrounding, resultado.HasGrounding);
        Assert.Equal(esperaAddendum, resultado.ConfidenceAddendum is not null);
    }

    [Fact]
    public void CeroChunks_SiempreCaeEnSinGrounding()
    {
        var gate = ConstruirGate();

        var resultado = gate.Assess(
            Array.Empty<RetrievalResult>(),
            useReRanking: true,
            minimumScore: 0.10f,
            query: "pregunta de prueba");

        Assert.False(resultado.HasGrounding);
        Assert.Null(resultado.ConfidenceAddendum);
    }

    /// <summary>
    /// Sin rerank el score es RRF (función del ranking, no similitud) y el gate no lo
    /// evalúa contra los umbrales — ver el comentario de <see cref="ConfidenceGate.Assess"/>.
    /// Un score "bajo" en esta escala no debe tumbar el grounding.
    /// </summary>
    [Fact]
    public void SinRerank_NoAplicaElUmbralDeScoreAunqueElNumeroSeaBajo()
    {
        var gate = ConstruirGate();

        var resultado = gate.Assess(
            new[] { Chunk(0.001f) },
            useReRanking: false,
            minimumScore: 0.10f,
            query: "pregunta de prueba");

        Assert.True(resultado.HasGrounding);
        Assert.Null(resultado.ConfidenceAddendum);
    }

    /// <summary>
    /// El test central del ítem 4.5: la banda media debe seguir sin citar la frase que
    /// 4.4 encontró que qwen2.5-coder copiaba tal cual (8 de 22 consultas de banda
    /// media, docs/eval/quality/4.4-antes-media.json). Si el fix de 4.4 se revierte
    /// (la rama (b) del addendum vuelve a citar la frase en vez de describir la forma
    /// prohibida), este assert falla.
    ///
    /// Prueba de mutación ejecutada manualmente (no queda en el código): al reinsertar
    /// temporalmente la frase fugada completa en <c>LowConfidencePrompt.Addendum</c>,
    /// este test falla con el mensaje de abajo. Ver el resumen final de la tarea.
    /// </summary>
    [Fact]
    public void BandaMedia_NoContieneLaFraseFugadaDelItem44()
    {
        var gate = ConstruirGate();

        var resultado = gate.Assess(
            new[] { Chunk(0.30f) },
            useReRanking: true,
            minimumScore: 0.10f,
            query: "pregunta ambigua de banda media");

        Assert.True(resultado.HasGrounding);
        Assert.NotNull(resultado.ConfidenceAddendum);

        // El addendum es un raw string literal envuelto a mano; una regresión real
        // podría reintroducir la frase con saltos de línea en puntos distintos a los
        // de este comentario. Se normalizan los espacios en blanco (incl. saltos de
        // línea) a uno solo antes de buscar, para no depender del wrapping exacto.
        var addendumSinSaltos = Regex.Replace(resultado.ConfidenceAddendum, @"\s+", " ");

        Assert.DoesNotContain(
            NucleoFraseFugada,
            addendumSinSaltos,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see cref="IOptionsMonitor{TOptions}"/> mínimo para un valor fijo — no hay
    /// necesidad de recargar config en un test, sólo de satisfacer el constructor de
    /// <see cref="ConfidenceGate"/>, que depende del monitor (no de <c>IOptions</c>)
    /// porque los umbrales deben poder cambiar sin reiniciar el host.
    /// </summary>
    private sealed class OpcionesFijas(RagGenerationOptions valor) : IOptionsMonitor<RagGenerationOptions>
    {
        public RagGenerationOptions CurrentValue { get; } = valor;

        public RagGenerationOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<RagGenerationOptions, string?> listener) => null;
    }
}
