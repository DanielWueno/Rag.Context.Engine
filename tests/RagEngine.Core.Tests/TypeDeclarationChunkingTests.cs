using Microsoft.Extensions.Logging.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Chunking;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 5.g del plan: la declaración de un tipo C# debe quedar en el chunk de tipo
/// <see cref="ChunkType.Class"/> que la representa, con el texto normalizado de forma
/// declarada en vez de silenciosa.
///
/// Hallazgo original (etiquetado del eval-set de bsuite-repo): `public interface X : Y`
/// se indexaba como `public interface X: Y`, sin el espacio antes de ':' — porque
/// <c>BaseListSyntax.ToString()</c> descarta la trivia inicial del primer token. El fix
/// normaliza siempre a un solo espacio en vez de perseguir preservar el whitespace
/// exacto del archivo (frágil: 0, 1 o varios espacios, salto de línea antes de ':').
///
/// El otro hallazgo del mismo etiquetado —que un tipo sin miembros propios puede no
/// tener NINGÚN chunk Class en el índice— no es un bug del chunker (se verificó que
/// el chunker de HEAD sí produce ese chunk); es <c>DefaultIngestionPipeline.MinIndexableContentChars</c>
/// descartándolo por longitud antes de llegar a Qdrant. Eso queda fuera de esta ficha
/// (archivos: sólo RoslynCSharpChunkingStrategy.cs) y se registra como ítem nuevo.
/// </summary>
public class TypeDeclarationChunkingTests
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

    [Fact]
    public async Task DeclaracionConBaseList_ConservaElEspacioAntesDeLosDosPuntos()
    {
        const string codigo = """
            namespace Ejemplo;

            public interface IEntidadReport : IEntidad
            {
                void Reportar();
            }
            """;

        var chunks = await ChunkCSharpAsync(codigo);
        var claseChunk = Assert.Single(chunks, c => c.Type == ChunkType.Class && c.Metadata.ClassName == "IEntidadReport");

        Assert.Contains("public interface IEntidadReport : IEntidad", claseChunk.Content);
        Assert.DoesNotContain("IEntidadReport: IEntidad", claseChunk.Content);
    }

    [Fact]
    public async Task DeclaracionConMultiplesInterfacesBase_ConservaElEspacioAntesDeLosDosPuntos()
    {
        const string codigo = """
            namespace Ejemplo;

            public class DevolucionInterna : ProcesoBase, IValidable, IAuditable
            {
                public int Folio { get; set; }
            }
            """;

        var chunks = await ChunkCSharpAsync(codigo);
        var claseChunk = Assert.Single(chunks, c => c.Type == ChunkType.Class && c.Metadata.ClassName == "DevolucionInterna");

        Assert.Contains("public class DevolucionInterna : ProcesoBase, IValidable, IAuditable", claseChunk.Content);
    }

    [Fact]
    public async Task TipoSinAtributosConMiembrosPropios_ProduceUnChunkQueContieneSuDeclaracion()
    {
        const string codigo = """
            namespace Ejemplo;

            public class PagoService
            {
                public int Monto { get; set; }
            }
            """;

        var chunks = await ChunkCSharpAsync(codigo);
        var claseChunk = Assert.Single(chunks, c => c.Type == ChunkType.Class && c.Metadata.ClassName == "PagoService");

        Assert.Contains("public class PagoService", claseChunk.Content);
    }

    [Fact]
    public async Task TipoConAtributos_SigueProduciendoUnChunkConSuDeclaracion()
    {
        // No-regresión sobre el caso DevolucionInterna citado en la ficha: una clase con
        // varios atributos ([DefaultClassOptions], reglas XAF, etc.) ya emitía su
        // declaración completa desde el fix de 4c7c408 — esto lo deja cubierto por test,
        // no sólo por lectura del código.
        const string codigo = """
            namespace Ejemplo;

            [DefaultClassOptions]
            [Persistent("ia_tr_devoluciones_interna")]
            [XafDisplayName("Devolución Interna")]
            public class DevolucionInterna : ProcesoBase
            {
                public int Folio { get; set; }
            }
            """;

        var chunks = await ChunkCSharpAsync(codigo);
        var claseChunk = Assert.Single(chunks, c => c.Type == ChunkType.Class && c.Metadata.ClassName == "DevolucionInterna");

        Assert.Contains("[DefaultClassOptions]", claseChunk.Content);
        Assert.Contains("public class DevolucionInterna : ProcesoBase", claseChunk.Content);
    }

    [Fact]
    public async Task InterfazSoloConFirmasDeMetodo_ProduceUnChunkClassConSuDeclaracion()
    {
        // Caso ICombProvider: interfaz sin campos ni propiedades, sólo firmas de método.
        // El chunker SÍ produce este chunk (verificado también contra el archivo real
        // IProvider.cs de bsuite-repo) — lo que lo descarta antes de Qdrant es el filtro
        // de longitud mínima de la ingesta, no el chunker.
        const string codigo = """
            namespace Ejemplo;

            public interface ICombProvider
            {
                Guid Create();
                Guid Create(Guid value);
            }
            """;

        var chunks = await ChunkCSharpAsync(codigo);
        var claseChunk = Assert.Single(chunks, c => c.Type == ChunkType.Class && c.Metadata.ClassName == "ICombProvider");

        Assert.Contains("public interface ICombProvider", claseChunk.Content);
    }
}
