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

            // --- Class header chunk(s) (fields + base types, without method bodies) ---
            foreach (var chunk in BuildClassHeaderChunks(artifact, typeDecl, fileContext, lines, options))
                yield return chunk;

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

            // --- Grouped properties chunk(s) ---
            foreach (var chunk in BuildGroupedPropertiesChunks(artifact, typeDecl, fileContext, lines, options))
                yield return chunk;
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

    private IEnumerable<CodeChunk> BuildClassHeaderChunks(
        RawArtifact artifact,
        TypeDeclarationSyntax typeDecl,
        FileContext ctx,
        string[] lines,
        ChunkingOptions options)
    {
        var span = typeDecl.GetLocation().GetLineSpan();
        int startLine = span.StartLinePosition.Line + 1;

        // Class header = attributes + full declaration line + fields (sin cuerpos).
        // Se reconstruye desde el AST en lugar de tomar la "primera línea no vacía"
        // de ToFullString(): en clases con atributos ([DefaultClassOptions], reglas
        // XAF, etc.) esa primera línea es el atributo y se perdía la declaración
        // completa, dejando chunks de clase casi vacíos.
        var declarationLines = new List<string>();

        foreach (var attrList in typeDecl.AttributeLists)
            declarationLines.Add(attrList.ToString().Trim());

        var declaration =
            $"{typeDecl.Modifiers} {typeDecl.Keyword} {typeDecl.Identifier}" +
            $"{typeDecl.TypeParameterList}{typeDecl.BaseList}";
        declarationLines.Add(declaration.Trim());
        var declarationText = string.Join('\n', declarationLines).Trim();

        var fields = typeDecl.Members.OfType<FieldDeclarationSyntax>().ToList();
        var header = BuildContextHeader(ctx, typeDecl);
        int headerTokens = header.Length / ApproxCharsPerToken;
        int declarationTokens = declarationText.Length / ApproxCharsPerToken;

        // Group fields into batches that keep the header (attrs + declaration) plus
        // fields under MaxTokensPerChunk, splitting only at field boundaries — same
        // fix as BuildGroupedPropertiesChunks, for classes with many fields.
        var groups = new List<List<FieldDeclarationSyntax>>();
        var current = new List<FieldDeclarationSyntax>();
        int currentChars = 0;

        foreach (var field in fields)
        {
            int fieldChars = field.ToFullString().TrimEnd().Length;
            int baseTokens = headerTokens + declarationTokens;
            int projectedTokens = baseTokens + (currentChars + fieldChars) / ApproxCharsPerToken;

            if (current.Count > 0 && projectedTokens > options.MaxTokensPerChunk)
            {
                groups.Add(current);
                current = new List<FieldDeclarationSyntax>();
                currentChars = 0;
            }

            current.Add(field);
            currentChars += fieldChars + 1;
        }
        groups.Add(current); // always at least the declaration-only group, even with no fields

        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var isFirst = g == 0;

            var contentLines = new List<string>();
            if (isFirst)
                contentLines.Add(declarationText);
            contentLines.AddRange(group.Select(f => f.ToFullString().TrimEnd()));
            var content = string.Join('\n', contentLines).Trim();

            int fragmentStartLine = isFirst || group.Count == 0
                ? startLine
                : group[0].GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            int fragmentEndLine = group.Count > 0
                ? group[^1].GetLocation().GetLineSpan().EndLinePosition.Line + 1
                : span.EndLinePosition.Line + 1;

            var fragmentSuffix = groups.Count > 1 ? $" [Fragment {g + 1}/{groups.Count}]" : string.Empty;
            var enriched = $"{header}{fragmentSuffix}\n\n{content}";
            var hash = ContentHasher.Compute(content);

            yield return new CodeChunk
            {
                Id = DeterministicGuid.CreateForChunk(artifact.AbsolutePath, fragmentStartLine, hash),
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
                    StartLine: fragmentStartLine,
                    EndLine: fragmentEndLine,
                    LastModified: artifact.LastModified,
                    RepositoryName: ctx.RepositoryName
                )
            };
        }
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

    private IEnumerable<CodeChunk> BuildGroupedPropertiesChunks(
        RawArtifact artifact,
        TypeDeclarationSyntax typeDecl,
        FileContext ctx,
        string[] lines,
        ChunkingOptions options)
    {
        var props = typeDecl.Members.OfType<PropertyDeclarationSyntax>().ToList();
        if (props.Count == 0) yield break;

        var header = BuildContextHeader(ctx, typeDecl);
        int headerTokens = header.Length / ApproxCharsPerToken;

        // Group properties into batches that stay under MaxTokensPerChunk, splitting
        // only at property boundaries (never mid-declaration) — mirrors the sliding
        // window BuildMethodChunks already applies to oversized methods. Without this,
        // classes with many properties produce a single chunk that grows unbounded
        // (e.g. 700+ lines), burying individual properties past the retrieval TopK.
        var groups = new List<List<PropertyDeclarationSyntax>>();
        var current = new List<PropertyDeclarationSyntax>();
        int currentChars = 0;

        foreach (var prop in props)
        {
            int propChars = prop.ToFullString().Trim().Length;
            int projectedTokens = headerTokens + (currentChars + propChars) / ApproxCharsPerToken;

            if (current.Count > 0 && projectedTokens > options.MaxTokensPerChunk)
            {
                groups.Add(current);
                current = new List<PropertyDeclarationSyntax>();
                currentChars = 0;
            }

            current.Add(prop);
            currentChars += propChars + 1;
        }
        groups.Add(current);

        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var content = string.Join('\n', group.Select(p => p.ToFullString().Trim())).Trim();
            var firstSpan = group[0].GetLocation().GetLineSpan();
            var lastSpan = group[^1].GetLocation().GetLineSpan();
            int startLine = firstSpan.StartLinePosition.Line + 1;

            var fragmentSuffix = groups.Count > 1 ? $" [Fragment {g + 1}/{groups.Count}]" : string.Empty;
            var enriched = $"{header}\n// Properties{fragmentSuffix}\n\n{content}";
            var hash = ContentHasher.Compute(content);

            yield return new CodeChunk
            {
                Id = DeterministicGuid.CreateForChunk(artifact.AbsolutePath, startLine, hash),
                Content = content,
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
