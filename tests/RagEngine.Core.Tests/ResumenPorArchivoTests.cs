using RagEngine.Core.Domain;
using RagEngine.Core.Pipeline;
using RagEngine.Core.Infrastructure.Summary;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 5.b (experimental, opt-in): resumen de negocio por archivo/tipo en vez de por
/// chunk. Estos tests fijan las dos propiedades mecánicas de las que depende el
/// experimento antes de gastar cómputo real de Ollama: (1) el hash de grupo es
/// determinista e insensible al orden de scroll, y (2) el namespace de caché del modo
/// por archivo/tipo nunca colisiona con el del modo por chunk, aunque compartan
/// modelo — de lo contrario el A/B mediría una caché contaminada, no la granularidad.
/// </summary>
public class ResumenPorArchivoTests
{
    private static CodeChunk MakeChunk(string contentHash, string filePath = "src/Foo.cs", string? className = "Foo") =>
        new()
        {
            Id = Guid.NewGuid(),
            Content = $"contenido-{contentHash}",
            EnrichedContent = $"contenido-{contentHash}",
            Metadata = new CodeChunkMetadata(
                FilePath: filePath,
                RelativeFilePath: filePath,
                Language: SourceLanguage.CSharp,
                Namespace: "Demo",
                ClassName: className,
                MethodName: null,
                StartLine: 1,
                EndLine: 10,
                LastModified: DateTimeOffset.UtcNow,
                RepositoryName: "demo-repo"),
            Type = ChunkType.Method,
            ContentHash = contentHash
        };

    [Fact]
    public void HashDeGrupo_EsInsensibleAlOrdenDeLosChunks()
    {
        var a = MakeChunk("hash-a");
        var b = MakeChunk("hash-b");
        var c = MakeChunk("hash-c");

        var enOrden = DefaultIngestionPipeline.ComputeGroupContentHash([a, b, c]);
        var reordenado = DefaultIngestionPipeline.ComputeGroupContentHash([c, a, b]);

        Assert.Equal(enOrden, reordenado);
    }

    [Fact]
    public void HashDeGrupo_CambiaSiCambiaElConjuntoDeChunks()
    {
        var grupo1 = DefaultIngestionPipeline.ComputeGroupContentHash([MakeChunk("hash-a"), MakeChunk("hash-b")]);
        var grupo2 = DefaultIngestionPipeline.ComputeGroupContentHash([MakeChunk("hash-a"), MakeChunk("hash-c")]);

        Assert.NotEqual(grupo1, grupo2);
    }

    [Fact]
    public void HashDeGrupo_DeUnSoloChunk_NoColisionaConSuPropioContentHash()
    {
        // Un grupo de un solo chunk no debe ser indistinguible de un content_hash de
        // chunk individual: viven bajo distinto prompt_version, pero esta propiedad es
        // defensa en profundidad adicional (el hash de grupo es SHA-256 del hash de
        // chunk, nunca el valor crudo).
        var chunk = MakeChunk("hash-solo");
        var hashDeGrupo = DefaultIngestionPipeline.ComputeGroupContentHash([chunk]);

        Assert.NotEqual(chunk.ContentHash, hashDeGrupo);
    }

    [Fact]
    public void PromptVersionDeGrupo_EsDistintaDeLaDeChunk_ParaElMismoModelo()
    {
        const string modelId = "qwen2.5:7b-instruct";

        var versionChunk = OllamaBusinessSummaryGenerator.ComputePromptVersion(modelId);
        var versionGrupo = OllamaBusinessSummaryGenerator.ComputeGroupPromptVersion(modelId);

        Assert.NotEqual(versionChunk, versionGrupo);
    }

    [Fact]
    public void PromptVersionDeGrupo_EsDeterministaParaElMismoModelo()
    {
        const string modelId = "qwen2.5:7b-instruct";

        var primera = OllamaBusinessSummaryGenerator.ComputeGroupPromptVersion(modelId);
        var segunda = OllamaBusinessSummaryGenerator.ComputeGroupPromptVersion(modelId);

        Assert.Equal(primera, segunda);
    }
}
