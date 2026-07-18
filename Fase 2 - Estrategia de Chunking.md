Fase 2: Estrategia Avanzada de Chunking

### Parsing Semántico con Roslyn AST — Fragmentación Quirúrgica de Código

──────

## El Problema del Chunking Naïve

El enfoque más simple (dividir por N tokens fijos o por número de líneas) destruye el contexto semántico del código:

    ❌ CHUNKING POR LÍNEAS FIJAS (Naïve):
    ─────────────────────────────────────────
    Chunk A: "public class OrderService : IOrderService {\n  private readonly IOrderRep"
    Chunk B: "ository _repo;\n  public async Task<Order> GetOrderAsync(int id) {\n    ..."

    → El LLM recibe fragmentos que no representan unidades lógicas.
    → Un método cortado a la mitad = contexto inútil.
    → Sin información sobre qué clase o namespace pertenece el fragmento.

La estrategia correcta es respetar los límites naturales del lenguaje de programación usando el árbol de sintaxis abstracta (AST) como guía de corte.
──────

## Contrato de la Estrategia (Strategy Pattern)

    // RagEngine.Core/Abstractions/IChunkingStrategy.cs

    namespace RagEngine.Core.Abstractions;

    /// <summary>
    /// Contrato para fragmentar el contenido de un artefacto de código.
    /// Implementaciones distintas para C#, TypeScript, XAML y texto plano.
    /// El Strategy Pattern permite seleccionar el algoritmo en runtime
    /// basado en el SourceLanguage del RawArtifact.
    /// </summary>
    public interface IChunkingStrategy
    {
        /// <summary>El lenguaje de programación que esta estrategia maneja.</summary>
        SourceLanguage TargetLanguage { get; }

        /// <summary>
        /// Divide el contenido del artefacto en chunks semánticamente coherentes.
        /// Retorna IAsyncEnumerable para mantener el modelo de streaming del pipeline.
        /// </summary>
        IAsyncEnumerable<CodeChunk> ChunkAsync(
            RawArtifact artifact,
            string fileContent,
            ChunkingOptions options,
            CancellationToken cancellationToken = default);
    }

    // RagEngine.Core/Domain/CodeChunk.cs

    namespace RagEngine.Core.Domain;

    /// <summary>
    /// Un fragmento semántico de código, listo para ser vectorizado.
    /// Contiene el texto del fragmento MÁS el contexto estructural
    /// que se almacenará como payload en Qdrant.
    /// </summary>
    public sealed record CodeChunk(
        string Id,                          // GUID estable basado en FilePath + StartLine
        string Content,                     // Texto del fragmento (lo que se vectoriza)
        string EnrichedContent,             // Content + contexto inyectado (para mejor embedding)
        CodeChunkMetadata Metadata,
        ChunkType Type
    );

    public enum ChunkType
    {
        Method,             // Un método completo
        Class,              // Cabecera de clase + campos (sin métodos)
        Interface,          // Definición de interfaz completa
        Property,           // Una propiedad con getter/setter
        Constructor,        // Constructor de clase
        XamlControl,        // Un control XAML con sus propiedades
        XamlDataTemplate,   // Un DataTemplate completo
        SqlProcedure,       // Un stored procedure completo
        DocumentSection,    // Sección de Markdown (entre headers)
        PlainTextWindow     // Ventana deslizante para texto sin estructura
    }

    // RagEngine.Core/Domain/ChunkingOptions.cs

    namespace RagEngine.Core.Domain;

    public sealed record ChunkingOptions
    {
        /// <summary>Máximo de tokens por chunk. 512 para MiniLM.</summary>
        public int MaxTokensPerChunk { get; init; } = 512;

        /// <summary>
        /// Tokens de solapamiento entre chunks consecutivos del mismo archivo.
        /// Preserva contexto en límites de corte.
        /// </summary>
        public int OverlapTokens { get; init; } = 64;

        /// <summary>
        /// Si un método supera MaxTokensPerChunk, ¿se subdivide o se trunca?
        /// </summary>
        public OversizedChunkBehavior OversizedBehavior { get; init; }
            = OversizedChunkBehavior.SplitWithOverlap;

        /// <summary>
        /// Número de líneas de "contexto padre" a prefijar en cada chunk.
        /// Ej: para un método, inyectar la firma de la clase contenedora.
        /// </summary>
        public int ParentContextLines { get; init; } = 5;
    }

    public enum OversizedChunkBehavior
    {
        SplitWithOverlap,   // Dividir con ventana deslizante
        TruncateToMax,      // Cortar al límite (puede perder el cierre)
        IndexAsWholeFile    // Indexar el archivo completo (útil para archivos pequeños)
    }
    ──────

