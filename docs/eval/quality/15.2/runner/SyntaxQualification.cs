using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RagEngine.Core.Infrastructure.VectorStore.Expansion;

namespace RagEngine.Experiment152;

/// <summary>
/// Brazo "syntax": cualifica el destino de cada llamada usando SOLO el arbol sintactico,
/// sin Compilation ni resolucion de simbolos. Es el escalon intermedio entre el join por
/// nombre de 6.d (que solo ve el identificador y colisiona entre homonimos) y el
/// SemanticModel (que exige referencias restauradas).
///
/// Que puede y que no: infiere el tipo del receptor por el tipo DECLARADO de locales,
/// parametros, campos y propiedades, y encadena accesos a miembro usando un mapa de tipos
/// construido sobre todo el corpus. No resuelve genericos, no resuelve el tipo de retorno
/// de una invocacion intermedia, y ante una interfaz devuelve la interfaz, no la
/// implementacion. Cada uno de esos huecos es una ABSTENCION declarada, nunca una caida
/// silenciosa al join por nombre.
/// </summary>
public sealed class SyntaxQualificationBuilder
{
    private readonly Dictionary<string, Dictionary<string, string>> _memberTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _baseTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownTypes = new(StringComparer.Ordinal);

    public int FilesParsed { get; private set; }
    public int ChunksLocated { get; private set; }
    public int ChunksUnlocatable { get; private set; }
    public int InvocationsSeen { get; private set; }
    public int InvocationsQualified { get; private set; }

