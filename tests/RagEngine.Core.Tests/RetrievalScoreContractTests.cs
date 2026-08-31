using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Reranking;
using RagEngine.Core.Services.Generation;
using RagEngine.Core.Services.Summary;
using RagEngine.Core.Utilities;
using Xunit;
using Xunit.Sdk;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 4.9 del plan — el contrato del score.
///
/// <see cref="RetrievalResult.SimilarityScore"/> transporta números de escalas distintas
/// según el camino que lo produjo, y desde este ítem el tipo lo declara en
/// <see cref="RetrievalResult.ScoreScale"/>. Lo que se fija aquí es la consecuencia
/// menos evidente de esa declaración, y la que más caro sale romper: <b>tras la
/// re-puntuación de la posición #0 que introdujo el ítem 4.2, la lista devuelta por el
/// reranker NO está ordenada monótonamente por score</b>, y ningún consumidor la
/// reordena.
///
/// La trampa que esto cubre: un consumidor que "normalice" con un
/// <c>OrderByDescending(r =&gt; r.SimilarityScore)</c> —un gesto que parece inocuo y hasta
/// correcto— revierte 4.2 sin tocar su código, porque devuelve a la posición #0 un chunk
/// cuyo score volvió a depender del tamaño del lote y, por tanto, del TopK de la
/// petición. Las bandas que 4.3 calibró se mueven en silencio.
/// </summary>
public class RetrievalScoreContractTests : IDisposable
{
    private readonly string _rutaTemporal =
        Path.Combine(Path.GetTempPath(), $"ragengine-score-contract-{Guid.NewGuid():N}.sqlite3");