## Implementación Estrella: RoslynCSharpChunkingStrategy

Esta es la pieza técnica más sofisticada del sistema. Usa Microsoft.CodeAnalysis (Roslyn) para parsear el AST del código C# y extraer unidades semánticas reales.

    // RagEngine.Core/Infrastructure/Chunking/RoslynCSharpChunkingStrategy.cs

    namespace RagEngine.Core.Infrastructure.Chunking;

    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    public sealed class RoslynCSharpChunkingStrategy : IChunkingStrategy
    {
        private readonly ILogger<RoslynCSharpChunkingStrategy> _logger;

        public SourceLanguage TargetLanguage => SourceLanguage.CSharp;

        public async IAsyncEnumerable<CodeChunk> ChunkAsync(
            RawArtifact artifact,
            string fileContent,
            ChunkingOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // 1. Parsear el árbol de sintaxis
            var syntaxTree = CSharpSyntaxTree.ParseText(
                fileContent,
                cancellationToken: cancellationToken);

            var root = await syntaxTree.GetRootAsync(cancellationToken);

            // 2. Extraer contexto a nivel de archivo (namespace, usings)
            var fileContext = ExtractFileContext(root);

            // 3. Iterar sobre todas las declaraciones de tipo (clases, interfaces, records, enums)
            var typeDeclarations = root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>();

            foreach (var typeDecl in typeDeclarations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 4. Emitir un chunk para la cabecera de la clase
                yield return BuildClassHeaderChunk(
                    artifact, typeDecl, fileContext, options);

                // 5. Emitir un chunk por cada constructor
                foreach (var ctor in typeDecl.Members.OfType<ConstructorDeclarationSyntax>())
                {
                    yield return await BuildMemberChunkAsync(
                        artifact, ctor, typeDecl, fileContext,
                        ChunkType.Constructor, options, cancellationToken);
                }

                // 6. Emitir un chunk por cada método
                foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
                {
                    var chunks = await BuildMethodChunksAsync(
                        artifact, method, typeDecl, fileContext, options, cancellationToken);

                    foreach (var chunk in chunks)
                        yield return chunk;
                }

                // 7. Emitir chunk para propiedades agrupadas (evitar chunks de 3 líneas)
                var propertiesChunk = BuildGroupedPropertiesChunk(
                    artifact, typeDecl, fileContext, options);

                if (propertiesChunk is not null)
                    yield return propertiesChunk;
            }

            // 8. Fallback: si no se encontraron tipos (ej: archivos de top-level statements)
            if (!typeDeclarations.Any())
            {
                await foreach (var chunk in FallbackSlidingWindowAsync(
                    artifact, fileContent, fileContext, options, cancellationToken))
                {
                    yield return chunk;
                }
            }
        }

### El Corazón: Construcción de Chunks con Contexto Enriquecido

> 📌 **Actualización (julio 2026) — lección aprendida en el chunk de clase:** la primera
> implementación de `BuildClassHeaderChunk` tomaba "la primera línea no vacía" del
> `ToFullString()` del tipo. En clases decoradas con atributos (patrón dominante en corpus
> XAF/DevExpress, donde las **reglas de negocio viven en atributos**), esa primera línea es el
> atributo — un chunk que abarcaba 62 líneas quedaba reducido a `[DefaultClassOptions]`
> (21 chars) y el LLM recibía cáscaras vacías. La cabecera se reconstruye ahora **desde el
> AST**: listas de atributos completas + declaración (modificadores, identificador, base list)
> + campos. Regla general: al extraer texto de nodos Roslyn para chunks, componer desde las
> propiedades tipadas del nodo, nunca desde heurísticas sobre el texto plano.

        /// <summary>
        /// Construye el chunk para un método, incluyendo el "contexto padre" inyectado.
        /// El EnrichedContent es lo que realmente se vectoriza: combina el método
        /// con información de su clase contenedora para embeddings más precisos.
        /// </summary>
        private async Task<IEnumerable<CodeChunk>> BuildMethodChunksAsync(
            RawArtifact artifact,
            MethodDeclarationSyntax method,
            TypeDeclarationSyntax containingType,
            FileContext fileContext,
            ChunkingOptions options,
            CancellationToken ct)
        {
            var methodText = method.ToFullString();
            var lineSpan = method.GetLocation().GetLineSpan();

            // Construir el "header de contexto" que se prepende al chunk
            // Esto es CRÍTICO para la calidad del embedding:
            var contextHeader = BuildContextHeader(fileContext, containingType, method);

            /*
             * contextHeader ejemplo:
             * ─────────────────────────────────────────────────────────
             * // File: src/Services/OrderService.cs
             * // Namespace: MyCompany.ERP.Services
             * // Class: OrderService : IOrderService
             * // Method: GetOrderAsync(int id) → Task<Order>
             * ─────────────────────────────────────────────────────────
             */

            var enrichedContent = $"{contextHeader}\n\n{methodText}";

            // ¿Cabe en el límite de tokens?
            var tokenCount = EstimateTokenCount(enrichedContent);

            if (tokenCount <= options.MaxTokensPerChunk)
            {
                // Caso feliz: el método entero cabe en un chunk
                return [CreateChunk(
                    artifact, methodText, enrichedContent,
                    method, containingType, fileContext,
                    ChunkType.Method, (int)lineSpan.StartLinePosition.Line,
                    (int)lineSpan.EndLinePosition.Line)];
            }

            // Caso: método demasiado grande → estrategia de subdivisión
            return options.OversizedBehavior switch
            {
                OversizedChunkBehavior.SplitWithOverlap =>
                    SplitOversizedMethod(artifact, method, containingType,
                        fileContext, contextHeader, options),
                OversizedChunkBehavior.TruncateToMax =>
                    [TruncateMethod(artifact, methodText, enrichedContent,
                        method, containingType, fileContext, options)],
                _ => [CreateChunk(artifact, methodText, enrichedContent,
                        method, containingType, fileContext,
                        ChunkType.Method,
                        (int)lineSpan.StartLinePosition.Line,
                        (int)lineSpan.EndLinePosition.Line)]
            };
        }

        /// <summary>
        /// Para métodos que exceden el límite, divide por bloques lógicos internos:
        /// statement groups, try/catch blocks, etc. Con solapamiento configurable.
        /// </summary>
        private IEnumerable<CodeChunk> SplitOversizedMethod(
            RawArtifact artifact,
            MethodDeclarationSyntax method,
            TypeDeclarationSyntax containingType,
            FileContext fileContext,
            string contextHeader,
            ChunkingOptions options)
        {
            var lines = method.ToFullString().Split('\n');
            var chunks = new List<CodeChunk>();
            var lineSpan = method.GetLocation().GetLineSpan();
            int startOffset = (int)lineSpan.StartLinePosition.Line;

            // Ventana deslizante con solapamiento por líneas
            int windowSize = EstimateLineCountForTokens(options.MaxTokensPerChunk);
            int overlapLines = EstimateLineCountForTokens(options.OverlapTokens);
            int step = windowSize - overlapLines;

            for (int i = 0; i < lines.Length; i += step)
            {
                var windowLines = lines.Skip(i).Take(windowSize).ToArray();
                var windowText = string.Join('\n', windowLines);
                var enriched = $"{contextHeader}\n// [Fragment {chunks.Count + 1}]\n\n{windowText}";

                chunks.Add(CreateChunkFromText(
                    artifact, windowText, enriched,
                    containingType, fileContext,
                    ChunkType.Method,
                    startOffset + i,
                    startOffset + i + windowLines.Length));
            }

            return chunks;
        }

### Extracción del Contexto Estructural (FileContext)

        private sealed record FileContext(
            string? RootNamespace,
            IReadOnlyList<string> UsingDirectives,
            string RelativeFilePath,
            string RepositoryName
        );

        private static FileContext ExtractFileContext(SyntaxNode root)
        {
            // Extraer el namespace raíz (soporta tanto namespace tradicional como file-scoped)
            var namespaceName = root.DescendantNodes()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault()
                ?.Name.ToString();

            // Extraer usings para detectar dependencias del archivo
            var usings = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Select(u => u.Name?.ToString() ?? string.Empty)
                .Where(u => !string.IsNullOrEmpty(u))
                .ToList();

            return new FileContext(namespaceName, usings, string.Empty, string.Empty);
        }

        private static string BuildContextHeader(
            FileContext ctx,
            TypeDeclarationSyntax typeDecl,
            MethodDeclarationSyntax? method = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// Repository: {ctx.RepositoryName}");
            sb.AppendLine($"// File: {ctx.RelativeFilePath}");
            if (ctx.RootNamespace is not null)
                sb.AppendLine($"// Namespace: {ctx.RootNamespace}");

            // Tipo de declaración con interfaces implementadas
            var typeKind = typeDecl switch
            {
                ClassDeclarationSyntax => "class",
                InterfaceDeclarationSyntax => "interface",
                RecordDeclarationSyntax => "record",
                StructDeclarationSyntax => "struct",
                _ => "type"
            };

            var baseList = typeDecl.BaseList?.ToString() ?? string.Empty;
            sb.AppendLine($"// {typeKind}: {typeDecl.Identifier.Text}{baseList}");

            if (method is not null)
            {
                var returnType = method.ReturnType.ToString();
                var parameters = method.ParameterList.ToString();
                sb.AppendLine($"// method: {method.Identifier.Text}{parameters} → {returnType}");

                // ¿El método tiene atributos importantes? (HttpGet, Authorize, etc.)
                var attrs = method.AttributeLists
                    .SelectMany(al => al.Attributes)
                    .Select(a => $"[{a.Name}]");
                if (attrs.Any())
                    sb.AppendLine($"// attributes: {string.Join(", ", attrs)}");
            }

            return sb.ToString().TrimEnd();
        }
    ──────

## TypeScriptChunkingStrategy — Soporte Multi-Lenguaje

Para TypeScript, Roslyn no aplica. Se usa la librería Tree-sitter (portada a .NET) o, en alternativa práctica, parsing con reglas léxicas basadas en patrones de indentación y tokens.

    // RagEngine.Core/Infrastructure/Chunking/TypeScriptChunkingStrategy.cs

    public sealed class TypeScriptChunkingStrategy : IChunkingStrategy
    {
        public SourceLanguage TargetLanguage => SourceLanguage.TypeScript;

        // Patrones que identifican límites naturales en TypeScript
        private static readonly Regex[] ChunkBoundaryPatterns =
        [
            // Funciones exportadas
            new(@"^export\s+(async\s+)?function\s+\w+", RegexOptions.Multiline | RegexOptions.Compiled),
            // Clases completas
            new(@"^export\s+(abstract\s+)?class\s+\w+", RegexOptions.Multiline | RegexOptions.Compiled),
            // Arrow functions asignadas a const/export
            new(@"^export\s+const\s+\w+\s*=\s*(async\s+)?\(", RegexOptions.Multiline | RegexOptions.Compiled),
            // Interfaces y types
            new(@"^export\s+(interface|type)\s+\w+", RegexOptions.Multiline | RegexOptions.Compiled),
            // Decoradores Angular/NestJS (componentes, servicios)
            new(@"^@(Component|Injectable|NgModule|Controller|Module)\(", RegexOptions.Multiline | RegexOptions.Compiled),
        ];

        public async IAsyncEnumerable<CodeChunk> ChunkAsync(
            RawArtifact artifact,
            string fileContent,
            ChunkingOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Encontrar todos los puntos de corte naturales
            var boundaryLines = FindBoundaryLines(fileContent);
            var segments = SliceByBoundaries(fileContent, boundaryLines);

            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var tokenCount = EstimateTokenCount(segment.Content);
                if (tokenCount <= options.MaxTokensPerChunk)
                {
                    yield return BuildTypeScriptChunk(artifact, segment, options);
                }
                else
                {
                    // Subdividir segmento grande por ventana deslizante
                    await foreach (var subChunk in SlidingWindowAsync(
                        artifact, segment, options, cancellationToken))
                    {
                        yield return subChunk;
                    }
                }
            }
        }
    }
    ──────

