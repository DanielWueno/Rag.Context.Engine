using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RagEngine.Core.Infrastructure.Chunking;

/// <summary>
/// Extrae símbolos definidos y consumidos de un chunk, para el ítem 5.c del plan.
///
/// El árbol de C# es puramente SINTÁCTICO (<see cref="CSharpSyntaxTree.ParseText"/>
/// sin <see cref="Microsoft.CodeAnalysis.Compilation"/>): no hay resolución de tipos,
/// todo es por nombre de identificador. Eso alcanza para el objetivo — un índice de
/// "qué símbolo aparece en qué chunk", no un grafo de llamadas resuelto — y trae una
/// ventaja gratis: los comentarios son trivia (no nodos del árbol) y los literales de
/// string son tokens/`LiteralExpressionSyntax` (no `IdentifierNameSyntax`), así que
/// nunca entran en `Consumed` sin necesitar lógica extra para excluirlos.
///
/// Para TypeScript no hay AST real (ver <see cref="TypeScriptChunkingStrategy"/>): se
/// reutiliza el mismo criterio de 3 estados (Normal / InString / InBlockComment) para
/// no escanear identificadores dentro de strings o comentarios.
///
/// Una entrada que no se puede procesar (excepción durante la extracción) nunca debe
/// tumbar la ingesta: se atrapa internamente y se devuelven listas vacías.
/// </summary>
internal static class SymbolExtractor
{
    private static readonly IReadOnlyList<string> Empty = Array.Empty<string>();

    // Palabras clave y tipos primitivos de C# que son ruido si aparecen como
    // "consumido" (p.ej. `int`, `var`, `string` en una declaración de variable).
    private static readonly HashSet<string> CSharpNoise = new(StringComparer.Ordinal)
    {
        "var", "int", "string", "bool", "double", "float", "decimal", "long", "short",
        "byte", "char", "object", "void", "true", "false", "null", "this", "base",
        "new", "return", "if", "else", "for", "foreach", "while", "do", "switch",
        "case", "break", "continue", "try", "catch", "finally", "throw", "using",
        "namespace", "class", "struct", "interface", "enum", "public", "private",
        "protected", "internal", "static", "readonly", "const", "async", "await",
        "get", "set", "value", "nameof", "typeof", "default", "out", "ref", "in",
        "is", "as", "yield", "params"
    };

    /// <summary>
    /// Extrae símbolos definidos y consumidos de un nodo de C# (método, constructor,
    /// declaración de tipo, o cualquier <see cref="SyntaxNode"/> equivalente).
    /// </summary>
    public static (IReadOnlyList<string> Defined, IReadOnlyList<string> Consumed) FromCSharpNode(
        SyntaxNode? node)
    {
        if (node is null) return (Empty, Empty);

        try
        {
            var defined = new HashSet<string>(StringComparer.Ordinal);
            var consumed = new HashSet<string>(StringComparer.Ordinal);

            switch (node)
            {
                case MethodDeclarationSyntax method:
                    defined.Add(method.Identifier.Text);
                    CollectConsumed(method.Body as SyntaxNode ?? method.ExpressionBody, consumed);
                    break;

                case ConstructorDeclarationSyntax ctor:
                    defined.Add(ctor.Identifier.Text);
                    CollectConsumed(ctor.Body as SyntaxNode ?? ctor.ExpressionBody, consumed);
                    CollectConsumed(ctor.Initializer, consumed);
                    break;

                case TypeDeclarationSyntax type:
                    defined.Add(type.Identifier.Text);
                    break;

                default:
                    // Nodo genérico (p.ej. propiedad o campo individual): el nombre
                    // declarado se agrega vía FromCSharpFieldOrPropertyGroup.
                    CollectConsumed(node, consumed);
                    break;
            }

            return (Sorted(defined), Sorted(consumed));
        }
        catch
        {
            // La extracción de símbolos nunca debe tumbar la ingesta.
            return (Empty, Empty);
        }
    }

