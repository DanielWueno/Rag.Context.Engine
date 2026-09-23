using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Utilities;

namespace RagEngine.Core.Infrastructure.Chunking;

/// <summary>
/// Sliding-window chunking strategy for unsupported languages (SQL, Markdown, plain text, etc.).
/// Also used as a fallback when language-specific strategies fail.
/// Uses a configurable window size and overlap to maintain context continuity.
/// </summary>
public sealed class FallbackChunkingStrategy : IChunkingStrategy
{
    private const int MinChunkLines = 5;

    // SourceLanguage.Unknown means this strategy accepts any language
    public SourceLanguage TargetLanguage => SourceLanguage.Unknown;

    /// <inheritdoc />
    public async IAsyncEnumerable<CodeChunk> ChunkAsync(
        RawArtifact artifact,
        string fileContent,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Un solo punto de normalizacion: mismo contenido -> mismos chunks y
        // mismos hashes, venga el archivo de Windows o de Unix.
        fileContent = SourceLines.Normalize(fileContent);

        var lines = SourceLines.Split(fileContent);
        if (lines.Length == 0) yield break;

        var (windowLines, _, step) = ChunkBuilder.WindowGeometry(options, MinChunkLines);

        int fragIndex = 0;

        for (int i = 0; i < lines.Length; i += step)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var window = lines.Skip(i).Take(windowLines).ToArray();
            var content = string.Join('\n', window).Trim();

            if (string.IsNullOrWhiteSpace(content)) continue;

            int startLine = i + 1;
            int endLine = i + window.Length;

            var header = BuildContextHeader(artifact, options.RepositoryName, startLine, endLine, ++fragIndex);

            yield return ChunkBuilder.Create(
                artifact,
                content: content,
                enrichedContent: $"{header}\n\n{content}",
                type: ChunkType.PlainTextWindow,
                startLine: startLine,
                endLine: endLine,
                repositoryName: options.RepositoryName);

            // Stop if we've reached the end
            if (i + window.Length >= lines.Length) break;
        }

        await Task.CompletedTask; // satisfy async enumerable requirement
    }

    private static string BuildContextHeader(
        RawArtifact artifact,
        string repoName,
        int startLine,
        int endLine,
        int fragmentIndex)
    {
        return ChunkBuilder.HeaderPrefix(repoName, artifact.RelativePath)
             + $"\n// Language: {artifact.Language}"
             + $"\n// Lines: {startLine}-{endLine} [Fragment {fragmentIndex}]";
    }
}