    public void Dispose()
    {
        foreach (var sufijo in new[] { "", "-wal", "-shm" })
        {
            var archivo = _rutaTemporal + sufijo;
            if (File.Exists(archivo)) File.Delete(archivo);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Parte 1 — el invariante, sin modelo: la lista no monótona atraviesa a los
    //  consumidores reales sin que ninguno la reordene.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reproduce la forma EXACTA que deja <c>OnnxCrossEncoderReRanker.ReRankAsync</c> con
    /// <c>StableGateScore</c> encendido: la posición #0 lleva el score estable (lote de 1)
    /// y el resto el score por lotes, y el #0 puntúa POR DEBAJO del #1. Las escalas lo
    /// dicen; los números lo demuestran.
    /// </summary>
    private static IReadOnlyList<RetrievalResult> ListaNoMonotona() => new[]
    {
        Chunk("ganador", "el chunk que el ranking por lotes dejó primero", "hash-ganador",
              score: 0.42f, RetrievalScoreScale.CrossEncoderStable),
        Chunk("segundo", "el chunk que quedó segundo en el ranking por lotes", "hash-segundo",
              score: 0.95f, RetrievalScoreScale.CrossEncoderBatched),
        Chunk("tercero", "el chunk que quedó tercero en el ranking por lotes", "hash-tercero",
              score: 0.10f, RetrievalScoreScale.CrossEncoderBatched),
    };

    private static RetrievalResult Chunk(
        string id, string contenido, string contentHash, float score, RetrievalScoreScale escala) => new(
        ChunkId: id,
        Content: contenido,
        SimilarityScore: score,
        ScoreScale: escala,
        Metadata: new CodeChunkMetadata(
            FilePath: $"/repo/docs/{id}.md",
            RelativeFilePath: $"docs/{id}.md",
            Language: SourceLanguage.Markdown,
            Namespace: null,
            ClassName: null,
            MethodName: null,
            StartLine: 1,
            EndLine: 5,
            LastModified: DateTimeOffset.UtcNow,
            RepositoryName: "repo-prueba"),
        ContentHash: contentHash);

    /// <summary>
    /// El escenario de riesgo, no el benigno: el gate debe puntuar la banda con el score
    /// del #0 (0.42 → banda media, con hedge) y NO con el máximo de la lista (0.95 →
    /// banda alta, sin hedge). Un consumidor que ordenara por score antes de llamar al
    /// gate produciría justo el segundo veredicto.
    ///
    /// Con los umbrales vigentes de 4.3 (0.05 / 0.60) los dos números caen en bandas
    /// distintas a propósito: el assert distingue "leyó el #0" de "leyó el mayor".
    /// </summary>
    [Fact]
    public void ConfidenceGate_PuntuaLaBandaConElChunkCeroYNoConElScoreMasAlto()
    {
        var opciones = new RagGenerationOptions();
        var gate = new ConfidenceGate(
            new OpcionesFijas(opciones), NullLogger<ConfidenceGate>.Instance);

        var lista = ListaNoMonotona();

        // Precondición del escenario: sin esto el test pasaría por accidente.
        Assert.True(lista[0].SimilarityScore < lista[1].SimilarityScore);
        Assert.True(lista[0].SimilarityScore >= opciones.LowConfidenceThreshold);
        Assert.True(lista[0].SimilarityScore < opciones.HighConfidenceThreshold);
        Assert.True(lista[1].SimilarityScore >= opciones.HighConfidenceThreshold);

        var veredicto = gate.Assess(lista, minimumScore: 0.10f, query: "pregunta de prueba");

        Assert.True(veredicto.HasGrounding);
        Assert.NotNull(veredicto.ConfidenceAddendum); // banda media: la del #0, no la del 0.95
    }

    /// <summary>
    /// El otro consumidor del orden: el bloque de contexto que se inyecta en el prompt.
    /// <c>[Chunk #1]</c> tiene que ser el <c>ChunkId</c> "ganador" —el que el ranking por
    /// lotes dejó primero— aunque su score sea el más bajo de los tres. El LLM lee ese
    /// bloque de arriba abajo; reordenarlo cambia qué fragmento se presenta como el más
    /// relevante.
    /// </summary>
    [Fact]
    public async Task GenerationContextAssembler_PreservaElOrdenRecibido()
    {
        var cache = SummaryCache.Open(_rutaTemporal, "v1");
        var assembler = new GenerationContextAssembler(
            cache,
            new OpcionesFijas(new RagGenerationOptions()),
            NullLogger<GenerationContextAssembler>.Instance);

        var lista = ListaNoMonotona();

        var bloque = await assembler.BuildAsync(
            lista, ResponseMode.Technical, CancellationToken.None);

        var posiciones = lista
            .Select(c => bloque.IndexOf(c.Content, StringComparison.Ordinal))
            .ToList();

        Assert.All(posiciones, p => Assert.True(p >= 0, "Falta un chunk en el bloque de contexto."));
        Assert.Equal(posiciones.OrderBy(p => p).ToList(), posiciones);

        // Y el encabezado del primero es literalmente "Chunk #1", con su score bajo.
        Assert.Contains("[Chunk #1 | Score: 0.42]", bloque);
    }

    /// <summary>
    /// La declaración que hace útil todo lo anterior: la escala dice si el número puede
    /// compararse contra un umbral absoluto. Es lo que consulta
    /// <see cref="ConfidenceGate"/> en vez del antiguo parámetro <c>useReRanking</c>, y
    /// lo que separa un score de fusión (función del rango) de una sigmoide del
    /// cross-encoder.
    /// </summary>
    [Theory]
    [InlineData(RetrievalScoreScale.CosineSimilarity,    true)]
    [InlineData(RetrievalScoreScale.CrossEncoderBatched, true)]
    [InlineData(RetrievalScoreScale.CrossEncoderStable,  true)]
    [InlineData(RetrievalScoreScale.RankFusionNative,    false)]
    [InlineData(RetrievalScoreScale.RankFusionWeighted,  false)]
    public void LaEscalaDeclaraSiElScoreEsComparableEntreConsultas(
        RetrievalScoreScale escala, bool esperado)
    {
        Assert.Equal(esperado, escala.IsComparableAcrossQueries());
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Parte 2 — el invariante contra el reranker real: que el escenario de la
    //  parte 1 no sea hipotético.
    // ─────────────────────────────────────────────────────────────────────────

    private const string QueryReal = "¿Cómo se cierra un ticket de soporte?";

    private const string ContenidoGanador =
        "Para cerrar un ticket de soporte, el agente debe marcarlo como resuelto en el " +
        "sistema, registrar la causa raíz y notificar al cliente antes de archivarlo.";

    /// <summary>Ver <c>CrossEncoderStableGateScoreTests.ModelPathProduccion</c>.</summary>
    private const string ModelPathProduccion =
        "models/mmarco-mMiniLMv2-L12-H384-v1/model_qint8_arm64.onnx";

    private static OnnxCrossEncoderReRanker ConstruirReRanker(bool stableGateScore) =>
        new(
            Options.Create(new CrossEncoderOptions
            {
                ModelPath = RagEnginePaths.ResolveModelPath(ModelPathProduccion),
                VocabPath = RagEnginePaths.ResolveModelPath(new CrossEncoderOptions().VocabPath),
                StableGateScore = stableGateScore,
            }),
            NullLogger<OnnxCrossEncoderReRanker>.Instance);

    private static string TextoLargo(string fraseBase, int repeticiones = 20) =>
        string.Join(" ", Enumerable.Repeat(fraseBase, repeticiones));

    /// <summary>
    /// Pool con relleno largo, para que el padding dinámico del lote sea grande: es la
    /// diferencia entre el score por lotes del ganador y su score en lote de 1, que es
    /// justo la magnitud que 4.2 estabiliza.
    /// </summary>
    private static List<RetrievalResult> PoolReal()
    {
        var pool = new List<RetrievalResult>
        {
            Chunk("a-ganador", ContenidoGanador, "h-a", 0f, RetrievalScoreScale.RankFusionNative),
        };

        var relleno = new[]
        {
            TextoLargo("Los volcanes activos se monitorean por sismicidad, deformación del terreno y emisión de gases volcánicos."),
            TextoLargo("La fotosíntesis convierte luz solar en energía química dentro de los cloroplastos de la planta."),
            TextoLargo("El maratón olímpico mide 42,195 kilómetros desde su estandarización en los Juegos de 1921."),
            TextoLargo("La imprenta de Gutenberg permitió la reproducción masiva de textos escritos en toda Europa."),
            "Las mareas oceánicas responden principalmente a la atracción gravitatoria lunar.",
            "El café arábica crece mejor en altitudes elevadas con clima templado.",
            "El vidrio se fabrica fundiendo arena de sílice a temperaturas muy altas.",
        };

        for (int i = 0; i < relleno.Length; i++)
        {
            pool.Add(Chunk($"b-relleno-{i:00}", relleno[i], $"h-b{i}", 0f,
                           RetrievalScoreScale.RankFusionNative));
        }

        return pool;
    }

    /// <summary>
    /// Contra el modelo cuantizado real: encender <c>StableGateScore</c> cambia el NÚMERO
    /// de la posición #0 pero no el ORDEN de la lista, y deja las escalas declaradas
    /// (estable en el #0, por lotes en la cola). Es el invariante del ítem 4.2 expresado
    /// como comportamiento observable: el ranking lo decide la pasada por lotes, la
    /// re-puntuación sólo sustituye el número que va a leer el gate.
    ///
    /// Se salta si el modelo no está en disco, igual que
    /// <see cref="CrossEncoderStableGateScoreTests"/> (ítem 1.7, CI sin modelos).
    /// </summary>
    [OnnxModeloDisponibleFact]
    public async Task ReRank_LaRepuntuacionDelCeroCambiaElScorePeroNoElOrden()
    {
        var sinEstable = await ConstruirReRanker(stableGateScore: false)
            .ReRankAsync(QueryReal, PoolReal(), topK: 8);
        var conEstable = await ConstruirReRanker(stableGateScore: true)
            .ReRankAsync(QueryReal, PoolReal(), topK: 8);

        Assert.Equal(
            sinEstable.Select(r => r.ChunkId).ToList(),
            conEstable.Select(r => r.ChunkId).ToList());

        Assert.Equal("a-ganador", conEstable[0].ChunkId);

        // Las escalas declaran de dónde sale cada número.
        Assert.Equal(RetrievalScoreScale.CrossEncoderStable, conEstable[0].ScoreScale);
        Assert.All(conEstable.Skip(1),
            r => Assert.Equal(RetrievalScoreScale.CrossEncoderBatched, r.ScoreScale));
        Assert.All(sinEstable,
            r => Assert.Equal(RetrievalScoreScale.CrossEncoderBatched, r.ScoreScale));

        // Y el número del #0 sí cambió: si no, esta prueba no estaría ejercitando nada.
        Assert.NotEqual(sinEstable[0].SimilarityScore, conEstable[0].SimilarityScore);
    }

    /// <summary>
    /// Contenido repetido a propósito en dos candidatos con ids consecutivos: el reranker
    /// ordena por <c>ChunkId</c> antes de batchear, así que <c>a-gem1</c> y <c>a-gem2</c>
    /// caen en el MISMO lote (BatchSize=8, y los seis rellenos <c>b-*</c> completan ese
    /// lote). Al compartir lote comparten padding, y al ser el mismo texto obtienen el
    /// MISMO score batcheado: un empate exacto en cabeza.
    /// </summary>
    private const string ContenidoGemelo =
        "El inventario del almacén se revisa periódicamente por el personal.";

    private const string QueryGemelos =
        "¿Cuál es el procedimiento de auditoría de inventarios en almacén?";

    private static List<RetrievalResult> PoolConEmpateEnCabeza()
    {
        var pool = new List<RetrievalResult>
        {
            Chunk("a-gem1", ContenidoGemelo, "h-gem1", 0f, RetrievalScoreScale.RankFusionNative),
            Chunk("a-gem2", ContenidoGemelo, "h-gem2", 0f, RetrievalScoreScale.RankFusionNative),
        };

        // El relleno de este pool está fijado por MEDICIÓN, no por gusto: la magnitud del
        // padding del lote decide el SIGNO del desplazamiento entre el score batcheado y
        // el de lote de 1. Con estas mismas frases repetidas 20 veces el desplazamiento
        // sale al revés (el #0 SUBE, 0.5424850 → 0.5446229) y el escenario desaparece;
        // con 50 el #0 BAJA (0.6393979 → 0.5446229). Cambiar estos textos o el número de
        // repeticiones invalida la construcción.
        const int Repeticiones = 50;
        var relleno = new[]
        {
            TextoLargo("Los volcanes activos se monitorean por sismicidad.", Repeticiones),
            TextoLargo("La fotosíntesis ocurre en los cloroplastos.", Repeticiones),
            TextoLargo("El maratón mide 42,195 kilómetros.", Repeticiones),
            TextoLargo("La imprenta permitió reproducir textos.", Repeticiones),
            "Las mareas responden a la Luna.",
            "El café arábica crece en altura.",
        };

        for (int i = 0; i < relleno.Length; i++)
        {
            pool.Add(Chunk($"b-relleno-{i:00}", relleno[i], $"h-b{i}", 0f,
                           RetrievalScoreScale.RankFusionNative));
        }

        return pool;
    }

    /// <summary>
    /// El invariante del ítem 4.2 demostrado contra el modelo real, no supuesto: con un
    /// empate exacto en cabeza, la re-puntuación del #0 lo deja POR DEBAJO del #1 y aun
    /// así el #0 no se mueve. La lista devuelta no está ordenada monótonamente por score.
    ///
    /// Números medidos sobre el modelo cuantizado de producción (2026-08-31): los dos
    /// gemelos empatan batcheados en 0.6393979 y, tras el lote de 1, el #0 pasa a
    /// 0.5446229 — 0.095 por debajo del #1, una distancia enorme comparada con el ruido
    /// de cuantización, así que el escenario no es un empate frágil.
    ///
    /// La mutación que detecta: añadir un <c>OrderByDescending(r =&gt; r.SimilarityScore)</c>
    /// después de sustituir <c>reranked[0]</c> en <c>OnnxCrossEncoderReRanker.ReRankAsync</c>
    /// —o hacerlo en cualquier consumidor— sube <c>a-gem2</c> a la posición #0 y el primer
    /// assert falla. Eso es exactamente revertir 4.2: el gate volvería a leer un score
    /// batcheado, que depende del tamaño del pool y por tanto del TopK.
    /// </summary>
    [OnnxModeloDisponibleFact]
    public async Task ReRank_ConEmpateEnCabeza_DevuelveListaNoMonotonaYNoLaReordena()
    {
        var conEstable = await ConstruirReRanker(stableGateScore: true)
            .ReRankAsync(QueryGemelos, PoolConEmpateEnCabeza(), topK: 4);

        // Sigue primero el que ganó el ranking por lotes, pese a puntuar menos.
        Assert.Equal("a-gem1", conEstable[0].ChunkId);
        Assert.Equal("a-gem2", conEstable[1].ChunkId);

        Assert.True(
            conEstable[0].SimilarityScore < conEstable[1].SimilarityScore,
            "El #0 no quedó por debajo del #1, así que este test ya no está ejercitando la "
          + "lista no monótona que introduce el ítem 4.2. Revisa el pool de gemelos: si el "
          + "empate en cabeza se rompió, o el desplazamiento del lote de 1 cambió de signo, "
          + "el escenario dejó de existir y hay que reconstruirlo midiendo de nuevo — no "
          + "relajar el assert. Sospecha primero de otra arquitectura: el binario ONNX que "
          + "se carga depende de ella y el signo del desplazamiento es ruido de "
          + "cuantización, no una ley del modelo. "
          + $"(#0 {conEstable[0].SimilarityScore:F7}, #1 {conEstable[1].SimilarityScore:F7})");

        // Y el empate de partida sigue ahí: los dos gemelos batcheados eran iguales, y sólo
        // el #0 fue re-puntuado.
        var sinEstable = await ConstruirReRanker(stableGateScore: false)
            .ReRankAsync(QueryGemelos, PoolConEmpateEnCabeza(), topK: 4);

        Assert.Equal(sinEstable[0].SimilarityScore, sinEstable[1].SimilarityScore);
        Assert.Equal(sinEstable[1].SimilarityScore, conEstable[1].SimilarityScore);

        Assert.Equal(RetrievalScoreScale.CrossEncoderStable, conEstable[0].ScoreScale);
        Assert.Equal(RetrievalScoreScale.CrossEncoderBatched, conEstable[1].ScoreScale);
    }

    private sealed class OnnxModeloDisponibleFactAttribute : FactAttribute
    {
        public OnnxModeloDisponibleFactAttribute()
        {
            var modelPath = RagEnginePaths.ResolveModelPath(ModelPathProduccion);
            var vocabPath = RagEnginePaths.ResolveModelPath(new CrossEncoderOptions().VocabPath);

            if (!File.Exists(modelPath))
            {
                Skip = $"Modelo cross-encoder no encontrado en '{modelPath}'. "
                     + "Corre 'bash infra/download-model.sh reranker' para habilitar este test.";
            }
            else if (!File.Exists(vocabPath))
            {
                Skip = $"Tokenizer del cross-encoder no encontrado en '{vocabPath}'. "
                     + "Corre 'bash infra/download-model.sh reranker' para habilitar este test.";
            }
        }
    }

    /// <summary>
    /// <see cref="IOptionsMonitor{TOptions}"/> mínimo — mismo motivo que en
    /// <see cref="ConfidenceGateBandTests"/>.
    /// </summary>
    private sealed class OpcionesFijas(RagGenerationOptions valor) : IOptionsMonitor<RagGenerationOptions>
    {
        public RagGenerationOptions CurrentValue { get; } = valor;

        public RagGenerationOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<RagGenerationOptions, string?> listener) => null;
    }
}