## XamlChunkingStrategy — Fragmentación de Interfaces de Usuario

XAML es XML estructurado; aquí usamos System.Xml.Linq (XDocument) para navegar el árbol DOM.

    // RagEngine.Core/Infrastructure/Chunking/XamlChunkingStrategy.cs

    public sealed class XamlChunkingStrategy : IChunkingStrategy
    {
        public SourceLanguage TargetLanguage => SourceLanguage.Xaml;

        // Elementos XAML que constituyen unidades semánticas naturales
        private static readonly HashSet<string> SemanticRootElements = new(StringComparer.OrdinalIgnoreCase)
        {
            "UserControl", "Window", "Page", "DataTemplate",
            "ControlTemplate", "ResourceDictionary", "Style",
            "Grid", "StackPanel", "ListView", "DataGrid"
        };

        public async IAsyncEnumerable<CodeChunk> ChunkAsync(
            RawArtifact artifact,
            string fileContent,
            ChunkingOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(fileContent, LoadOptions.SetLineInfo);
            }
            catch (XmlException ex)
            {
                // XAML malformado: fallback a ventana deslizante de texto plano
                _logger.LogWarning("XAML inválido en {Path}: {Error}", artifact.RelativePath, ex.Message);
                await foreach (var chunk in FallbackSlidingWindowAsync(artifact, fileContent, options, cancellationToken))
                    yield return chunk;
                yield break;
            }

            // Buscar elementos semánticos de primer nivel
            var semanticElements = doc.Descendants()
                .Where(el => SemanticRootElements.Contains(el.Name.LocalName))
                .ToList();

            foreach (var element in semanticElements)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var elementXml = element.ToString();
                var lineInfo = (IXmlLineInfo)element;

                // Determinar el tipo de chunk XAML
                var chunkType = element.Name.LocalName switch
                {
                    "DataTemplate" => ChunkType.XamlDataTemplate,
                    _ => ChunkType.XamlControl
                };

                // Extraer x:Name o x:Key del elemento para el contexto
                var elementName = element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name")?.Value
                               ?? element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Key")?.Value
                               ?? element.Name.LocalName;

                // Header de contexto para embeddings de UI
                var contextHeader =
                    $"// XAML Control: {element.Name.LocalName}\n" +
                    $"// Name/Key: {elementName}\n" +
                    $"// File: {artifact.RelativePath}\n" +
                    $"// Bindings: {ExtractBindingsSummary(element)}";

                var enrichedContent = $"{contextHeader}\n\n{elementXml}";

                yield return new CodeChunk(
                    Id: GenerateStableId(artifact.AbsolutePath, lineInfo.LineNumber),
                    Content: elementXml,
                    EnrichedContent: enrichedContent,
                    Metadata: new CodeChunkMetadata(
                        FilePath: artifact.RelativePath,
                        Language: SourceLanguage.Xaml,
                        Namespace: null,
                        ClassName: elementName,
                        MethodName: null,
                        StartLine: lineInfo.LineNumber,
                        EndLine: lineInfo.LineNumber + elementXml.Count(c => c == '\n'),
                        LastModified: artifact.LastModified,
                        RepositoryName: string.Empty
                    ),
                    Type: chunkType
                );
            }
        }

        /// <summary>
        /// Extrae un resumen de los bindings del control para enriquecer el embedding.
        /// Un LLM que busca "binding de precio" necesita que el chunk mencione '{Binding Price}'.
        /// </summary>
        private static string ExtractBindingsSummary(XElement element)
        {
            var bindingPattern = new Regex(@"\{Binding\s+([^}]+)\}", RegexOptions.Compiled);
            var bindings = bindingPattern
                .Matches(element.ToString())
                .Select(m => m.Groups[1].Value.Split(',')[0].Trim())
                .Distinct()
                .Take(10); // Máximo 10 bindings en el resumen

            return string.Join(", ", bindings);
        }
    }
    ──────

