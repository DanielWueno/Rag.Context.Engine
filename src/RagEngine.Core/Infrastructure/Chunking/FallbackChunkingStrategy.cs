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
    private const int ApproxCharsPerToken = 4;
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
        var lines = fileContent.Split('\n');
        if (lines.Length == 0) yield break;

        int windowLines = Math.Max(MinChunkLines,
            options.MaxTokensPerChunk * ApproxCharsPerToken / 80);
        int overlapLines = Math.Max(0,
            options.OverlapTokens * ApproxCharsPerToken / 80);
        int step = Math.Max(1, windowLines - overlapLines);

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
            var enriched = $"{header}\n\n{content}";
            var hash = ContentHasher.Compute(content);

            yield return new CodeChunk
            {
                Id = DeterministicGuid.CreateForChunk(artifact.AbsolutePath, startLine, hash),
                Content = content,
                EnrichedContent = enriched,
                Type = ChunkType.PlainTextWindow,
                ContentHash = hash,
                Metadata = new CodeChunkMetadata(
                    FilePath: artifact.AbsolutePath,
                    RelativeFilePath: artifact.RelativePath,
                    Language: artifact.Language,
                    Namespace: null,
                    ClassName: null,
                    MethodName: null,
                    StartLine: startLine,
                    EndLine: endLine,
                    LastModified: artifact.LastModified,
                    RepositoryName: options.RepositoryName
                )
            };

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
        return $"// Repository: {repoName}\n// File: {artifact.RelativePath}\n// Language: {artifact.Language}\n// Lines: {startLine}-{endLine} [Fragment {fragmentIndex}]";
    }
}
