using Microsoft.Extensions.Logging.Abstractions;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Chunking;

namespace RagEngine.Poc.FreeSearch;

/// <summary>
/// Fase 1 del PoC: recolecta CodeChunks de una carpeta reutilizando los MISMOS
/// chunkers del motor real, pero SIN el pipeline de ingesta (sin Channel, sin ONNX,
/// sin Qdrant). Cada estrategia sólo depende de ILogger, así que se instancian con
/// NullLogger — ésta es la razón por la que "script offline sin tocar el pipeline"
/// es viable en esta arquitectura.
/// </summary>
public sealed class ChunkHarvester
{
    // Espejo mínimo del ExtensionMap de FileSystemIngestionScanner (que es privado).
    private static readonly Dictionary<string, SourceLanguage> ExtensionMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = SourceLanguage.CSharp,
            [".ts"] = SourceLanguage.TypeScript,
            [".tsx"] = SourceLanguage.TypeScript,
            [".js"] = SourceLanguage.JavaScript,
            [".jsx"] = SourceLanguage.JavaScript,
            [".xaml"] = SourceLanguage.Xaml,
            [".sql"] = SourceLanguage.Sql,
            [".md"] = SourceLanguage.Markdown,
            [".txt"] = SourceLanguage.PlainText,
            [".json"] = SourceLanguage.PlainText,
            [".xml"] = SourceLanguage.PlainText,
            [".csproj"] = SourceLanguage.PlainText,
        };

    private readonly ChunkingStrategyRouter _router;

    public ChunkHarvester()
    {
        // Sólo las estrategias con AST/lexer dedicado; el resto cae al fallback.
        var strategies = new IChunkingStrategy[]
        {
            new RoslynCSharpChunkingStrategy(NullLogger<RoslynCSharpChunkingStrategy>.Instance),
            new TypeScriptChunkingStrategy(NullLogger<TypeScriptChunkingStrategy>.Instance),
            new MarkdownChunkingStrategy(NullLogger<MarkdownChunkingStrategy>.Instance),
        };

        _router = new ChunkingStrategyRouter(
            strategies,
            new FallbackChunkingStrategy(),
            NullLogger<ChunkingStrategyRouter>.Instance);
    }

    /// <summary>Recorre <paramref name="root"/> y devuelve todos los chunks de los archivos soportados.</summary>
    public async Task<List<CodeChunk>> HarvestAsync(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"SourceRoot no existe: {root}");

        var options = ChunkingOptions.Default;
        var chunks = new List<CodeChunk>();

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(path);
            if (!ExtensionMap.TryGetValue(ext, out var language))
                continue;

            var info = new FileInfo(path);
            var artifact = new RawArtifact(
                AbsolutePath: info.FullName,
                RelativePath: Path.GetRelativePath(root, info.FullName),
                Language: language,
                LastModified: info.LastWriteTimeUtc,
                SizeBytes: info.Length);

            string content;
            try
            {
                content = await File.ReadAllTextAsync(path, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [skip] no se pudo leer {artifact.RelativePath}: {ex.Message}");
                continue;
            }

            var strategy = _router.GetStrategy(artifact);
            await foreach (var chunk in strategy.ChunkAsync(artifact, content, options, ct))
                chunks.Add(chunk);
        }

        return chunks;
    }
}
