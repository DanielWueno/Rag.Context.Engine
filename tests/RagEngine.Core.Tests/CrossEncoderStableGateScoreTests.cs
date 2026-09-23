using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Reranking;
using RagEngine.Core.Utilities;
using Xunit;
using Xunit.Sdk;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 4.5 del plan — regresión permanente de la invarianza de score frente al TopK
/// (ítem 4.2, <see cref="CrossEncoderOptions.StableGateScore"/>).
///
/// Por qué existe: antes de 4.2, el score del ganador #1 dependía de sus vecinos de
/// lote — y como el pool de rerank es 3×TopK, el MISMO par (query, chunk) puntuaba
/// distinto según el TopK pedido. El gate de confianza lee justamente ese número
/// (<c>chunks[0].SimilarityScore</c>) contra un umbral absoluto, así que un score que
/// se mueve con un parámetro de la petición (no con el contenido) rompe la calibración
/// de 4.3. Ver docs/analisis-futuro/gate-de-confianza-score-inestable-y-fuga-de-prompt.md.
///
/// Este test SÍ carga el modelo ONNX real del cross-encoder — es la única forma de
/// probar la regresión de verdad, no una simulación — pero NO necesita Qdrant ni
/// Ollama: construye directamente dos lotes de <see cref="RetrievalResult"/> sintéticos
/// de tamaño distinto y llama a <see cref="OnnxCrossEncoderReRanker.ReRankAsync"/> sobre
/// cada uno.
///
/// Si el modelo no está disponible en disco (p. ej. un runner de CI sin
/// infra/download-model.sh corrido, ver ítem 1.7), el test se SALTA explícitamente en
/// vez de fallar en rojo por ausencia de infraestructura — ver
/// <see cref="OnnxModeloDisponibleFactAttribute"/>.
/// </summary>
public class CrossEncoderStableGateScoreTests
{
    private const string Query = "¿Cómo se cierra un ticket de soporte?";

    private const string ContenidoGanador =
        "Para cerrar un ticket de soporte, el agente debe marcarlo como resuelto en el " +
        "sistema, registrar la causa raíz y notificar al cliente antes de archivarlo.";

    /// <summary>
    /// Ruta al modelo tal como lo configura appsettings.json en producción
    /// (src/RagEngine.Cli/appsettings.json, src/RagEngine.Api/appsettings.json):
    /// el binario int8 <c>_qint8_arm64</c>, NO el default en fp32 de
    /// <see cref="CrossEncoderOptions.ModelPath"/>. Importa para esta prueba en
    /// concreto: la sensibilidad al padding dinámico que 4.2 corrige es del
    /// cuantizado — contra el modelo fp32 por defecto no se observó (verificado
    /// manualmente: con <c>StableGateScore=false</c> sobre fp32 el score no se movía
    /// ni siquiera con lotes de longitud muy dispar). <see cref="RagEnginePaths.SelectArchitectureBinary"/>
    /// cae sola al binario genérico fuera de arm64.
    /// </summary>
    private const string ModelPathProduccion =
        "models/mmarco-mMiniLMv2-L12-H384-v1/model_qint8_arm64.onnx";

    private static CrossEncoderOptions OpcionesModeloReal(bool stableGateScore) => new()
    {
        ModelPath = RagEnginePaths.ResolveModelPath(ModelPathProduccion),
        VocabPath = RagEnginePaths.ResolveModelPath(new CrossEncoderOptions().VocabPath),
        StableGateScore = stableGateScore,
    };

    private static OnnxCrossEncoderReRanker ConstruirReRanker(bool stableGateScore) =>
        new(
            Options.Create(OpcionesModeloReal(stableGateScore)),
            NullLogger<OnnxCrossEncoderReRanker>.Instance);

    private static RetrievalResult Candidato(string id, string contenido) => new(
        ChunkId: id,
        Content: contenido,
        SimilarityScore: 0f, // el RRF/prefetch previo es irrelevante: ReRankAsync lo reemplaza
        ScoreScale: RetrievalScoreScale.RankFusionNative,
        Metadata: new CodeChunkMetadata(
            FilePath: $"/repo/docs/{id}.md",
            RelativeFilePath: $"docs/{id}.md",
            Language: SourceLanguage.Markdown,
            Namespace: null,
            ClassName: null,
            MethodName: null,
            StartLine: 1,
            EndLine: 3,
            LastModified: DateTimeOffset.UtcNow,
            RepositoryName: "repo-prueba"),
        ContentHash: id);

