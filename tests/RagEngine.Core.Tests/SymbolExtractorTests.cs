using Microsoft.Extensions.Logging.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Chunking;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 5.c del plan: extractor sintáctico de símbolos (defined_symbols /
/// consumed_symbols) para C# y TypeScript.
///
/// Los casos negativos (comentario, string literal) no son un detalle cosmético:
/// son la propiedad que justifica usar el AST en vez de un grep de identificadores
/// sobre el texto crudo del chunk — un grep habría encontrado el nombre igual
/// dentro de un comentario o de un string, contaminando el índice de símbolos con
/// menciones que no son referencias reales de código.
/// </summary>
public class SymbolExtractorTests
{
    private static async Task<IReadOnlyList<CodeChunk>> ChunkCSharpAsync(string contenido)
    {
        var strategy = new RoslynCSharpChunkingStrategy(
            NullLogger<RoslynCSharpChunkingStrategy>.Instance);

        var artifact = new RawArtifact(
            AbsolutePath: "/tmp/ejemplo.cs",
            RelativePath: "ejemplo.cs",
            Language: SourceLanguage.CSharp,
            LastModified: DateTimeOffset.UnixEpoch,
            SizeBytes: contenido.Length);

        var chunks = new List<CodeChunk>();
        await foreach (var chunk in strategy.ChunkAsync(artifact, contenido, ChunkingOptions.Default))
            chunks.Add(chunk);

        return chunks;
    }

    private static async Task<IReadOnlyList<CodeChunk>> ChunkTypeScriptAsync(string contenido)
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
            chunks.Add(chunk);

        return chunks;
    }

    // ── C#: consumed ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CSharp_Condicional_ProduceAmbosIdentificadoresEnConsumed()
    {
        const string codigo = """
            namespace Ejemplo;

            public class OrderValidator
            {
                public bool PuedeAprobar(User x)
                {
                    if (HasPermission(x) && Status == Draft)
                    {
                        return true;
                    }
                    return false;
                }
            }
            """;

        var chunks = await ChunkCSharpAsync(codigo);
        var metodo = Assert.Single(chunks, c => c.Metadata.MethodName == "PuedeAprobar");

        Assert.Contains("HasPermission", metodo.ConsumedSymbols);
        Assert.Contains("Status", metodo.ConsumedSymbols);
    }

    [Fact]
    public async Task CSharp_ComentarioDeLinea_NoProduceElNombreEnConsumed()
    {
        const string codigo = """
            namespace Ejemplo;

            public class OrderService
            {
                public void Procesar()
                {
                    // llama a CanCancelOrder() en el modulo viejo
                    var x = 1;
                }
            }
            """;

        var chunks = await ChunkCSharpAsync(codigo);
        var metodo = Assert.Single(chunks, c => c.Metadata.MethodName == "Procesar");

        Assert.DoesNotContain("CanCancelOrder", metodo.ConsumedSymbols);
    }

    [Fact]
    public async Task CSharp_ComentarioDeBloque_NoProduceElNombreEnConsumed()
    {
        const string codigo = """
            namespace Ejemplo;

            public class OrderService
            {
                public void Procesar()
                {
                    /* llama a CanCancelOrder() en el modulo viejo */
                    var x = 1;
                }
            }
            """;

        var chunks = await ChunkCSharpAsync(codigo);
        var metodo = Assert.Single(chunks, c => c.Metadata.MethodName == "Procesar");

        Assert.DoesNotContain("CanCancelOrder", metodo.ConsumedSymbols);
    }

    [Fact]
    public async Task CSharp_StringLiteral_NoProduceElNombreEnConsumed()
    {
        const string codigo = """
            namespace Ejemplo;

            public class OrderService
            {
                public void Procesar()
                {
                    var mensaje = "ver CanCancelOrder para mas detalle";
                }
            }
            """;

        var chunks = await ChunkCSharpAsync(codigo);
        var metodo = Assert.Single(chunks, c => c.Metadata.MethodName == "Procesar");

        Assert.DoesNotContain("CanCancelOrder", metodo.ConsumedSymbols);
    }

    // ── C#: defined ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CSharp_NombreDeClase_ApareceEnDefinedSymbolsDelChunkQueLaDeclara()
    {
        const string codigo = """
            namespace Ejemplo;

            public class ProcesarPago
            {
                public int Monto { get; set; }
            }
            """;

        var chunks = await ChunkCSharpAsync(codigo);
        var claseChunk = Assert.Single(chunks, c => c.Type == ChunkType.Class && c.Metadata.ClassName == "ProcesarPago");

        Assert.Contains("ProcesarPago", claseChunk.DefinedSymbols);
    }

    [Fact]
    public async Task CSharp_NombreDeMetodo_ApareceEnDefinedSymbolsDelChunkQueLoDeclara()
    {
        const string codigo = """
            namespace Ejemplo;

            public class PagoService
            {
                public void ProcesarPago(int monto)
                {
                    Confirmar(monto);
                }
            }
            """;

        var chunks = await ChunkCSharpAsync(codigo);
        var metodo = Assert.Single(chunks, c => c.Metadata.MethodName == "ProcesarPago");

        Assert.Contains("ProcesarPago", metodo.DefinedSymbols);
        Assert.Contains("Confirmar", metodo.ConsumedSymbols);
    }

    // ── C#: archivo roto no tumba la ingesta ────────────────────────────────

    [Fact]
    public async Task CSharp_ArchivoConErrorDeSintaxis_NoLanzaYCaeAFallback()
    {
        // Llave sin cerrar: error de sintaxis real, no sólo semántico.
        const string codigoRoto = """
            namespace Ejemplo;

            public class OrderService
            {
                public void Procesar()
                {
                    var x = 1;
            """;

        var excepcion = await Record.ExceptionAsync(async () =>
        {
            var chunks = await ChunkCSharpAsync(codigoRoto);
            Assert.NotEmpty(chunks);
        });

        Assert.Null(excepcion);
    }

    [Fact]
    public async Task CSharp_ClaseSinNombre_NoLanzaYCaeAFallback()
    {
        const string codigoRoto = """
            namespace Ejemplo;

            public class
            {
                public void Procesar() { var x = 1; }
            }
            """;

        var excepcion = await Record.ExceptionAsync(async () =>
        {
            var chunks = await ChunkCSharpAsync(codigoRoto);
            Assert.NotEmpty(chunks);
        });

        Assert.Null(excepcion);
    }

    // ── TypeScript: consumed ─────────────────────────────────────────────────

    [Fact]
    public async Task TypeScript_Condicional_ProduceAmbosIdentificadoresEnConsumed()
    {
        const string codigo = """
            export function puedeAprobar(x) {
              if (hasPermission(x) && status === draft) {
                return true;
              }
              return false;
            }
            """;

        var chunks = await ChunkTypeScriptAsync(codigo);
        var metodo = Assert.Single(chunks, c => c.Metadata.MethodName == "puedeAprobar");

        Assert.Contains("hasPermission", metodo.ConsumedSymbols);
        Assert.Contains("status", metodo.ConsumedSymbols);
    }

    [Fact]
    public async Task TypeScript_ComentarioDeLinea_NoProduceElNombreEnConsumed()
    {
        const string codigo = """
            export function procesar() {
              // llama a canCancelOrder() en el modulo viejo
              const x = 1;
            }
            """;

        var chunks = await ChunkTypeScriptAsync(codigo);
        var metodo = Assert.Single(chunks, c => c.Metadata.MethodName == "procesar");

        Assert.DoesNotContain("canCancelOrder", metodo.ConsumedSymbols);
    }

    [Fact]
    public async Task TypeScript_StringLiteral_NoProduceElNombreEnConsumed()
    {
        const string codigo = """
            export function procesar() {
              const mensaje = "ver canCancelOrder para mas detalle";
            }
            """;

        var chunks = await ChunkTypeScriptAsync(codigo);
        var metodo = Assert.Single(chunks, c => c.Metadata.MethodName == "procesar");

        Assert.DoesNotContain("canCancelOrder", metodo.ConsumedSymbols);
    }

    [Fact]
    public async Task TypeScript_NombreDeFuncion_ApareceEnDefinedSymbols()
    {
        const string codigo = """
            export function procesarPago(monto) {
              confirmar(monto);
            }
            """;

        var chunks = await ChunkTypeScriptAsync(codigo);
        var metodo = Assert.Single(chunks, c => c.Metadata.MethodName == "procesarPago");

        Assert.Contains("procesarPago", metodo.DefinedSymbols);
        Assert.Contains("confirmar", metodo.ConsumedSymbols);
    }
}