    /// <summary>
    /// Extrae símbolos de un grupo de campos o propiedades (los chunks de clase/
    /// propiedades agrupan varios miembros en un solo chunk).
    /// </summary>
    public static (IReadOnlyList<string> Defined, IReadOnlyList<string> Consumed) FromCSharpMemberGroup(
        IEnumerable<MemberDeclarationSyntax> members)
    {
        try
        {
            var defined = new HashSet<string>(StringComparer.Ordinal);
            var consumed = new HashSet<string>(StringComparer.Ordinal);

            foreach (var member in members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                        foreach (var v in field.Declaration.Variables)
                            defined.Add(v.Identifier.Text);
                        CollectConsumed(field.Declaration, consumed);
                        break;

                    case PropertyDeclarationSyntax prop:
                        defined.Add(prop.Identifier.Text);
                        CollectConsumed(prop.Initializer, consumed);
                        CollectConsumed(prop.ExpressionBody, consumed);
                        if (prop.AccessorList is not null)
                            CollectConsumed(prop.AccessorList, consumed);
                        break;
                }
            }

            return (Sorted(defined), Sorted(consumed));
        }
        catch
        {
            return (Empty, Empty);
        }
    }

    private static void CollectConsumed(SyntaxNode? scope, HashSet<string> consumed)
    {
        if (scope is null) return;

        foreach (var invocation in scope.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                _ => null
            };
            if (name is not null) consumed.Add(name);
        }

        foreach (var memberAccess in scope.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
            consumed.Add(memberAccess.Name.Identifier.Text);

        foreach (var identifier in scope.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var text = identifier.Identifier.Text;
            if (CSharpNoise.Contains(text)) continue;
            consumed.Add(text);
        }
    }

    private static IReadOnlyList<string> Sorted(HashSet<string> set) =>
        set.Count == 0 ? Empty : set.OrderBy(s => s, StringComparer.Ordinal).ToArray();

    // ── TypeScript ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Extrae identificadores "consumidos" de un bloque de TypeScript ya capturado
    /// (las líneas de un chunk), recorriendo carácter a carácter con el mismo
    /// criterio de 3 estados que usa <see cref="TypeScriptChunkingStrategy"/> para
    /// contar llaves: Normal / InString / InBlockComment. Sólo se consideran
    /// identificadores vistos en estado Normal, nunca dentro de un string, de un
    /// comentario de línea (// ...) ni de un comentario de bloque (/* ... */).
    /// </summary>
    public static IReadOnlyList<string> FromTypeScriptLines(IEnumerable<string> lines)
    {
        try
        {
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            var state = TsLexerState.Normal;
            char stringDelim = '\0';

            foreach (var line in lines)
            {
                int i = 0;
                while (i < line.Length)
                {
                    char c = line[i];

                    switch (state)
                    {
                        case TsLexerState.Normal:
                            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                            {
                                state = TsLexerState.InBlockComment;
                                i += 2;
                                continue;
                            }

                            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                            {
                                // Resto de la línea es comentario: saltar a la siguiente línea.
                                i = line.Length;
                                continue;
                            }

                            if (c is '\'' or '"' or '`')
                            {
                                stringDelim = c;
                                state = TsLexerState.InString;
                                i++;
                                continue;
                            }

                            if (IsIdentifierStart(c))
                            {
                                int start = i;
                                while (i < line.Length && IsIdentifierPart(line[i])) i++;
                                var name = line.Substring(start, i - start);

                                if (!TsNoise.Contains(name))
                                    consumed.Add(name);

                                continue;
                            }

                            i++;
                            break;

                        case TsLexerState.InString:
                            if (c == '\\')
                            {
                                i += 2;
                                continue;
                            }
                            if (c == stringDelim)
                            {
                                state = TsLexerState.Normal;
                                stringDelim = '\0';
                            }
                            i++;
                            break;

                        case TsLexerState.InBlockComment:
                            if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                            {
                                state = TsLexerState.Normal;
                                i += 2;
                                continue;
                            }
                            i++;
                            break;
                    }
                }
            }

            return Sorted(consumed);
        }
        catch
        {
            return Empty;
        }
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || c == '$';
    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private enum TsLexerState { Normal, InString, InBlockComment }

    private static readonly HashSet<string> TsNoise = new(StringComparer.Ordinal)
    {
        "var", "let", "const", "function", "class", "interface", "type", "enum",
        "extends", "implements", "return", "if", "else", "for", "while", "do",
        "switch", "case", "break", "continue", "try", "catch", "finally", "throw",
        "new", "this", "super", "true", "false", "null", "undefined", "void",
        "typeof", "instanceof", "in", "of", "async", "await", "export", "import",
        "default", "public", "private", "protected", "static", "readonly",
        "abstract", "override", "as", "from", "number", "string", "boolean", "any"
    };
}
