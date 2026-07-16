using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Utilities;

namespace RagEngine.Core.Infrastructure.Chunking;

/// <summary>
/// Uses the Roslyn AST to fragment C# source files into semantically coherent chunks.
/// Each chunk represents a complete, logical unit: method, class header, interface, etc.
/// Enriched content includes a context header with namespace, class, and method info
/// to produce higher-quality embeddings.
/// </summary>
public sealed class RoslynCSharpChunkingStrategy : IChunkingStrategy
{
    private readonly ILogger<RoslynCSharpChunkingStrategy> _logger;
    private const int ApproxCharsPerToken = 4; // rough estimate: 1 token ≈ 4 chars

    public SourceLanguage TargetLanguage => SourceLanguage.CSharp;

    public RoslynCSharpChunkingStrategy(ILogger<RoslynCSharpChunkingStrategy> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CodeChunk> ChunkAsync(
        RawArtifact artifact,
        string fileContent,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SyntaxTree? syntaxTree = null;
        SyntaxNode? root = null;

        bool parseFailed = false;
        try
        {
            syntaxTree = CSharpSyntaxTree.ParseText(fileContent, cancellationToken: cancellationToken);
            root = await syntaxTree.GetRootAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse {File} with Roslyn, falling back to sliding window.",
                artifact.RelativePath);
            parseFailed = true;
        }

        if (parseFailed)
        {
            await foreach (var chunk in FallbackSlidingWindowAsync(artifact, fileContent, options, cancellationToken))
                yield return chunk;
            yield break;
        }

        var fileContext = ExtractFileContext(root!, artifact, options.RepositoryName);
        var lines = fileContent.Split('\n');
        var typeDeclarations = root!.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();

        if (!typeDeclarations.Any())
        {
            // No types found (e.g., top-level statements, scripts)
            await foreach (var chunk in FallbackSlidingWindowAsync(artifact, fileContent, options, cancellationToken))
                yield return chunk;
            yield break;
        }

        foreach (var typeDecl in typeDeclarations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // --- Class header chunk (fields + base types, without method bodies) ---
            yield return BuildClassHeaderChunk(artifact, typeDecl, fileContext, lines);

            // --- Constructor chunks ---
            foreach (var ctor in typeDecl.Members.OfType<ConstructorDeclarationSyntax>())
            {
                foreach (var chunk in BuildMemberChunks(
                    artifact, ctor, typeDecl, fileContext, lines,
                    ChunkType.Constructor, options))
                {
                    yield return chunk;
                }
            }

            // --- Method chunks ---
            foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                foreach (var chunk in BuildMethodChunks(
                    artifact, method, typeDecl, fileContext, lines, options))
                {
                    yield return chunk;
                }
            }

            // --- Grouped properties chunk ---
            var propsChunk = BuildGroupedPropertiesChunk(artifact, typeDecl, fileContext, lines);
            if (propsChunk is not null)
                yield return propsChunk;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    private sealed record FileContext(
        string? RootNamespace,
        string RelativeFilePath,
        string RepositoryName);

    private static FileContext ExtractFileContext(
        SyntaxNode root, RawArtifact artifact, string repoName)
    {
        var ns = root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()
            ?.Name.ToString();

        return new FileContext(ns, artifact.RelativePath, repoName);
    }

    private static string BuildContextHeader(
        FileContext ctx,
        TypeDeclarationSyntax typeDecl,
        MemberDeclarationSyntax? member = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// Repository: {ctx.RepositoryName}");
        sb.AppendLine($"// File: {ctx.RelativeFilePath}");

        if (ctx.RootNamespace is not null)
            sb.AppendLine($"// Namespace: {ctx.RootNamespace}");

        var typeKind = typeDecl switch
        {
            ClassDeclarationSyntax     => "class",
            InterfaceDeclarationSyntax => "interface",
            RecordDeclarationSyntax    => "record",
            StructDeclarationSyntax    => "struct",
            _                          => "type"
        };
        var baseList = typeDecl.BaseList?.ToString() ?? string.Empty;
        sb.AppendLine($"// {typeKind}: {typeDecl.Identifier.Text}{baseList}");

        if (member is MethodDeclarationSyntax method)
        {
            sb.AppendLine(
                $"// method: {method.Identifier.Text}{method.ParameterList} → {method.ReturnType}");

            var attrs = method.AttributeLists
                .SelectMany(al => al.Attributes)
                .Select(a => $"[{a.Name}]");
            if (attrs.Any())
                sb.AppendLine($"// attributes: {string.Join(", ", attrs)}");
        }
        else if (member is ConstructorDeclarationSyntax ctor)
        {
            sb.AppendLine($"// constructor: {ctor.Identifier.Text}{ctor.ParameterList}");
        }

        return sb.ToString().TrimEnd();
    }

    private CodeChunk BuildClassHeaderChunk(
        RawArtifact artifact,
        TypeDeclarationSyntax typeDecl,
        FileContext ctx,
        string[] lines)
    {
        var span = typeDecl.GetLocation().GetLineSpan();
        int startLine = span.StartLinePosition.Line + 1;

        // Class header = everything up to the opening brace + fields/props only
        var headerLines = new List<string>();
        headerLines.Add(typeDecl.ToFullString()
            .Split('\n')
            .First(l => !string.IsNullOrWhiteSpace(l)));

        // Add field declarations only (not methods)
        foreach (var field in typeDecl.Members.OfType<FieldDeclarationSyntax>())
            headerLines.Add(field.ToFullString().TrimEnd());

        var content = string.Join('\n', headerLines).Trim();
        var header = BuildContextHeader(ctx, typeDecl);
        var enriched = $"{header}\n\n{content}";
        var hash = ContentHasher.Compute(content);

        return new CodeChunk
        {
            Id = DeterministicGuid.CreateForChunk(artifact.AbsolutePath, startLine, hash),
            Content = content,
            EnrichedContent = enriched,
            Type = ChunkType.Class,
            ContentHash = hash,
            Metadata = new CodeChunkMetadata(
                FilePath: artifact.AbsolutePath,
                RelativeFilePath: artifact.RelativePath,
                Language: SourceLanguage.CSharp,
                Namespace: ctx.RootNamespace,
                ClassName: typeDecl.Identifier.Text,
                MethodName: null,
                StartLine: startLine,
                EndLine: span.EndLinePosition.Line + 1,
                LastModified: artifact.LastModified,
                RepositoryName: ctx.RepositoryName
            )
        };
    }

    private IEnumerable<CodeChunk> BuildMethodChunks(
        RawArtifact artifact,
        MethodDeclarationSyntax method,
        TypeDeclarationSyntax typeDecl,
        FileContext ctx,
        string[] lines,
        ChunkingOptions options)
    {
        var methodText = method.ToFullString().Trim();
        var span = method.GetLocation().GetLineSpan();
        int startLine = span.StartLinePosition.Line + 1;
        int endLine = span.EndLinePosition.Line + 1;

        var header = BuildContextHeader(ctx, typeDecl, method);
        var enriched = $"{header}\n\n{methodText}";
        int tokenEstimate = enriched.Length / ApproxCharsPerToken;

        if (tokenEstimate <= options.MaxTokensPerChunk)
        {
            // Method fits in one chunk
            var hash = ContentHasher.Compute(methodText);
            yield return new CodeChunk
            {
                Id = DeterministicGuid.CreateForChunk(artifact.AbsolutePath, startLine, hash),
                Content = methodText,
                EnrichedContent = enriched,
                Type = ChunkType.Method,
                ContentHash = hash,
                Metadata = new CodeChunkMetadata(
                    FilePath: artifact.AbsolutePath,
                    RelativeFilePath: artifact.RelativePath,
                    Language: SourceLanguage.CSharp,
                    Namespace: ctx.RootNamespace,
                    ClassName: typeDecl.Identifier.Text,
                    MethodName: method.Identifier.Text,
                    StartLine: startLine,
                    EndLine: endLine,
                    LastModified: artifact.LastModified,
                    RepositoryName: ctx.RepositoryName
                )
            };
            yield break;
        }

        // Method too large — split with sliding window
        var methodLines = methodText.Split('\n');
        int windowLines = options.MaxTokensPerChunk * ApproxCharsPerToken / 80; // ~80 chars/line
        int overlapLines = options.OverlapTokens * ApproxCharsPerToken / 80;
        int step = Math.Max(1, windowLines - overlapLines);
        int fragIndex = 0;

        for (int i = 0; i < methodLines.Length; i += step)
        {
            var window = methodLines.Skip(i).Take(windowLines).ToArray();
            var windowText = string.Join('\n', window).Trim();
            var windowEnriched = $"{header}\n// [Fragment {++fragIndex}]\n\n{windowText}";
            var hash = ContentHasher.Compute(windowText);

            yield return new CodeChunk
            {
                Id = DeterministicGuid.CreateForChunk(artifact.AbsolutePath, startLine + i, hash),
                Content = windowText,
                EnrichedContent = windowEnriched,
                Type = ChunkType.Method,
                ContentHash = hash,
                Metadata = new CodeChunkMetadata(
                    FilePath: artifact.AbsolutePath,
                    RelativeFilePath: artifact.RelativePath,
                    Language: SourceLanguage.CSharp,
                    Namespace: ctx.RootNamespace,
                    ClassName: typeDecl.Identifier.Text,
                    MethodName: method.Identifier.Text,
                    StartLine: startLine + i,
                    EndLine: startLine + i + window.Length,
                    LastModified: artifact.LastModified,
                    RepositoryName: ctx.RepositoryName
                )
            };
        }
    }

    private IEnumerable<CodeChunk> BuildMemberChunks(
        RawArtifact artifact,
        MemberDeclarationSyntax member,
        TypeDeclarationSyntax typeDecl,
        FileContext ctx,
        string[] lines,
        ChunkType chunkType,
        ChunkingOptions options)
    {
        var text = member.ToFullString().Trim();
        var span = member.GetLocation().GetLineSpan();
        int startLine = span.StartLinePosition.Line + 1;

        var header = BuildContextHeader(ctx, typeDecl, member);
        var enriched = $"{header}\n\n{text}";
        var hash = ContentHasher.Compute(text);

        yield return new CodeChunk
        {
            Id = DeterministicGuid.CreateForChunk(artifact.AbsolutePath, startLine, hash),
            Content = text,
            EnrichedContent = enriched,
            Type = chunkType,
            ContentHash = hash,
            Metadata = new CodeChunkMetadata(
                FilePath: artifact.AbsolutePath,
                RelativeFilePath: artifact.RelativePath,
                Language: SourceLanguage.CSharp,
                Namespace: ctx.RootNamespace,
                ClassName: typeDecl.Identifier.Text,
                MethodName: null,
                StartLine: startLine,
                EndLine: span.EndLinePosition.Line + 1,
                LastModified: artifact.LastModified,
                RepositoryName: ctx.RepositoryName
            )
        };
    }

    private CodeChunk? BuildGroupedPropertiesChunk(
        RawArtifact artifact,
        TypeDeclarationSyntax typeDecl,
        FileContext ctx,
        string[] lines)
    {
        var props = typeDecl.Members.OfType<PropertyDeclarationSyntax>().ToList();
        if (props.Count == 0) return null;

        var allProps = string.Join('\n', props.Select(p => p.ToFullString().Trim())).Trim();
        var firstSpan = props[0].GetLocation().GetLineSpan();
        var lastSpan = props[^1].GetLocation().GetLineSpan();
        int startLine = firstSpan.StartLinePosition.Line + 1;

        var header = BuildContextHeader(ctx, typeDecl);
        var enriched = $"{header}\n// Properties\n\n{allProps}";
        var hash = ContentHasher.Compute(allProps);

        return new CodeChunk
        {
            Id = DeterministicGuid.CreateForChunk(artifact.AbsolutePath, startLine, hash),
            Content = allProps,
            EnrichedContent = enriched,
            Type = ChunkType.Property,
            ContentHash = hash,
            Metadata = new CodeChunkMetadata(
                FilePath: artifact.AbsolutePath,
                RelativeFilePath: artifact.RelativePath,
                Language: SourceLanguage.CSharp,
                Namespace: ctx.RootNamespace,
                ClassName: typeDecl.Identifier.Text,
                MethodName: null,
                StartLine: startLine,
                EndLine: lastSpan.EndLinePosition.Line + 1,
                LastModified: artifact.LastModified,
                RepositoryName: ctx.RepositoryName
            )
        };
    }

    private async IAsyncEnumerable<CodeChunk> FallbackSlidingWindowAsync(
        RawArtifact artifact,
        string content,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var fallback = new FallbackChunkingStrategy();
        await foreach (var chunk in fallback.ChunkAsync(artifact, content, options, ct))
            yield return chunk;
    }
}