    /// <summary>
    /// Relleno deliberadamente ajeno al dominio de la query (clima, cocina, historia),
    /// para que el candidato ganador quede #1 de forma inequívoca en ambos pools —
    /// la invarianza que se prueba es la del NÚMERO, no la del orden, pero hace falta
    /// que el orden sea estable para comparar el mismo candidato en la posición #1.
    ///
    /// Los cuatro primeros son cortos a propósito: son los únicos que entran en el
    /// pool de 5 (junto al ganador), y ese pool debe quedar con secuencias parecidas
    /// en longitud entre sí. El resto (sólo presentes en el pool de 20) alternan textos
    /// cortos y largos — el reranker ordena por <c>ChunkId</c> antes de batchear
    /// (<see cref="OnnxCrossEncoderReRanker.ReRankAsync"/>), así que "ganador" cae en
    /// el primer lote de 8 junto con relleno-00..06: si alguno de esos es mucho más
    /// largo, el padding dinámico de ESE lote cambia respecto al lote único del pool
    /// de 5 — que es justo el escenario que hacía que el score batcheado (sin 4.2)
    /// dependiera del TopK.
    /// </summary>
    private static readonly string[] TemasRelleno =
    {
        "El clima en Madrid es variable en primavera, con lluvias frecuentes por la tarde.",
        "La receta de tortilla de patatas lleva huevo, patata y cebolla a fuego lento.",
        "La Revolución Industrial comenzó en Gran Bretaña a finales del siglo XVIII.",
        "El ajedrez se juega en un tablero de 64 casillas con piezas de dos colores.",
        TextoLargo("Los volcanes activos se monitorean por sismicidad, deformación del terreno y emisión de gases volcánicos."),
        TextoLargo("La fotosíntesis convierte luz solar en energía química dentro de los cloroplastos de la planta."),
        TextoLargo("El maratón olímpico mide 42,195 kilómetros desde su estandarización en los Juegos de 1921."),
        "Las mareas oceánicas responden principalmente a la atracción gravitatoria lunar.",
        "El café arábica crece mejor en altitudes elevadas con clima templado.",
        TextoLargo("La imprenta de Gutenberg permitió la reproducción masiva de textos escritos en toda Europa."),
        "Los faros marítimos usan lentes de Fresnel para proyectar luz a larga distancia.",
        "El reciclaje de aluminio consume mucha menos energía que producirlo desde cero.",
        TextoLargo("La migración estacional de las aves suele guiarse por el campo magnético terrestre y por puntos de referencia visuales."),
        "El vidrio se fabrica fundiendo arena de sílice a temperaturas muy altas.",
        "Los arrecifes de coral albergan una cuarta parte de la vida marina conocida.",
        TextoLargo("La imprenta digital abarató drásticamente el costo de publicar un libro en tirajes pequeños."),
        "El té verde se procesa sin fermentación, a diferencia del té negro.",
        "Las auroras boreales resultan de partículas solares chocando con la atmósfera.",
        "El pan de masa madre fermenta con levaduras y bacterias silvestres.",
        "La brújula usa el magnetismo terrestre para señalar aproximadamente el norte.",
    };

    /// <summary>
    /// Repite la frase hasta ~300 palabras para forzar una secuencia larga que cambie
    /// el padding dinámico del lote que la contiene — ver el comentario de
    /// <see cref="TemasRelleno"/>.
    /// </summary>
    private static string TextoLargo(string fraseBase) =>
        string.Join(" ", Enumerable.Repeat(fraseBase, 20));

    private static List<RetrievalResult> ConstruirPool(int tamano)
    {
        var pool = new List<RetrievalResult> { Candidato("ganador", ContenidoGanador) };
        for (int i = 0; i < tamano - 1; i++)
        {
            pool.Add(Candidato($"relleno-{i:00}", TemasRelleno[i % TemasRelleno.Length]));
        }

        return pool;
    }

