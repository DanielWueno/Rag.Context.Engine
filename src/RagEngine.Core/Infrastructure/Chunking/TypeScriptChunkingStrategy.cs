using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Utilities;

namespace RagEngine.Core.Infrastructure.Chunking;

/// <summary>
/// Estrategia semántica para archivos TypeScript y JavaScript.
///
/// ALGORITMO: Mini-Lexer de 3 Estados + Contador de Llaves
/// ─────────────────────────────────────────────────────────
/// El parser recorre el archivo caracter a caracter (o línea a línea) 
/// manteniendo un estado mínimo en memoria, sin construir un AST completo.
///
/// Estados del Lexer:
///   Normal          → procesamiento estándar; detecta firmas y llaves.
///   InString        → dentro de un string ('...', "...", `...`); las llaves se ignoran.
///   InBlockComment  → dentro de un bloque /* ... */; todo el contenido se ignora.
///
/// Regla de captura:
///   1. Se detecta una "firma semántica" (class, function, método de clase, etc.).
///   2. Se activa la captura y se empieza a contar llaves: depth++ al abrir {, depth-- al cerrar }.
///   3. Cuando depth vuelve a 0, el bloque está completo → se emite un CodeChunk.
///   4. Los bloques "sucios" (imports, exports, constantes sueltas) se agrupan
///      en un chunk de tipo PlainTextWindow al final del archivo.
/// </summary>
public sealed partial class TypeScriptChunkingStrategy : IChunkingStrategy
{
    private readonly ILogger<TypeScriptChunkingStrategy> _logger;

    // ── Firmas de bloque semántico (detectadas ANTES de abrir '{') ────────────