## El Router de Estrategias: ChunkingStrategyRouter

    // RagEngine.Core/Infrastructure/Chunking/ChunkingStrategyRouter.cs

    namespace RagEngine.Core.Infrastructure.Chunking;

    /// <summary>
    /// Selecciona la estrategia de chunking correcta según el lenguaje del artefacto.
    /// Registrado como Singleton; las estrategias individuales también son Singletons.
    /// </summary>
    public sealed class ChunkingStrategyRouter
    {
        private readonly IReadOnlyDictionary<SourceLanguage, IChunkingStrategy> _strategies;
        private readonly PlainTextChunkingStrategy _fallbackStrategy;

        public ChunkingStrategyRouter(IEnumerable<IChunkingStrategy> strategies,
                                       PlainTextChunkingStrategy fallback)
        {
            _strategies = strategies.ToDictionary(s => s.TargetLanguage);
            _fallbackStrategy = fallback;
        }

        public IChunkingStrategy GetStrategy(SourceLanguage language)
            => _strategies.GetValueOrDefault(language) ?? _fallbackStrategy;
    }
    ──────

## Estimación de Tokens: TokenEstimator

Una pieza crítica pero frecuentemente olvidada: necesitamos estimar tokens sin ejecutar el tokenizador completo (muy costoso si se llama por cada línea).

    // RagEngine.Core/Infrastructure/TokenEstimator.cs

    public static class TokenEstimator
    {
        // Heurística empírica para código fuente: ~3.5 caracteres por token
        // (más conservadora que el promedio de texto en inglés ~4 chars/token)
        private const double CharsPerToken = 3.5;

        /// <summary>
        /// Estimación rápida O(n) basada en longitud de caracteres.
        /// Suficiente para decisiones de chunking; no para conteo exacto.
        /// </summary>
        public static int Estimate(string text)
            => (int)Math.Ceiling(text.Length / CharsPerToken);

        /// <summary>
        /// Estimación por número de líneas para cálculos de ventana deslizante.
        /// Asume ~15 tokens por línea de código (promedio empírico para C#).
        /// </summary>
        public static int EstimateLineCount(int maxTokens, int avgTokensPerLine = 15)
            => maxTokens / avgTokensPerLine;
    }
    ──────

