using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagEngine.Core.Domain;
using RagEngine.Core.Services.Generation;
using RagEngine.Core.Infrastructure.Summary;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 4.8 del plan — parte (b): <see cref="GenerationContextAssembler.BuildAsync"/>
/// no tenía ningún test que cubriera la rama de <c>ResolveSimpleModeContextChunksAsync</c>
/// (el intercambio de contenido crudo por resumen de negocio en
/// <see cref="ResponseMode.Simple"/>). <see cref="GenerationContextGoldenTests"/> fija
/// <c>BuildContextBlock</c> y <c>SystemPromptComposer.SelectTemplate</c> byte a byte,
/// pero siempre con chunks ya resueltos — nunca ejercita la consulta a
/// <see cref="SummaryCache"/> ni la lógica de cobertura/umbral que decide si un chunk
/// sin resumen se excluye o se degrada a su contenido crudo.
///
/// <see cref="SummaryCache"/> es sealed (no interfaz) — se usa una instancia real
/// contra un SQLite temporal, mismo patrón de <c>SummaryCacheTests</c>.
/// </summary>
public class GenerationContextAssemblerResumenSwapTests : IDisposable
{
    private readonly string _rutaTemporal =
        Path.Combine(Path.GetTempPath(), $"ragengine-assembler-test-{Guid.NewGuid():N}.sqlite3");

    public void Dispose()
    {
        foreach (var sufijo in new[] { "", "-wal", "-shm" })
        {
            var archivo = _rutaTemporal + sufijo;
            if (File.Exists(archivo)) File.Delete(archivo);
        }
    }

    private SummaryCache AbrirCache() => SummaryCache.Open(_rutaTemporal, "v1");

    private static GenerationContextAssembler ConstruirAssembler(SummaryCache cache, RagGenerationOptions opciones) =>
        new(cache, new OpcionesFijas(opciones), NullLogger<GenerationContextAssembler>.Instance);

    /// <summary>
    /// Copiado de <c>ConfidenceGateBandTests.Chunk</c>, adaptado para variar
    /// <c>Content</c> y <c>ContentHash</c> por caso — son justo los dos campos que
    /// <c>ResolveSimpleModeContextChunksAsync</c> lee (hash para la consulta a la
    /// caché, content como candidato a ser reemplazado).
    /// </summary>
    private static RetrievalResult Chunk(string content, string contentHash) => new(
        ChunkId: $"chunk-{contentHash}",
        Content: content,
        SimilarityScore: 0.9f,
        ScoreScale: RetrievalScoreScale.CrossEncoderStable,
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
        ContentHash: contentHash);

    /// <summary>
    /// Caso 1: flag apagado (<c>EnableSimpleModeResumenContext = false</c>) — el bloque
    /// final debe conservar el contenido CRUDO aunque exista un resumen cacheado
    /// distinto para ese hash. Prueba indirecta de "no toca SummaryCache en absoluto":
    /// si la rama de resumen se ejecutara igual, el bloque contendría el texto del
    /// resumen sembrado, no el crudo.
    ///
    /// Mutación que este test detecta: borrar la condición
    /// <c>!Options.EnableSimpleModeResumenContext</c> (o invertirla) en
    /// <c>ResolveContextChunksAsync</c> — el bloque pasaría a contener el resumen.
    /// </summary>
    [Fact]
    public async Task FlagApagado_DejaElContenidoCrudoIntacto()
    {
        var cache = AbrirCache();
        await cache.SetAsync("hash-1", "RESUMEN-QUE-NO-DEBE-APARECER");

        var opciones = new RagGenerationOptions { EnableSimpleModeResumenContext = false };
        var assembler = ConstruirAssembler(cache, opciones);

        var bloque = await assembler.BuildAsync(
            new[] { Chunk("contenido crudo original", "hash-1") },
            ResponseMode.Simple,
            CancellationToken.None);

        Assert.Contains("contenido crudo original", bloque);
        Assert.DoesNotContain("RESUMEN-QUE-NO-DEBE-APARECER", bloque);
    }

    /// <summary>
    /// Caso 1 (variante): <see cref="ResponseMode.Technical"/> tampoco activa el swap,
    /// aunque el flag esté en <c>true</c> — es la otra mitad del <c>||</c> corto-circuito
    /// en <c>ResolveContextChunksAsync</c>.
    /// </summary>
    [Fact]
    public async Task ModoTechnical_DejaElContenidoCrudoIntactoAunqueElFlagEsteEncendido()
    {
        var cache = AbrirCache();
        await cache.SetAsync("hash-1", "RESUMEN-QUE-NO-DEBE-APARECER");

        var opciones = new RagGenerationOptions { EnableSimpleModeResumenContext = true };
        var assembler = ConstruirAssembler(cache, opciones);

        var bloque = await assembler.BuildAsync(
            new[] { Chunk("contenido crudo original", "hash-1") },
            ResponseMode.Technical,
            CancellationToken.None);

        Assert.Contains("contenido crudo original", bloque);
        Assert.DoesNotContain("RESUMEN-QUE-NO-DEBE-APARECER", bloque);
    }

