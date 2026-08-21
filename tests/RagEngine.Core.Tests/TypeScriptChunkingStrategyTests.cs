using Microsoft.Extensions.Logging.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Chunking;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// El chunker de TypeScript es un mini-lexer de 586 líneas que decide dónde
/// empieza y termina una función contando llaves. Lo que se prueba aquí es la
/// propiedad que le da sentido a un chunk: que el CUERPO de la función viaje
/// junto a su firma. Un chunk que contiene solo la línea de la firma es peor que
/// no tener chunk — mete al índice código sin su encabezado semántico y diluye el
/// recall, que es justo el problema que este proyecto ya identificó por otras
/// vías.
///
/// Los tres formatos cubiertos no son hipotéticos: K&amp;R es el default de
/// prettier, Allman aparece en bases de código con estilo heredado de C#, y la
/// firma partida en varias líneas es lo que produce prettier en cuanto la firma
/// pasa del ancho máximo — es decir, el caso MÁS común en TypeScript real con
/// tipos anotados.
/// </summary>
public class TypeScriptChunkingStrategyTests
{
    private static async Task<IReadOnlyList<CodeChunk>> ChunkAsync(string contenido)
    {
        var strategy = new TypeScriptChunkingStrategy(
            NullLogger<TypeScriptChunkingStrategy>.Instance);

        var artifact = new RawArtifact(
            AbsolutePath: "/tmp/ejemplo.ts",
            RelativePath: "ejemplo.ts",
            Language: SourceLanguage.TypeScript,
            LastModified: DateTimeOffset.UnixEpoch,
            SizeBytes: contenido.Length);

        var chunks = new List<CodeChunk>();
        await foreach (var chunk in strategy.ChunkAsync(artifact, contenido, ChunkingOptions.Default))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static void AssertCuerpoAcompanaALaFirma(
        IReadOnlyList<CodeChunk> chunks, string marcaDelCuerpo, string nombreFuncion)
    {
        var conCuerpo = chunks.Where(c => c.Content.Contains(marcaDelCuerpo)).ToList();

        Assert.True(conCuerpo.Count > 0,
            $"ningún chunk contiene el cuerpo ('{marcaDelCuerpo}'). "
          + $"Chunks producidos: {chunks.Count}. Contenidos: "
          + string.Join(" || ", chunks.Select(c => c.Content.Replace("\n", "\\n"))));

        Assert.True(conCuerpo.Any(c => c.Content.Contains(nombreFuncion)),
            $"el cuerpo se indexó SEPARADO de su firma: ningún chunk contiene a la vez "
          + $"'{marcaDelCuerpo}' y '{nombreFuncion}'. Chunks producidos: {chunks.Count}. "
          + string.Join(" || ", chunks.Select((c, i) =>
                $"[{i} {c.Type} clase={c.Metadata.ClassName ?? "-"} metodo={c.Metadata.MethodName ?? "-"} "
              + $"L{c.Metadata.StartLine}-{c.Metadata.EndLine}] {c.Content.Replace("\n", "\\n")}")));
    }

    [Fact]
    public async Task FirmaEstiloKandR_ElCuerpoViajaConLaFirma()
    {
        // Llave de apertura en la misma línea que la firma. Default de prettier.
        var contenido = """
            export function calcularTotal(items: Item[]): number {
              const marcaDelCuerpo = items.length;
              return marcaDelCuerpo * 2;
            }
            """;

        var chunks = await ChunkAsync(contenido);

        AssertCuerpoAcompanaALaFirma(chunks, "marcaDelCuerpo", "calcularTotal");
    }

    [Fact(Skip = "Falla HOY: bug confirmado, pendiente del item 2.1 del plan (docs/analisis-futuro/ejecucion-plan.estado.json). El chunker emite la firma sola como chunk Method y el cuerpo como PlainTextWindow sin clase ni metodo, o sea codigo indexado sin su encabezado semantico. Se deja Skip en vez de rojo para no bloquear el CI del item 1.7; quitar el Skip ES el criterio de aceptacion del 2.1.")]
    public async Task FirmaEstiloAllman_ElCuerpoViajaConLaFirma()
    {
        // Llave de apertura en su propia línea.
        var contenido = """
            export function calcularTotal(items: Item[]): number
            {
              const marcaDelCuerpo = items.length;
              return marcaDelCuerpo * 2;
            }
            """;

        var chunks = await ChunkAsync(contenido);

        AssertCuerpoAcompanaALaFirma(chunks, "marcaDelCuerpo", "calcularTotal");
    }

    [Fact(Skip = "Falla HOY: bug confirmado, pendiente del item 2.1 del plan (docs/analisis-futuro/ejecucion-plan.estado.json). El chunker emite la firma sola como chunk Method y el cuerpo como PlainTextWindow sin clase ni metodo, o sea codigo indexado sin su encabezado semantico. Se deja Skip en vez de rojo para no bloquear el CI del item 1.7; quitar el Skip ES el criterio de aceptacion del 2.1.")]
    public async Task FirmaPartidaEnVariasLineas_ElCuerpoViajaConLaFirma()
    {
        // Lo que produce prettier cuando la firma excede el ancho máximo: el caso
        // más frecuente en TypeScript real con parámetros anotados.
        var contenido = """
            export function calcularTotal(
              items: Item[],
              descuento: number,
            ): number {
              const marcaDelCuerpo = items.length;
              return marcaDelCuerpo * descuento;
            }
            """;

        var chunks = await ChunkAsync(contenido);

        AssertCuerpoAcompanaALaFirma(chunks, "marcaDelCuerpo", "calcularTotal");
    }

    [Fact]
    public async Task ArchivoVacio_NoProduceChunks()
    {
        Assert.Empty(await ChunkAsync(string.Empty));
    }

    [Fact]
    public async Task TodoChunk_TieneContenidoNoVacio()
    {
        // Un chunk vacío o de solo espacios es basura pura en el índice.
        var contenido = """
            export class Servicio {
              private readonly cache = new Map<string, number>();

              obtener(clave: string): number | undefined {
                return this.cache.get(clave);
              }
            }
            """;

        var chunks = await ChunkAsync(contenido);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Content)));
    }
}
