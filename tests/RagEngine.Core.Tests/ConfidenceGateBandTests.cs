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

    /// <summary>
    /// La escala por defecto es la del camino de producción con el gate activo: el score
    /// estable del ítem 4.2, que es sobre el que 4.3 calibró los umbrales. Desde el ítem
    /// 4.9 el gate ya no recibe un <c>useReRanking</c> aparte — decide mirando esta
    /// escala, así que aquí es el parámetro que distingue un caso de otro.
    /// </summary>
    private static RetrievalResult Chunk(
        float score,
        RetrievalScoreScale escala = RetrievalScoreScale.CrossEncoderStable) => new(
        ChunkId: "chunk-1",
        Content: "contenido de prueba para el gate de confianza",
        SimilarityScore: score,
        ScoreScale: escala,
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
            minimumScore: 0.10f,
            query: "pregunta de prueba");

        Assert.Equal(esperaGrounding, resultado.HasGrounding);
        Assert.Equal(esperaAddendum, resultado.ConfidenceAddendum is not null);
    }

    /// <summary>
    /// Ítem 4.7 del plan: <c>RagEngine.Api/Program.cs</c> reimplementaba esta regla a
    /// mano (<c>results[0].SimilarityScore &lt; ragOptions.LowConfidenceThreshold</c>)
    /// en vez de consumir <see cref="ConfidenceGate"/>, con el riesgo de que 4.2/4.3
    /// recalibraran los umbrales aquí y la copia de la API se quedara atrás en
    /// silencio. El fix hace que <c>ShouldSuppressSources</c> delegue por completo en
    /// <c>confidenceGate.Assess(...).HasGrounding</c> — no queda una segunda
    /// implementación de la comparación de score con la que "coincidir": el único
    /// camino que decide la banda es este. Este test fija ese comportamiento en el
    /// borde exacto del umbral (el punto donde una reimplementación divergiría antes
    /// que en ningún otro): justo por debajo (con <see cref="MathF.BitDecrement"/>,
    /// el float representable inmediatamente anterior), exactamente igual, y justo
    /// por encima (<see cref="MathF.BitIncrement"/>). La comparación del gate es
    /// estrictamente "menor que", así que el valor exacto del umbral YA cae dentro de
    /// la banda media (con grounding y con hedge), no en sin-grounding — si alguien
    /// cambiara ese operador a "&lt;=" sin querer, este test lo detecta.
    /// </summary>
    [Fact]
    public void FronteraExactaDelUmbralBajo_MarcaLaBandaEsperada()
    {
        var opciones = new RagGenerationOptions();
        var gate = new ConfidenceGate(new OpcionesFijas(opciones), NullLogger<ConfidenceGate>.Instance);
        var umbral = opciones.LowConfidenceThreshold;

        var justoDebajo = MathF.BitDecrement(umbral);
        var justoEncima = MathF.BitIncrement(umbral);

        var resultadoDebajo = gate.Assess(
            new[] { Chunk(justoDebajo) }, minimumScore: 0.10f, query: "q");
        var resultadoIgual = gate.Assess(
            new[] { Chunk(umbral) }, minimumScore: 0.10f, query: "q");
        var resultadoEncima = gate.Assess(
            new[] { Chunk(justoEncima) }, minimumScore: 0.10f, query: "q");

        Assert.False(resultadoDebajo.HasGrounding);
        Assert.True(resultadoIgual.HasGrounding);
        Assert.True(resultadoEncima.HasGrounding);

        // Ambos caen en banda media (por debajo de HighConfidenceThreshold): con
        // grounding pero con el addendum de baja confianza.
        Assert.NotNull(resultadoIgual.ConfidenceAddendum);
        Assert.NotNull(resultadoEncima.ConfidenceAddendum);
    }

    [Fact]
    public void CeroChunks_SiempreCaeEnSinGrounding()
    {
        var gate = ConstruirGate();

        var resultado = gate.Assess(
            Array.Empty<RetrievalResult>(),
            minimumScore: 0.10f,
            query: "pregunta de prueba");

        Assert.False(resultado.HasGrounding);
        Assert.Null(resultado.ConfidenceAddendum);
    }

    /// <summary>
    /// Sin rerank el score es RRF (función del ranking, no similitud) y el gate no lo
    /// evalúa contra los umbrales — ver el comentario de <see cref="ConfidenceGate.Assess"/>.
    /// Un score "bajo" en esta escala no debe tumbar el grounding.
    ///
    /// <para>Desde el ítem 4.9 la condición se lee del propio resultado
    /// (<see cref="RetrievalResult.ScoreScale"/>) y no de un booleano que el llamador
    /// tenía que pasar bien. Entran las dos escalas de fusión: el camino nativo y el
    /// ponderado producen magnitudes distintas y ninguna es comparable contra un umbral
    /// absoluto.</para>
    /// </summary>
    [Theory]
    [InlineData(RetrievalScoreScale.RankFusionNative)]
    [InlineData(RetrievalScoreScale.RankFusionWeighted)]
    public void ScoreDeFusion_NoAplicaElUmbralAunqueElNumeroSeaBajo(RetrievalScoreScale escala)
    {
        var gate = ConstruirGate();

        var resultado = gate.Assess(
            new[] { Chunk(0.001f, escala) },
            minimumScore: 0.10f,
            query: "pregunta de prueba");

        Assert.True(resultado.HasGrounding);
        Assert.Null(resultado.ConfidenceAddendum);
    }

    /// <summary>
    /// La cara opuesta del test anterior, y la razón de ser del ítem 4.9: el mismo número
    /// bajo, en una escala que SÍ es comparable entre consultas, tiene que tumbar el
    /// grounding. Antes eso dependía de que el llamador acertara con <c>useReRanking</c>
    /// desde otra capa; ahora lo decide el dato. Cubre las dos escalas del cross-encoder
    /// porque ambas son sigmoides absolutas — la batcheada es la de la cola de la lista,
    /// y un consumidor que mire un chunk que no sea el #0 debe obtener el mismo veredicto.
    /// </summary>
    [Theory]
    [InlineData(RetrievalScoreScale.CrossEncoderStable)]
    [InlineData(RetrievalScoreScale.CrossEncoderBatched)]
    public void ScoreDeCrossEncoder_SiAplicaElUmbralYTumbaElGrounding(RetrievalScoreScale escala)
    {
        var gate = ConstruirGate();

        var resultado = gate.Assess(
            new[] { Chunk(0.001f, escala) },
            minimumScore: 0.10f,
            query: "pregunta de prueba");

        Assert.False(resultado.HasGrounding);
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