    /// <summary>
    /// Caso 2: guarda <c>coveredCount &gt; 0</c>. Con umbral mal configurado a 0 y CERO
    /// chunks cubiertos, <c>0 &gt;= 0</c> sería verdadero sin la guarda — vaciaría el
    /// contexto entero. La guarda debe impedirlo: los chunks sin resumen se conservan
    /// crudos.
    ///
    /// Mutación que este test detecta: quitar <c>coveredCount > 0 &&</c> de la
    /// expresión de <c>excludeUncovered</c> en <c>ResolveSimpleModeContextChunksAsync</c>
    /// — el bloque resultante quedaría vacío.
    /// </summary>
    [Fact]
    public async Task UmbralCeroSinCobertura_NoVaciaElContexto()
    {
        var cache = AbrirCache(); // vacía: ningún hash tiene resumen cacheado

        var opciones = new RagGenerationOptions { SimpleModeResumenCoverageThreshold = 0f };
        var assembler = ConstruirAssembler(cache, opciones);

        var bloque = await assembler.BuildAsync(
            new[]
            {
                Chunk("contenido crudo A", "hash-sin-resumen-a"),
                Chunk("contenido crudo B", "hash-sin-resumen-b"),
            },
            ResponseMode.Simple,
            CancellationToken.None);

        Assert.Contains("contenido crudo A", bloque);
        Assert.Contains("contenido crudo B", bloque);
    }

    /// <summary>
    /// Caso 3: cobertura (1/2 = 0.5) IGUAL al umbral (0.5) — la comparación es
    /// <c>&gt;=</c>, así que debe excluir al chunk no cubierto, no degradarlo.
    ///
    /// Mutación que este test detecta: cambiar <c>coverage &gt;= Options.SimpleModeResumenCoverageThreshold</c>
    /// por <c>&gt;</c> estricto en <c>ResolveSimpleModeContextChunksAsync</c> — con
    /// cobertura exactamente igual al umbral, el chunk no cubierto NO se excluiría y
    /// su contenido crudo aparecería en el bloque.
    /// </summary>
    [Fact]
    public async Task CoberturaIgualAlUmbral_ExcluyeLosNoCubiertos()
    {
        var cache = AbrirCache();
        await cache.SetAsync("hash-cubierto", "resumen de negocio del chunk cubierto");

        var opciones = new RagGenerationOptions { SimpleModeResumenCoverageThreshold = 0.5f };
        var assembler = ConstruirAssembler(cache, opciones);

        var bloque = await assembler.BuildAsync(
            new[]
            {
                Chunk("contenido crudo cubierto", "hash-cubierto"),
                Chunk("contenido crudo NO cubierto", "hash-no-cubierto"),
            },
            ResponseMode.Simple,
            CancellationToken.None);

        Assert.Contains("resumen de negocio del chunk cubierto", bloque);
        Assert.DoesNotContain("contenido crudo cubierto", bloque); // el crudo fue reemplazado por el resumen
        Assert.DoesNotContain("contenido crudo NO cubierto", bloque); // excluido del todo
    }

    /// <summary>
    /// Caso 4: cobertura (1/2 = 0.5) POR DEBAJO del umbral (0.9) — el chunk no
    /// cubierto se conserva con su contenido crudo en vez de excluirse.
    /// </summary>
    [Fact]
    public async Task CoberturaBajoElUmbral_ConservaLosNoCubiertosCrudos()
    {
        var cache = AbrirCache();
        await cache.SetAsync("hash-cubierto", "resumen de negocio del chunk cubierto");

        var opciones = new RagGenerationOptions { SimpleModeResumenCoverageThreshold = 0.9f };
        var assembler = ConstruirAssembler(cache, opciones);

        var bloque = await assembler.BuildAsync(
            new[]
            {
                Chunk("contenido crudo cubierto", "hash-cubierto"),
                Chunk("contenido crudo NO cubierto", "hash-no-cubierto"),
            },
            ResponseMode.Simple,
            CancellationToken.None);

        Assert.Contains("resumen de negocio del chunk cubierto", bloque);
        Assert.Contains("contenido crudo NO cubierto", bloque); // degradado a crudo, no excluido
    }

    /// <summary>
    /// Caso 5: el resumen sembrado lleva el prefijo típico
    /// "NombreDeEntidad: resto del resumen" que
    /// <see cref="RagEngine.Core.Utilities.SummaryTextUtilities.StripEntityPrefix"/>
    /// debe quitar antes de que el resumen entre al bloque de contexto.
    ///
    /// Mutación que este test detecta: usar el <c>summary</c> crudo de
    /// <c>TryGetAsync</c> en vez de <c>SummaryTextUtilities.StripEntityPrefix(summary)</c>
    /// en <c>ResolveSimpleModeContextChunksAsync</c> — el bloque contendría el
    /// prefijo "EntidadDePrueba:" en vez de quitarlo.
    /// </summary>
    [Fact]
    public async Task ResumenConPrefijoDeEntidad_SePublicaSinElPrefijo()
    {
        var cache = AbrirCache();
        await cache.SetAsync("hash-1", "EntidadDePrueba: el resto del resumen de negocio");

        var opciones = new RagGenerationOptions(); // default: flag on, umbral 0.5, cobertura 1/1 = 1.0
        var assembler = ConstruirAssembler(cache, opciones);

        var bloque = await assembler.BuildAsync(
            new[] { Chunk("contenido crudo cualquiera", "hash-1") },
            ResponseMode.Simple,
            CancellationToken.None);

        Assert.Contains("el resto del resumen de negocio", bloque);
        Assert.DoesNotContain("EntidadDePrueba:", bloque);
    }

    /// <summary>
    /// <see cref="IOptionsMonitor{TOptions}"/> mínimo para un valor fijo — mismo patrón
    /// de <c>ConfidenceGateBandTests.OpcionesFijas</c>; el proyecto de tests no
    /// referencia Moq.
    /// </summary>
    private sealed class OpcionesFijas(RagGenerationOptions valor) : IOptionsMonitor<RagGenerationOptions>
    {
        public RagGenerationOptions CurrentValue { get; } = valor;

        public RagGenerationOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<RagGenerationOptions, string?> listener) => null;
    }
}