    // class Foo | abstract class Foo | export class Foo | export default class
    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:abstract\s+)?class\s+(\w+)")]
    private static partial Regex ClassSignatureRegex();

    // function foo() | async function foo() | export function foo() | export async function foo()
    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*(\w+)\s*[(<]")]
    private static partial Regex FunctionSignatureRegex();

    // Método dentro de clase: foo() | async foo() | private async foo() | override foo()
    // Incluye constructores y métodos con modificadores TS (public/private/protected/static/override/abstract)
    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|static|override|abstract|async|readonly)\s+)*(\w+)\s*[(<][^=]*\)\s*(?::\s*[\w<>\[\]|&., ]+)?\s*\{?\s*$")]
    private static partial Regex MethodSignatureRegex();

    // const foo = () => { | const foo = async () => {
    [GeneratedRegex(@"^\s*(?:export\s+)?(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s+)?\(")]
    private static partial Regex ArrowFunctionSignatureRegex();

    // Decoradores NestJS/Angular: @Component, @Injectable, @Controller...
    [GeneratedRegex(@"^\s*@(\w+)")]
    private static partial Regex DecoratorRegex();

    // ─────────────────────────────────────────────────────────────────────────

    public TypeScriptChunkingStrategy(ILogger<TypeScriptChunkingStrategy> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    /// Esta estrategia cubre tanto TypeScript como JavaScript ya que el algoritmo
    /// de parser es compatible con ambos dialectos.
    public SourceLanguage TargetLanguage => SourceLanguage.TypeScript;

    /// <inheritdoc />
    public async IAsyncEnumerable<CodeChunk> ChunkAsync(
        RawArtifact artifact,
        string fileContent,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Yield para liberar el hilo y respetar el modelo de streaming asíncrono
        await Task.Yield();

        var lines = fileContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        // Estado del parser
        var lexerState   = LexerState.Normal;
        char stringDelim = '\0'; // El delimitador del string activo: ', " o `

        // Estado del bloque activo en captura
        var  capturedLines = new List<string>();
        int  captureStart  = -1;
        int  braceDepth    = 0;    // Contador de llaves anidadas
        bool isCapturing   = false;
        var  currentSig    = BlockSignature.Empty;

        // Bloques "sueltos" (imports, exports, constantes sin cuerpo de función)
        var looseLines     = new List<string>();
        int looseStart     = 1;

        // Decorador pendiente: guardamos el nombre del último @Decorador visto
        // para enriquecerlo al siguiente bloque class/function que aparezca.
        string? pendingDecorator = null;

        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string line     = lines[lineIdx];
            int    lineNum  = lineIdx + 1; // 1-indexed

            if (isCapturing)
            {
                // ── MODO CAPTURA: acumulamos líneas y contamos llaves ──────────
                capturedLines.Add(line);

                // Recorremos la línea caracter a caracter para contar
                // llaves con precisión, respetando strings y comentarios.
                for (int ci = 0; ci < line.Length; ci++)
                {
                    char c = line[ci];

                    switch (lexerState)
                    {
                        case LexerState.Normal:
                            // ── Detección de comentario de bloque ──
                            if (c == '/' && ci + 1 < line.Length && line[ci + 1] == '*')
                            {
                                lexerState = LexerState.InBlockComment;
                                ci++;        // Saltar el '*'
                                break;
                            }

                            // ── Detección de comentario de línea (// ...) ──
                            // Todo lo que sigue en esta línea es un comentario; salir del loop de chars.
                            if (c == '/' && ci + 1 < line.Length && line[ci + 1] == '/')
                                goto NextLine;

                            // ── Detección de inicio de string ──
                            if (c is '\'' or '"' or '`')
                            {
                                stringDelim = c;
                                lexerState  = LexerState.InString;
                                break;
                            }

                            // ── Contar llaves (solo en estado Normal) ──
                            if (c == '{')
                            {
                                braceDepth++;
                            }
                            else if (c == '}')
                            {
                                braceDepth--;

                                // depth == 0 significa que el bloque raíz cerró
                                if (braceDepth == 0)
                                {
                                    // Emitir el chunk completo
                                    foreach (var chunk in FlushBlock(
                                        artifact, currentSig, capturedLines,
                                        captureStart, lineNum, options))
                                    {
                                        yield return chunk;
                                    }

                                    // Resetear estado de captura
                                    isCapturing   = false;
                                    capturedLines = [];
                                    currentSig    = BlockSignature.Empty;
                                    captureStart  = -1;
                                    braceDepth    = 0;
                                }
                            }
                            break;

                        case LexerState.InString:
                            // ── Dentro de un string: esperar el delimitador de cierre ──
                            // Ignorar escape sequences (\' \" \` \\ etc.)
                            if (c == '\\')
                            {
                                ci++; // Saltar el carácter escapado
                                break;
                            }

                            // Template literal con expresión embebida ${...}
                            // La expresión puede contener llaves propias, pero para nuestro
                            // propósito (contar el bloque raíz de la función) es suficiente
                            // ignorarlas aquí y solo rastrear el cierre del backtick.
                            if (c == stringDelim)
                            {
                                lexerState  = LexerState.Normal;
                                stringDelim = '\0';
                            }
                            break;

                        case LexerState.InBlockComment:
                            // ── Dentro de comentario de bloque: esperar '*/' ──
                            if (c == '*' && ci + 1 < line.Length && line[ci + 1] == '/')
                            {
                                lexerState = LexerState.Normal;
                                ci++;      // Saltar el '/'
                            }
                            break;
                    }
                }

                NextLine:;
                // El estado InString NO se resetea al final de la línea para strings multilinea (`...`)
                // El estado InBlockComment tampoco, ya que puede abarcar múltiples líneas.
                // El estado Normal sí persiste naturalmente.
                continue;
            }

            // ── MODO ESCANEO: buscamos la próxima firma semántica ─────────────

            // Detectar decoradores (@Component, @Injectable, etc.) para anotarlos
            // al siguiente bloque que aparezca.
            var decoratorMatch = DecoratorRegex().Match(line);
            if (decoratorMatch.Success)
            {
                pendingDecorator = decoratorMatch.Groups[1].Value;
                looseLines.Add(line); // El decorador va en los loose hasta que empiece el bloque
                continue;
            }

            // Intentar detectar una firma de bloque en la línea actual.
            var sig = TryExtractSignature(line, pendingDecorator);

            if (sig != BlockSignature.Empty)
            {
                // Antes de empezar a capturar este bloque, fluir el buffer de líneas sueltas
                if (looseLines.Count > 0)
                {
                    foreach (var looseChunk in FlushLooseLines(artifact, looseLines, looseStart, lineNum - 1, options))
                        yield return looseChunk;

                    looseLines = [];
                    looseStart = lineNum;
                }

                // Activar captura
                isCapturing       = true;
                captureStart      = lineNum;
                currentSig        = sig;
                capturedLines     = [line];
                pendingDecorator  = null;

                // Contamos las llaves en la línea de la firma misma
                // (la apertura '{' puede estar en la misma línea que la firma)
                foreach (char c in line)
                {
                    if      (c == '{') braceDepth++;
                    else if (c == '}') braceDepth--;
                }

                // Si el bloque cerró en la misma línea (one-liner), emitirlo ya
                if (braceDepth == 0 && capturedLines.Count > 0)
                {
                    foreach (var chunk in FlushBlock(artifact, currentSig, capturedLines, captureStart, lineNum, options))
                        yield return chunk;

                    isCapturing   = false;
                    capturedLines = [];
                    currentSig    = BlockSignature.Empty;
                    captureStart  = -1;
                    braceDepth    = 0;
                }
            }
            else
            {
                // Línea suelta: import, export const, type alias, interface simple, etc.
                if (looseLines.Count == 0) looseStart = lineNum;
                looseLines.Add(line);
                pendingDecorator = null; // Si había un decorador sin clase/función, lo descartamos
            }
        }

        // ── Flush de los restos ───────────────────────────────────────────────

        // Si hay un bloque de captura sin cerrar (archivo truncado o error de parseo)
        if (isCapturing && capturedLines.Count > 0)
        {
            _logger.LogWarning(
                "TypeScript parser: bloque sin cerrar en {File} desde L{Start}. Se indexará como fragmento.",
                artifact.RelativePath, captureStart);

            foreach (var chunk in FlushBlock(artifact, currentSig, capturedLines, captureStart, lines.Length, options))
                yield return chunk;
        }

        // Líneas sueltas finales
        if (looseLines.Count > 0)
        {
            foreach (var chunk in FlushLooseLines(artifact, looseLines, looseStart, lines.Length, options))
                yield return chunk;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DETECCIÓN DE FIRMAS SEMÁNTICAS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Intenta detectar si una línea representa el inicio de un bloque semántico relevante.
    /// </summary>
    /// <param name="line">La línea a analizar.</param>
    /// <param name="pendingDecorator">Decorador TS previo, si lo hay.</param>
    /// <returns>Una BlockSignature con el nombre y tipo, o BlockSignature.Empty si no hay match.</returns>
    private static BlockSignature TryExtractSignature(string line, string? pendingDecorator)
    {
        // La línea debe contener una apertura de bloque '{' para ser relevante,
        // O ser la declaración de una clase/función que abrirá en la siguiente línea.
        // Aceptamos ambos casos para manejar estilos de llaves Allman y K&R.

        var classMatch = ClassSignatureRegex().Match(line);
        if (classMatch.Success)
        {
            var name = classMatch.Groups[1].Value;
            if (!string.IsNullOrEmpty(pendingDecorator))
                name = $"@{pendingDecorator} {name}";
            return new BlockSignature(name, BlockType.Class);
        }

        var fnMatch = FunctionSignatureRegex().Match(line);
        if (fnMatch.Success)
            return new BlockSignature(fnMatch.Groups[1].Value, BlockType.Function);

        var arrowMatch = ArrowFunctionSignatureRegex().Match(line);
        if (arrowMatch.Success && line.Contains("=>"))
            return new BlockSignature(arrowMatch.Groups[1].Value, BlockType.ArrowFunction);

        // El método regex es el más permisivo, solo lo aplicamos si la línea
        // tiene '{' al final o parece una firma de método válida.
        if (line.Contains('{') || line.TrimEnd().EndsWith(')'))
        {
            var methodMatch = MethodSignatureRegex().Match(line);
            if (methodMatch.Success)
            {
                var name = methodMatch.Groups[1].Value;
                // Evitar falsos positivos con keywords de control de flujo
                if (!IsControlFlowKeyword(name))
                    return new BlockSignature(name, BlockType.Method);
            }
        }

        return BlockSignature.Empty;
    }

    /// <summary>
    /// Devuelve true si el nombre es una keyword de control de flujo, para evitar
    /// falsos positivos del MethodSignatureRegex con if(), for(), while(), etc.
    /// </summary>
    private static bool IsControlFlowKeyword(string name) =>
        name is "if" or "else" or "for" or "while" or "do" or "switch"
              or "try" or "catch" or "finally" or "with" or "return";

    // ─────────────────────────────────────────────────────────────────────────
    // EMISIÓN DE CHUNKS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Emite uno o más chunks a partir de un bloque capturado.
    /// Si el bloque supera MaxTokensPerChunk, aplica el fallback secundario
    /// particionando por líneas vacías y manteniendo la firma en cada sub-chunk.
    /// </summary>
    private IEnumerable<CodeChunk> FlushBlock(
        RawArtifact artifact,
        BlockSignature sig,
        List<string> capturedLines,
        int startLine,
        int endLine,
        ChunkingOptions options)
    {
        string header  = BuildContextHeader(sig, artifact, options);
        string rawBody = string.Join("\n", capturedLines);
        string full    = $"{header}\n\n{rawBody}";

        if (TokenEstimator.FitsInBudget(full, options.MaxTokensPerChunk))
        {
            yield return CreateChunk(artifact, sig, full, rawBody, startLine, endLine, options);
            yield break;
        }

        // ── Fallback secundario: partir por párrafos (líneas vacías) ──────────
        _logger.LogDebug(
            "Block '{Name}' en {File} excede {Max} tokens — particionando por párrafos.",
            sig.Name, artifact.RelativePath, options.MaxTokensPerChunk);

        var paragraphs    = SplitIntoParagraphs(capturedLines);
        var currentParas  = new List<string>();
        int currentStart  = startLine;
        int currentEnd    = startLine;
        int relLine       = startLine;

        foreach (var (paraLines, paraLen) in paragraphs)
        {
            string paraText  = string.Join("\n", paraLines);
            string testContent = $"{header}\n\n{string.Join("\n\n", currentParas.Append(paraText))}";

            if (TokenEstimator.Estimate(testContent) > options.MaxTokensPerChunk && currentParas.Count > 0)
            {
                string flushBody    = string.Join("\n\n", currentParas);
                string flushContent = $"{header}\n\n{flushBody}";
                yield return CreateChunk(artifact, sig, flushContent, flushBody, currentStart, currentEnd, options);

                currentParas  = [];
                currentStart  = relLine;
            }

            currentParas.Add(paraText);
            currentEnd  = relLine + paraLen - 1;
            relLine    += paraLen;
        }

        if (currentParas.Count > 0)
        {
            string flushBody    = string.Join("\n\n", currentParas);
            string flushContent = $"{header}\n\n{flushBody}";
            yield return CreateChunk(artifact, sig, flushContent, flushBody, currentStart, endLine, options);
        }
    }

    /// <summary>
    /// Emite chunks para líneas "sueltas" (imports, type aliases, constantes simples).
    /// Usa la misma lógica de agrupación por párrafos del fallback.
    /// </summary>
    private IEnumerable<CodeChunk> FlushLooseLines(
        RawArtifact artifact,
        List<string> looseLines,
        int startLine,
        int endLine,
        ChunkingOptions options)
    {
        // Descartar grupos que sean únicamente líneas en blanco para mantener
        // el índice de Qdrant limpio sin chunks vacíos sin valor semántico.
        var meaningful = looseLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (meaningful.Count == 0) yield break;

        var looseSig = new BlockSignature("(imports / declaraciones)", BlockType.Loose);
        foreach (var chunk in FlushBlock(artifact, looseSig, meaningful, startLine, endLine, options))
            yield return chunk;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HELPERS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Construye el encabezado de contexto semántico que se inyecta al inicio del chunk,
    /// aportando al modelo de embeddings la clave estructural del fragmento.
    /// Ejemplo: "// File: src/services/AuthService.ts | Class: AuthService | Method: login"
    /// </summary>
    private static string BuildContextHeader(BlockSignature sig, RawArtifact artifact, ChunkingOptions options)
    {
        var parts = new List<string>
        {
            $"// Repository: {options.RepositoryName}",
            $"// File: {artifact.RelativePath}"
        };

        parts.Add(sig.Type switch
        {
            BlockType.Class        => $"// Class: {sig.Name}",
            BlockType.Function     => $"// Function: {sig.Name}",
            BlockType.ArrowFunction => $"// Arrow Function: {sig.Name}",
            BlockType.Method       => $"// Method: {sig.Name}",
            _                      => $"// Section: {sig.Name}"
        });

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Determina el ChunkType a partir del tipo de bloque capturado.
    /// </summary>
    private static ChunkType MapToChunkType(BlockType blockType) => blockType switch
    {
        BlockType.Class         => ChunkType.Class,
        BlockType.Function      => ChunkType.Method,
        BlockType.ArrowFunction => ChunkType.Method,
        BlockType.Method        => ChunkType.Method,
        _                       => ChunkType.PlainTextWindow
    };

    /// <summary>
    /// Crea un CodeChunk con ID determinístico (idempotente), listo para Qdrant.
    /// El EnrichedContent incluye el encabezado de contexto para mayor precisión semántica.
    /// </summary>
    private static CodeChunk CreateChunk(
        RawArtifact artifact,
        BlockSignature sig,
        string enrichedContent,
        string rawContent,
        int startLine,
        int endLine,
        ChunkingOptions options)
    {
        string hash = ContentHasher.Compute(rawContent);
        Guid   id   = DeterministicGuid.CreateForChunk(artifact.AbsolutePath, startLine, hash);

        // Extraer nombre de clase vs. método para los metadatos
        string? className  = sig.Type == BlockType.Class ? sig.Name : null;
        string? methodName = sig.Type is BlockType.Function or BlockType.ArrowFunction or BlockType.Method
            ? sig.Name : null;

        return new CodeChunk
        {
            Id              = id,
            Content         = rawContent,
            EnrichedContent = enrichedContent,
            ContentHash     = hash,
            Type            = MapToChunkType(sig.Type),
            Metadata        = new CodeChunkMetadata(
                FilePath:         artifact.AbsolutePath,
                RelativeFilePath: artifact.RelativePath,
                Language:         artifact.Language,
                Namespace:        null,  // TS no tiene namespaces C#; se podría extraer el módulo en el futuro
                ClassName:        className,
                MethodName:       methodName,
                StartLine:        startLine,
                EndLine:          endLine,
                LastModified:     artifact.LastModified,
                RepositoryName:   options.RepositoryName
            )
        };
    }

    /// <summary>
    /// Divide una lista de líneas en "párrafos" separados por líneas vacías.
    /// Cada párrafo se devuelve junto con su longitud (número de líneas, incluyendo el separador).
    /// </summary>
    private static List<(List<string> Lines, int TotalLen)> SplitIntoParagraphs(List<string> lines)
    {
        var result  = new List<(List<string>, int)>();
        var current = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0)
                {
                    result.Add((current, current.Count + 1)); // +1 por la línea vacía
                    current = [];
                }
            }
            else
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
            result.Add((current, current.Count));

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TIPOS DE SOPORTE INTERNOS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Estado del mini-lexer para el conteo de llaves con precisión.
    /// </summary>
    private enum LexerState
    {
        /// <summary>Procesamiento estándar: las llaves cuentan.</summary>
        Normal,
        /// <summary>Dentro de un string literal ('...', "...", `...`): las llaves se ignoran.</summary>
        InString,
        /// <summary>Dentro de un comentario de bloque /* ... */: todo se ignora.</summary>
        InBlockComment
    }

    /// <summary>Clasificación del tipo de bloque capturado.</summary>
    private enum BlockType { Class, Function, ArrowFunction, Method, Loose }

    /// <summary>
    /// Firma semántica de un bloque detectado: su nombre y tipo.
    /// Se usa como un simple value object para transportar el contexto entre métodos.
    /// </summary>
    private readonly record struct BlockSignature(string Name, BlockType Type)
    {
        /// <summary>Valor centinela que indica "ninguna firma detectada".</summary>
        public static readonly BlockSignature Empty = new(string.Empty, BlockType.Loose);

        public bool IsEmpty => string.IsNullOrEmpty(Name);
    }
}
