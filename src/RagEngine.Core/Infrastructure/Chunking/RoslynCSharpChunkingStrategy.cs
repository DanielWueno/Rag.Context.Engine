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
    // Alias de la constante compartida: misma regla de 4 chars/token que
    // TokenEstimator, sin una segunda copia del numero. La division sigue
    // siendo entera aqui — ver la nota en TokenEstimator.CharsPerToken.
    private const int ApproxCharsPerToken = (int)TokenEstimator.CharsPerToken;

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
        // Normalizacion ANTES de parsear, no despues: el contenido de constructores,
        // metodos y grupos de campos se toma del arbol con ToFullString(), que
        // devuelve el texto tal cual venia. Si el arbol se construye desde texto
        // CRLF, esos chunks arrastran el '\r' aunque el arreglo de lineas este
        // normalizado — medido con el golden master: 3 de 8 chunks seguian sucios.
        fileContent = SourceLines.Normalize(fileContent);

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
        var lines = SourceLines.Split(fileContent);
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
        sb.AppendLine(ChunkBuilder.HeaderPrefix(ctx.RepositoryName, ctx.RelativeFilePath));

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

        // Class header = attributes + full declaration line (sin cuerpos ni campos).
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

        var header = BuildContextHeader(ctx, typeDecl);

        // The declaration is ALWAYS its own chunk, never merged with the first field
        // group — merging assumed the first field always sits right after the class
        // declaration, which is false whenever properties/methods/nested types come
        // first (common in this codebase). EndLine is the opening brace's own line,
        // never the whole class body: a class with zero fields previously reported
        // EndLine = end of the entire class even though Content was just the
        // declaration text. Semicolon-terminated declarations (e.g. a positional
        // `record Foo(...);` with no body) have no OpenBraceToken — fall back to the
        // declaration's own span end rather than a missing token's meaningless location.
        int declarationEndLine = typeDecl.OpenBraceToken.IsKind(SyntaxKind.OpenBraceToken)
            ? typeDecl.OpenBraceToken.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            : span.EndLinePosition.Line + 1;
        yield return ChunkBuilder.Create(
            artifact,
            content: declarationText,
            enrichedContent: $"{header}\n\n{declarationText}",
            type: ChunkType.Class,
            startLine: startLine,
            endLine: declarationEndLine,
            repositoryName: ctx.RepositoryName,
            language: SourceLanguage.CSharp,
            namespaceName: ctx.RootNamespace,
            className: typeDecl.Identifier.Text);

        // Group fields into batches that stay under MaxTokensPerChunk, splitting only
        // at field boundaries — same fix as BuildGroupedPropertiesChunks, for classes
        // with many fields. Fields are first partitioned into contiguous runs (no
        // other member kind interleaved) so StartLine/EndLine never claims to cover
        // code — methods, nested types — that isn't actually part of the chunk.
        int headerTokens = header.Length / ApproxCharsPerToken;
        var fieldRuns = PartitionIntoContiguousRuns<FieldDeclarationSyntax>(typeDecl.Members);
        var groups = new List<List<FieldDeclarationSyntax>>();

        foreach (var run in fieldRuns)
        {
            var current = new List<FieldDeclarationSyntax>();
            int currentChars = 0;

            foreach (var field in run)
            {
                int fieldChars = field.ToFullString().TrimEnd().Length;
                int projectedTokens = headerTokens + (currentChars + fieldChars) / ApproxCharsPerToken;

                if (current.Count > 0 && projectedTokens > options.MaxTokensPerChunk)
                {
                    groups.Add(current);
                    current = new List<FieldDeclarationSyntax>();
                    currentChars = 0;
                }

                current.Add(field);
                currentChars += fieldChars + 1;
            }
            if (current.Count > 0)
                groups.Add(current);
        }

        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var content = string.Join('\n', group.Select(f => f.ToFullString().TrimEnd())).Trim();
            int fragmentStartLine = group[0].GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            int fragmentEndLine = group[^1].GetLocation().GetLineSpan().EndLinePosition.Line + 1;

            var fragmentSuffix = groups.Count > 1 ? $" [Fragment {g + 1}/{groups.Count}]" : string.Empty;
            yield return ChunkBuilder.Create(
                artifact,
                content: content,
                enrichedContent: $"{header}\n// Fields{fragmentSuffix}\n\n{content}",
                type: ChunkType.Class,
                startLine: fragmentStartLine,
                endLine: fragmentEndLine,
                repositoryName: ctx.RepositoryName,
                language: SourceLanguage.CSharp,
                namespaceName: ctx.RootNamespace,
                className: typeDecl.Identifier.Text);
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
            yield return ChunkBuilder.Create(
                artifact,
                content: methodText,
                enrichedContent: enriched,
                type: ChunkType.Method,
                startLine: startLine,
                endLine: endLine,
                repositoryName: ctx.RepositoryName,
                language: SourceLanguage.CSharp,
                namespaceName: ctx.RootNamespace,
                className: typeDecl.Identifier.Text,
                methodName: method.Identifier.Text);
            yield break;
        }

        // Method too large — split with sliding window
        var methodLines = SourceLines.Split(methodText);
        var (windowLines, _, step) = ChunkBuilder.WindowGeometry(options);
        int fragIndex = 0;

        for (int i = 0; i < methodLines.Length; i += step)
        {
            var window = methodLines.Skip(i).Take(windowLines).ToArray();
            var windowText = string.Join('\n', window).Trim();
            var windowEnriched = $"{header}\n// [Fragment {++fragIndex}]\n\n{windowText}";

            yield return ChunkBuilder.Create(
                artifact,
                content: windowText,
                enrichedContent: windowEnriched,
                type: ChunkType.Method,
                startLine: startLine + i,
                endLine: startLine + i + window.Length,
                repositoryName: ctx.RepositoryName,
                language: SourceLanguage.CSharp,
                namespaceName: ctx.RootNamespace,
                className: typeDecl.Identifier.Text,
                methodName: method.Identifier.Text);
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

        yield return ChunkBuilder.Create(
            artifact,
            content: text,
            enrichedContent: enriched,
            type: chunkType,
            startLine: startLine,
            endLine: span.EndLinePosition.Line + 1,
            repositoryName: ctx.RepositoryName,
            language: SourceLanguage.CSharp,
            namespaceName: ctx.RootNamespace,
            className: typeDecl.Identifier.Text);
    }

    private IEnumerable<CodeChunk> BuildGroupedPropertiesChunks(
        RawArtifact artifact,
        TypeDeclarationSyntax typeDecl,
        FileContext ctx,
        string[] lines,
        ChunkingOptions options)
    {
        if (!typeDecl.Members.OfType<PropertyDeclarationSyntax>().Any()) yield break;

        var header = BuildContextHeader(ctx, typeDecl);
        int headerTokens = header.Length / ApproxCharsPerToken;

        // Group properties into batches that stay under MaxTokensPerChunk, splitting
        // only at property boundaries (never mid-declaration) — mirrors the sliding
        // window BuildMethodChunks already applies to oversized methods. Without this,
        // classes with many properties produce a single chunk that grows unbounded
        // (e.g. 700+ lines), burying individual properties past the retrieval TopK.
        // Properties are first partitioned into contiguous runs (no other member kind
        // interleaved) so StartLine/EndLine never claims to cover code — methods,
        // fields, nested types — that isn't actually part of the chunk's content.
        var propRuns = PartitionIntoContiguousRuns<PropertyDeclarationSyntax>(typeDecl.Members);
        var groups = new List<List<PropertyDeclarationSyntax>>();

        foreach (var run in propRuns)
        {
            var current = new List<PropertyDeclarationSyntax>();
            int currentChars = 0;

            foreach (var prop in run)
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
            if (current.Count > 0)
                groups.Add(current);
        }

        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var content = string.Join('\n', group.Select(p => p.ToFullString().Trim())).Trim();
            var firstSpan = group[0].GetLocation().GetLineSpan();
            var lastSpan = group[^1].GetLocation().GetLineSpan();
            int startLine = firstSpan.StartLinePosition.Line + 1;

            var fragmentSuffix = groups.Count > 1 ? $" [Fragment {g + 1}/{groups.Count}]" : string.Empty;
            var enriched = $"{header}\n// Properties{fragmentSuffix}\n\n{content}";

            yield return ChunkBuilder.Create(
                artifact,
                content: content,
                enrichedContent: enriched,
                type: ChunkType.Property,
                startLine: startLine,
                endLine: lastSpan.EndLinePosition.Line + 1,
                repositoryName: ctx.RepositoryName,
                language: SourceLanguage.CSharp,
                namespaceName: ctx.RootNamespace,
                className: typeDecl.Identifier.Text);
        }
    }

    /// <summary>
    /// Splits <paramref name="allMembers"/> into runs of <typeparamref name="TMember"/> that are
    /// physically adjacent in the source file — i.e. no other member (of any kind: method, field,
    /// nested type, etc.) sits between them. Grouping by token budget alone (as the property/field
    /// chunkers below do within each run) cannot be safely applied across a gap: two members of the
    /// same kind that are far apart in the file would otherwise be merged into one chunk whose
    /// reported StartLine/EndLine spans everything in between, even though that in-between code
    /// isn't part of the chunk's actual content.
    /// </summary>
    private static List<List<TMember>> PartitionIntoContiguousRuns<TMember>(
        SyntaxList<MemberDeclarationSyntax> allMembers)
        where TMember : MemberDeclarationSyntax
    {
        var runs = new List<List<TMember>>();
        List<TMember>? current = null;
        int lastIndex = -2; // never adjacent to i=0

        for (int i = 0; i < allMembers.Count; i++)
        {
            if (allMembers[i] is TMember typed)
            {
                if (current is not null && i == lastIndex + 1)
                    current.Add(typed);
                else
                    runs.Add(current = new List<TMember> { typed });

                lastIndex = i;
            }
        }

        return runs;
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