    /// <summary>
    /// Núcleo del ítem 4.5, parte 2: el mismo par (query, chunk ganador) debe puntuar
    /// IGUAL con <c>StableGateScore=true</c> sin importar si compitió en un pool de 5 o
    /// de 20 candidatos (que además producen composiciones de lote distintas dado
    /// <see cref="CrossEncoderOptions.BatchSize"/>=8: 1 lote completo vs. 2 lotes de 8 +
    /// uno de 4). Antes de 4.2 estos dos números divergían porque el padding dinámico
    /// del lote batcheado cambiaba con el vecino más largo.
    ///
    /// Prueba de mutación ejecutada manualmente (no queda en el código): construyendo
    /// el mismo escenario con <c>StableGateScore=false</c>, el score del ganador SÍ
    /// difiere entre el pool de 5 y el de 20 sobre el modelo cuantizado real: 0.999057
    /// (pool=5) vs. 0.999258 (pool=20) — una diferencia de ~0.0002, bien por encima de
    /// la tolerancia de esta prueba. Ver el resumen final de la tarea.
    /// </summary>
    [OnnxModeloDisponibleFact]
    public async Task ElScoreDelGanadorEsInvarianteAlTamanoDelPool()
    {
        var reranker = ConstruirReRanker(stableGateScore: true);

        var pool5 = ConstruirPool(5);
        var pool20 = ConstruirPool(20);

        var resultado5 = await reranker.ReRankAsync(Query, pool5, topK: 5);
        var resultado20 = await reranker.ReRankAsync(Query, pool20, topK: 20);

        Assert.Equal("ganador", resultado5[0].ChunkId);
        Assert.Equal("ganador", resultado20[0].ChunkId);

        Assert.True(
            Math.Abs(resultado5[0].SimilarityScore - resultado20[0].SimilarityScore) < 0.0001f,
            "El score del chunk #1 varió según el tamaño del pool "
          + $"(pool=5 → {resultado5[0].SimilarityScore:F6}, pool=20 → {resultado20[0].SimilarityScore:F6}). "
          + "Eso es exactamente la regresión de 4.2: revisa CrossEncoderOptions.StableGateScore y el "
          + "re-scoring en lote de tamaño 1 de OnnxCrossEncoderReRanker.ReRankAsync.");
    }

    [OnnxModeloDisponibleFact]
    public async Task GateCalibration_identity_describes_loaded_bytes_not_a_replaced_file()
    {
        var options = OpcionesModeloReal(stableGateScore: true);
        var directory = Path.Combine(Path.GetTempPath(), $"rag-gate-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, Path.GetFileName(options.ModelPath));
        try
        {
            File.Copy(options.ModelPath, copy);
            using var reranker = new OnnxCrossEncoderReRanker(
                Options.Create(options with { ModelPath = copy }),
                NullLogger<OnnxCrossEncoderReRanker>.Instance);
            var result = await reranker.ReRankAsync(Query, ConstruirPool(5), 5);
            var identity = Assert.IsType<CrossEncoderIdentity>(result[0].CrossEncoder);
            Assert.Equal(await ContentHasher.ComputeFileAsync(copy), identity.ModelSha256);
            Assert.Equal(await ContentHasher.ComputeFileAsync(options.VocabPath), identity.TokenizerSha256);
            Assert.Equal(Path.GetFileName(copy), identity.Binary);
            Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                identity.Architecture);
            Assert.True(identity.StableGateScore);
            Assert.All(result, r => Assert.Equal(identity, r.CrossEncoder));
            if (Environment.GetEnvironmentVariable("RAG_GATE_IDENTITY_REPORT") is { Length: > 0 } reportPath)
                await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(identity,
                    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower }));

            // Replacing this disposable file must not relabel the already-loaded session.
            await File.WriteAllTextAsync(copy, "different binary under the same name");
            var repeated = await reranker.ReRankAsync(Query, ConstruirPool(5), 5);
            Assert.Equal(identity, repeated[0].CrossEncoder);
            Assert.Equal(result[0].SimilarityScore, repeated[0].SimilarityScore);
            var calibration = GateCalibrationTests.Calibration with { CrossEncoder = identity };
            calibration.ValidateFor(repeated[0]);
            var replaced = calibration with
            {
                CrossEncoder = identity with { ModelSha256 = await ContentHasher.ComputeFileAsync(copy) }
            };
            Assert.Throws<InvalidOperationException>(() => replaced.ValidateFor(repeated[0]));
        }
        finally
        {
            File.Delete(copy);
            Directory.Delete(directory);
        }
    }

    /// <summary>
    /// <see cref="FactAttribute"/> que se salta a sí mismo (con motivo explícito, visible
    /// como "Skipped" en el runner) cuando el modelo ONNX del cross-encoder no está en
    /// disco en este entorno — mismo espíritu que el ítem 1.7 (CI mínimo sin modelos).
    /// El chequeo corre en el constructor del atributo, que xunit invoca en tiempo de
    /// descubrimiento; asignar <see cref="FactAttribute.Skip"/> ahí sí puede depender de
    /// una comprobación en tiempo de ejecución (a diferencia de pasar <c>Skip = "..."</c>
    /// como argumento nombrado del atributo, que exige una constante de compilación).
    /// </summary>
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
}