## Registro de Estrategias en DI

    // Añadir al ServiceCollectionExtensions.cs

    services.AddSingleton<IChunkingStrategy, RoslynCSharpChunkingStrategy>();
    services.AddSingleton<IChunkingStrategy, TypeScriptChunkingStrategy>();
    services.AddSingleton<IChunkingStrategy, XamlChunkingStrategy>();
    services.AddSingleton<IChunkingStrategy, SqlChunkingStrategy>();
    services.AddSingleton<PlainTextChunkingStrategy>();    // Fallback explícito
    services.AddSingleton<ChunkingStrategyRouter>();
    ──────

## Comparativa de Estrategias

Lenguaje │ Motor de Parsing │ Unidad de Corte │ Enriquecimiento
───────────────────────────────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────────────
C# │ Roslyn ( CSharpSyntaxTree ) │ Método / Clase / Constructor │ Namespace + Clase + Firma + Atributos
TypeScript │ Regex léxico + indentación │ export function / class / interface │ Módulo + Tipo de exportación
XAML │ XDocument (DOM XML) │ Elemento raíz semántico │ Control name + resumen de bindings
SQL │ Regex + delimitadores GO │ Stored Procedure / View / Function │ Schema + nombre de objeto
Markdown │ Split por ## headers │ Sección (H2/H3) │ Título del documento + Breadcrumb
Texto plano │ Ventana deslizante │ N líneas con solapamiento │ Nombre de archivo + posición
──────

> 📌 **Criterio transversal (julio 2026) — contenido mínimo indexable:** independientemente de
> la estrategia, el pipeline descarta chunks cuyo contenido (sin encabezado) mida **< 60
> caracteres**: constructores boilerplate de una línea, interfaces marcador, cáscaras
> `public static class X`. No contienen información respondible, pero su `EnrichedContent`
> —casi puro encabezado con el nombre de la entidad— produce embeddings artificialmente
> cercanos a cualquier consulta que mencione esa entidad, contaminando el ranking híbrido.
> En un corpus real de 21k chunks el filtro eliminó ~1,100 cáscaras (5%).