    /// <summary>Primera pasada: inventario sintactico de tipos, bases y tipos de miembro.</summary>
    public void IndexCorpus(string corpusRoot, IEnumerable<string> relativePaths)
    {
        foreach (var relative in relativePaths)
        {
            var path = Path.Combine(corpusRoot, relative);
            if (!File.Exists(path)) continue;
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
            FilesParsed++;

            foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var name = type.Identifier.Text;
                _knownTypes.Add(name);

                if (type.BaseList is not null)
                {
                    if (!_baseTypes.TryGetValue(name, out var bases))
                        bases = _baseTypes[name] = new List<string>();
                    foreach (var b in type.BaseList.Types)
                    {
                        var simple = SimpleTypeName(b.Type);
                        if (simple is not null && !bases.Contains(simple)) bases.Add(simple);
                    }
                }

                if (!_memberTypes.TryGetValue(name, out var members))
                    members = _memberTypes[name] = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var member in type.Members)
                {
                    switch (member)
                    {
                        case PropertyDeclarationSyntax property:
                            if (SimpleTypeName(property.Type) is { } pt) members[property.Identifier.Text] = pt;
                            break;
                        case FieldDeclarationSyntax field:
                            if (SimpleTypeName(field.Declaration.Type) is { } ft)
                                foreach (var v in field.Declaration.Variables) members[v.Identifier.Text] = ft;
                            break;
                    }
                }
            }
        }
    }

    /// <summary>Segunda pasada: por chunk, los pares (clase, metodo) de sus invocaciones.</summary>
    public Dictionary<string, IReadOnlyList<QualifiedSymbolTarget>> Qualify(
        string corpusRoot, IEnumerable<ChunkPayload> chunks)
    {
        var result = new Dictionary<string, IReadOnlyList<QualifiedSymbolTarget>>(StringComparer.Ordinal);

        foreach (var group in chunks.Where(c => c.RelativePath is not null && c.Content is not null)
                                    .GroupBy(c => c.RelativePath!, StringComparer.Ordinal))
        {
            var path = Path.Combine(corpusRoot, group.Key);
            if (!File.Exists(path)) { ChunksUnlocatable += group.Count(); continue; }

            var text = File.ReadAllText(path);
            var root = CSharpSyntaxTree.ParseText(text, path: path).GetRoot();

            foreach (var chunk in group)
            {
                var span = SourceSpanLocator.Locate(text, chunk.Content!);
                if (span is null) { ChunksUnlocatable++; continue; }
                ChunksLocated++;

                var textSpan = TextSpan.FromBounds(span.Value.Start, Math.Min(span.Value.End + 1, text.Length));
                var targets = new List<QualifiedSymbolTarget>();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var invocation in root.DescendantNodes()
                                               .OfType<InvocationExpressionSyntax>()
                                               .Where(n => textSpan.Contains(n.SpanStart)))
                {
                    InvocationsSeen++;
                    var qualified = false;
                    foreach (var target in TargetsOf(invocation))
                    {
                        qualified = true;
                        if (seen.Add(target.ClassName + " " + target.MethodName))
                            targets.Add(target);
                    }
                    if (qualified) InvocationsQualified++;
                }

                result[chunk.Id] = targets;
            }
        }

        return result;
    }

    private IEnumerable<QualifiedSymbolTarget> TargetsOf(InvocationExpressionSyntax invocation)
    {
        var enclosingType = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;

        string? method = null;
        string? receiverType = null;

        switch (invocation.Expression)
        {
            case IdentifierNameSyntax id:
                method = id.Identifier.Text;
                receiverType = enclosingType;
                break;
            case GenericNameSyntax generic:
                method = generic.Identifier.Text;
                receiverType = enclosingType;
                break;
            case MemberAccessExpressionSyntax access:
                method = access.Name.Identifier.Text;
                receiverType = ResolveType(access.Expression, invocation, enclosingType);
                break;
            case MemberBindingExpressionSyntax binding:
                // a?.M() - el receptor vive en el ConditionalAccessExpression padre.
                method = binding.Name.Identifier.Text;
                var conditional = invocation.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault();
                receiverType = conditional is null ? null : ResolveType(conditional.Expression, invocation, enclosingType);
                break;
        }

        if (method is null || receiverType is null) yield break;

        foreach (var type in WithAncestors(receiverType))
            yield return new QualifiedSymbolTarget(type, method);
    }

    /// <summary>Tipo declarado de una expresion, por nombre. Null = no se puede afirmar.</summary>
    private string? ResolveType(ExpressionSyntax expression, SyntaxNode context, string? enclosingType)
    {
        switch (expression)
        {
            case ThisExpressionSyntax:
                return enclosingType;

            case ParenthesizedExpressionSyntax parenthesized:
                return ResolveType(parenthesized.Expression, context, enclosingType);

            case CastExpressionSyntax cast:
                return SimpleTypeName(cast.Type);

            case ObjectCreationExpressionSyntax creation:
                return SimpleTypeName(creation.Type);

            case IdentifierNameSyntax identifier:
            {
                var name = identifier.Identifier.Text;
                if (LocalOrParameterType(name, context) is { } local) return local;
                if (enclosingType is not null && MemberType(enclosingType, name) is { } member) return member;
                // Llamada estatica: el propio identificador es un tipo declarado en el corpus.
                return _knownTypes.Contains(name) ? name : null;
            }

            case MemberAccessExpressionSyntax access:
            {
                var receiver = ResolveType(access.Expression, context, enclosingType);
                return receiver is null ? null : MemberType(receiver, access.Name.Identifier.Text);
            }

            default:
                return null;
        }
    }

    private static string? LocalOrParameterType(string name, SyntaxNode context)
    {
        foreach (var ancestor in context.Ancestors())
        {
            if (ancestor is BaseMethodDeclarationSyntax method)
            {
                foreach (var parameter in method.ParameterList.Parameters)
                    if (parameter.Identifier.Text == name && parameter.Type is not null)
                        return SimpleTypeName(parameter.Type);
            }

            foreach (var declaration in ancestor.DescendantNodes().OfType<VariableDeclarationSyntax>())
            {
                foreach (var variable in declaration.Variables)
                {
                    if (variable.Identifier.Text != name) continue;
                    // `var x = new Foo()` sigue siendo sintactico: el tipo esta en el inicializador.
                    if (declaration.Type.IsVar)
                        return variable.Initializer?.Value is ObjectCreationExpressionSyntax creation
                            ? SimpleTypeName(creation.Type)
                            : null;
                    return SimpleTypeName(declaration.Type);
                }
            }

            if (ancestor is BaseMethodDeclarationSyntax or TypeDeclarationSyntax) break;
        }

        return null;
    }

    private string? MemberType(string typeName, string memberName)
    {
        foreach (var type in WithAncestors(typeName))
            if (_memberTypes.TryGetValue(type, out var members) && members.TryGetValue(memberName, out var result))
                return result;
        return null;
    }

    /// <summary>
    /// El tipo y hasta tres niveles de bases sintacticas: un metodo heredado se define en la
    /// base, no en la clase que lo llama.
    /// </summary>
    private IEnumerable<string> WithAncestors(string typeName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { typeName };
        var frontier = new List<string> { typeName };
        yield return typeName;

        for (var depth = 0; depth < 3 && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                if (!_baseTypes.TryGetValue(current, out var bases)) continue;
                foreach (var b in bases)
                    if (seen.Add(b)) { next.Add(b); yield return b; }
            }
            frontier = next;
        }
    }

    private static string? SimpleTypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax qualified => SimpleTypeName(qualified.Right),
        GenericNameSyntax generic => generic.Identifier.Text,
        NullableTypeSyntax nullable => SimpleTypeName(nullable.ElementType),
        ArrayTypeSyntax array => SimpleTypeName(array.ElementType),
        AliasQualifiedNameSyntax alias => SimpleTypeName(alias.Name),
        _ => null,
    };
}
