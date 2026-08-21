using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Utilities;

namespace RagEngine.Core.Infrastructure.Chunking;

/// <summary>
/// Estrategia semántica para archivos Markdown.
/// Particiona el documento basándose en las cabeceras (Headers: #, ##, ###)
/// preservando el contexto mediante inyección de la sección actual.
/// </summary>
public sealed partial class MarkdownChunkingStrategy : IChunkingStrategy
{
    private readonly ILogger<MarkdownChunkingStrategy> _logger;

    // Detecta cabeceras de Markdown, ej: "## Instalación", "### Configuración"
    [GeneratedRegex(@"^(#{1,6})\s+(.*)")]
    private static partial Regex HeaderRegex();

    public MarkdownChunkingStrategy(ILogger<MarkdownChunkingStrategy> logger)
    {
        _logger = logger;
    }

    public SourceLanguage TargetLanguage => SourceLanguage.Markdown;

    public async IAsyncEnumerable<CodeChunk> ChunkAsync(
        RawArtifact artifact,
        string fileContent,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Para asegurar que corra de manera asíncrona real y liberar el hilo
        await Task.Yield();

        // Un solo punto de normalizacion: mismo contenido -> mismos chunks y
        // mismos hashes, venga el archivo de Windows o de Unix.
        fileContent = SourceLines.Normalize(fileContent);

        var lines = SourceLines.Split(fileContent);

        string currentHeader = "Documento Principal";
        var currentSectionLines = new List<string>();
        int sectionStartLine = 1;
        int currentLineNum = 1;

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var match = HeaderRegex().Match(line);
            if (match.Success)
            {
                // Se encontró una nueva cabecera, procesamos la sección acumulada
                if (currentSectionLines.Count > 0)
                {
                    foreach (var chunk in ProcessSection(artifact, currentHeader, currentSectionLines, sectionStartLine, currentLineNum - 1, options))
                    {
                        yield return chunk;
                    }
                    currentSectionLines.Clear();
                }

                currentHeader = match.Value; // Línea completa, ej: "## Mi Cabecera"
                sectionStartLine = currentLineNum;
            }
            else
            {
                currentSectionLines.Add(line);
            }
            currentLineNum++;
        }

        // Procesar la última sección sobrante
        if (currentSectionLines.Count > 0)
        {
            foreach (var chunk in ProcessSection(artifact, currentHeader, currentSectionLines, sectionStartLine, currentLineNum - 1, options))
            {
                yield return chunk;
            }
        }
    }

    private IEnumerable<CodeChunk> ProcessSection(
        RawArtifact artifact,
        string header,
        List<string> sectionLines,
        int sectionStartLine,
        int sectionEndLine,
        ChunkingOptions options)
    {
        string cleanHeader = header.TrimStart('#').Trim();
        string contextHeader = $"[Sección: {cleanHeader}]";

        // Extraer los párrafos basándose en líneas en blanco
        var paragraphs = new List<(string Text, int StartLine, int EndLine)>();
        var currentPara = new List<string>();
        int paraStartLine = sectionStartLine + 1; // +1 porque la línea 1 de la sección era el Header
        int currentLineNum = paraStartLine;

        foreach (var line in sectionLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (currentPara.Count > 0)
                {
                    paragraphs.Add((string.Join("\n", currentPara), paraStartLine, currentLineNum - 1));
                    currentPara.Clear();
                }
                paraStartLine = currentLineNum + 1;
            }
            else
            {
                currentPara.Add(line);
            }
            currentLineNum++;
        }

        if (currentPara.Count > 0)
        {
            paragraphs.Add((string.Join("\n", currentPara), paraStartLine, currentLineNum - 1));
        }

        // Agrupar párrafos respetando el MaxTokensPerChunk
        var chunkParas = new List<string>();
        int chunkStartLine = sectionStartLine;
        int chunkEndLine = sectionStartLine;

        foreach (var para in paragraphs)
        {
            // Validar si el chunk actual + el nuevo párrafo exceden el límite de tokens
            string testContent = $"{contextHeader}\n\n{string.Join("\n\n", chunkParas.Concat(new[] { para.Text }))}";

            if (TokenEstimator.Estimate(testContent) > options.MaxTokensPerChunk && chunkParas.Count > 0)
            {
                // Emitir el bloque consolidado actual
                string flushContent = $"{contextHeader}\n\n{string.Join("\n\n", chunkParas)}";
                yield return CreateChunk(artifact, cleanHeader, flushContent, chunkStartLine, chunkEndLine, options);

                // Iniciar un nuevo bloque secundario
                chunkParas.Clear();
                chunkStartLine = para.StartLine;
            }

            if (chunkParas.Count == 0)
            {
                // Si es el primer bloque de esta partición secundaria, usamos su línea de inicio.
                // Sin embargo, si es el *primer* párrafo absoluto, usamos el sectionStartLine para atrapar la cabecera lógicamente.
                chunkStartLine = paragraphs.IndexOf(para) == 0 ? sectionStartLine : para.StartLine;
            }

            chunkParas.Add(para.Text);
            chunkEndLine = para.EndLine;
        }

        // Vaciar el último bloque remanente
        if (chunkParas.Count > 0)
        {
            string flushContent = $"{contextHeader}\n\n{string.Join("\n\n", chunkParas)}";
            yield return CreateChunk(artifact, cleanHeader, flushContent, chunkStartLine, chunkEndLine, options);
        }
    }

    private CodeChunk CreateChunk(RawArtifact artifact, string sectionName, string content, int startLine, int endLine, ChunkingOptions options)
    {
        // Hash de contenido + Path garantiza IDs estables para el motor de Qdrant (idempotencia)
        string hash = ContentHasher.Compute(content);
        Guid id = DeterministicGuid.CreateForChunk(artifact.AbsolutePath, startLine, hash);

        return new CodeChunk
        {
            Id = id,
            Content = content,
            EnrichedContent = content, // Para Markdown, la cabecera inyectada ya hace las veces de EnrichedContent
            ContentHash = hash,
            Type = ChunkType.DocumentSection,
            Metadata = new CodeChunkMetadata(
                FilePath: artifact.AbsolutePath,
                RelativeFilePath: artifact.RelativePath,
                Language: artifact.Language,
                Namespace: null,
                ClassName: null,
                MethodName: sectionName, // Guardamos la sección lógica aquí
                StartLine: startLine,
                EndLine: endLine,
                LastModified: artifact.LastModified,
                RepositoryName: options.RepositoryName
            )
        };
    }
}
