using RagEngine.Core.Infrastructure.VectorStore.Expansion;
using RagEngine.Experiment152;
using Xunit;

namespace RagEngine.Experiment152.Tests;

public class SourceSpanLocatorTests
{
    [Fact]
    public void Locates_chunk_ignoring_whitespace_differences()
    {
        const string file = "class A\n{\n    public void M()\n    {\n        N();\n    }\n}\n";
        var span = SourceSpanLocator.Locate(file, "public void M() { N(); }");

        Assert.NotNull(span);
        var text = file[span!.Value.Start..(span.Value.End + 1)];
        Assert.StartsWith("public void M()", text);
        Assert.EndsWith("}", text);
    }

    [Fact]
    public void Returns_null_when_content_is_absent()
    {
        Assert.Null(SourceSpanLocator.Locate("class A { }", "public void Missing() { }"));
    }

    [Fact]
    public void Returns_null_when_content_is_ambiguous()
    {
        // Dos ocurrencias identicas: localizar "la primera" seria elegir al azar cual de las
        // dos definiciones se esta cualificando. Se prefiere abstenerse.
        const string file = "class A { void M() { } }\nclass B { void M() { } }\n";
        Assert.Null(SourceSpanLocator.Locate(file, "void M() { }"));
    }
}

public class SyntaxQualificationTests
{
    private static string WriteCorpus(string content, string name = "Sample.cs")
    {
        var directory = Path.Combine(Path.GetTempPath(), "exp152-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), content);
        return directory;
    }

    private static IReadOnlyList<QualifiedSymbolTarget> Qualify(string source, string chunk, string name = "Sample.cs")
    {
        var root = WriteCorpus(source, name);
        var builder = new SyntaxQualificationBuilder();
        builder.IndexCorpus(root, new[] { name });
        var map = builder.Qualify(root, new[]
        {
            new ChunkPayload { Id = "chunk-1", RelativePath = name, Content = chunk },
        });
        Directory.Delete(root, recursive: true);
        return map["chunk-1"];
    }

    [Fact]
    public void Qualifies_call_through_a_typed_field_to_the_declaring_class()
    {
        const string source = """
            class Solicitud { public void Cancelar() { } }
            class Formulario
            {
                private Solicitud _solicitud;
                public void Accion() { _solicitud.Cancelar(); }
            }
            """;

        var targets = Qualify(source, "public void Accion() { _solicitud.Cancelar(); }");

        Assert.Contains(targets, t => t.ClassName == "Solicitud" && t.MethodName == "Cancelar");
        // El punto del experimento: el join por nombre habria admitido CUALQUIER clase con
        // un metodo Cancelar. La cualificacion no propone ninguna otra.
        Assert.DoesNotContain(targets, t => t.ClassName == "Formulario" && t.MethodName == "Cancelar");
    }

    [Fact]
    public void Qualifies_unqualified_call_to_the_enclosing_type()
    {
        const string source = """
            class Proceso
            {
                public void Interno() { }
                public void Publico() { Interno(); }
            }
            """;

        var targets = Qualify(source, "public void Publico() { Interno(); }");
        Assert.Contains(targets, t => t.ClassName == "Proceso" && t.MethodName == "Interno");
    }

    [Fact]
    public void Follows_one_property_hop_to_the_declared_type()
    {
        const string source = """
            class Solicitud { public void Cancelar() { } }
            interface IPortador { Solicitud SolicitudCuenta { get; } }
            class Controlador
            {
                private IPortador _portador;
                public void Accion() { _portador.SolicitudCuenta.Cancelar(); }
            }
            """;

        var targets = Qualify(source, "public void Accion() { _portador.SolicitudCuenta.Cancelar(); }");
        Assert.Contains(targets, t => t.ClassName == "Solicitud" && t.MethodName == "Cancelar");
    }

    [Fact]
    public void Abstains_when_the_receiver_type_cannot_be_asserted()
    {
        // El receptor es el retorno de una invocacion: sin resolucion de tipos no hay forma
        // sintactica de saber que devuelve. Debe abstenerse, no inventar la clase envolvente.
        const string source = """
            class Fabrica { }
            class Uso
            {
                public void Accion() { Crear().Cancelar(); }
                Fabrica Crear() { return null; }
            }
            """;

        var targets = Qualify(source, "public void Accion() { Crear().Cancelar(); }");
        Assert.DoesNotContain(targets, t => t.MethodName == "Cancelar");
    }
}

public class GraphStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "exp152-graph-" + Guid.NewGuid().ToString("N"));

    private GraphStore New(string name = "g.sqlite3")
    {
        Directory.CreateDirectory(_directory);
        return new GraphStore(Path.Combine(_directory, name));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Interrupted_replace_leaves_the_previous_state_intact()
    {
        using var store = New();
        store.ReplaceDocument("e1",
            new[] { new GraphNode("a", 1, "c", "T", "m"), new GraphNode("b", 1, "c", "T", "m") },
            new[] { new GraphEdge("a", 1, "b", 1, 1) }, crashBeforeCommit: false);

        var status = store.ReplaceDocument("e2",
            new[] { new GraphNode("b", 2, "c", "T", "m") },
            new[] { new GraphEdge("a", 1, "b", 2, 2) }, crashBeforeCommit: true);

        Assert.Equal("interrupted", status);
        Assert.Equal(1, store.Node("b")!.Version);
        Assert.Single(store.Edges());
        Assert.Equal(1, store.Edges()[0].EdgeVersion);
        Assert.False(store.IsApplied("e2"));
    }

    [Fact]
    public void Replace_removes_superseded_edges_leaving_no_orphan_endpoints()
    {
        using var store = New();
        store.ReplaceDocument("e1",
            new[] { new GraphNode("a", 1, "c", "T", "m"), new GraphNode("b", 1, "c", "T", "m") },
            new[] { new GraphEdge("a", 1, "b", 1, 1) }, crashBeforeCommit: false);

        store.ReplaceDocument("e2",
            new[] { new GraphNode("b", 2, "c", "T", "m") },
            new[] { new GraphEdge("a", 1, "b", 2, 2) }, crashBeforeCommit: false);

        var edges = store.Edges();
        Assert.Single(edges);
        Assert.Equal(2, edges[0].TargetVersion);
        var nodes = store.Nodes().ToDictionary(n => n.Id);
        Assert.All(edges, e => Assert.Equal(nodes[e.Source].Version, e.SourceVersion));
        Assert.All(edges, e => Assert.Equal(nodes[e.Target].Version, e.TargetVersion));
    }

    [Fact]
    public void Replay_of_an_applied_event_does_not_resurrect_a_deleted_node()
    {
        using var store = New();
        store.ReplaceDocument("e1",
            new[] { new GraphNode("a", 1, "c", "T", "m"), new GraphNode("b", 1, "c", "T", "m") },
            new[] { new GraphEdge("a", 1, "b", 1, 1) }, crashBeforeCommit: false);
        store.DeleteDocument("d1", new[] { "b" });
        store.Restart();

        store.ReplaceDocument("e1",
            new[] { new GraphNode("b", 1, "c", "T", "m") },
            new[] { new GraphEdge("a", 1, "b", 1, 1) }, crashBeforeCommit: false);

        Assert.Null(store.Node("b"));
        Assert.Empty(store.Edges());
    }

    [Theory]
    [InlineData("alpha", "alpha", true)]
    [InlineData("alpha.child", "alpha", true)]
    [InlineData("alpha-private", "alpha", false)]
    [InlineData("alphabet", "alpha", false)]
    [InlineData("beta", "alpha", false)]
    public void Module_boundary_is_exact_not_a_string_prefix(string value, string required, bool expected) =>
        Assert.Equal(expected, GraphStore.MatchesModuleBoundary(value, required));

    [Fact]
    public void Authorization_is_checked_on_the_seed_and_on_every_hop()
    {
        using var store = New();
        store.BulkLoad(new[]
        {
            new GraphNode("a", 1, "c", "T", "alpha"),
            new GraphNode("ok", 1, "c", "T", "alpha.child"),
            new GraphNode("otherTenant", 1, "c", "X", "alpha"),
            new GraphNode("deep", 1, "c", "X", "alpha"),
        }, new[]
        {
            new GraphEdge("a", 1, "ok", 1, 1),
            new GraphEdge("a", 1, "otherTenant", 1, 1),
            new GraphEdge("ok", 1, "deep", 1, 1),
        });

        var result = store.Query("a", "c", new GraphContext("authorized", "T", "alpha"), null, 2);

        Assert.Equal("ok", result.Status);
        // "deep" cuelga de un nodo autorizado pero es de otro tenant: comprobar la ACL solo
        // en el primer salto lo habria dejado entrar por la puerta de atras.
        Assert.Equal(new[] { "ok" }, result.Hits);
    }

    [Fact]
    public void Missing_context_is_an_explicit_error_not_an_open_query()
    {
        using var store = New();
        store.BulkLoad(new[] { new GraphNode("a", 1, "c", "T", "alpha") }, Array.Empty<GraphEdge>());

        var result = store.Query("a", "c", null, null, 2);

        Assert.Equal("invalid_context", result.Status);
        Assert.Empty(result.Hits);
    }
}

public class ExpansionPlanTests
{
    [Fact]
    public void Empty_plan_means_abstention()
    {
        Assert.True(SymbolExpansionPlan.Empty.IsEmpty);
        Assert.False(new SymbolExpansionPlan
        {
            Targets = new[] { new QualifiedSymbolTarget("A", "M") },
        }.IsEmpty);
        Assert.False(new SymbolExpansionPlan { ChunkIds = new[] { "id" } }.IsEmpty);
    }

    [Fact]
    public void Precomputed_qualifier_abstains_for_an_unknown_seed()
    {
        var qualifier = new PrecomputedQualifier("syntax",
            new Dictionary<string, IReadOnlyList<QualifiedSymbolTarget>>
            {
                ["known"] = new[] { new QualifiedSymbolTarget("A", "M") },
            });

        var unknown = qualifier.Qualify(
            new[] { new SymbolExpansionSeed("other", null, null, null, null, Array.Empty<string>()) },
            new[] { "M" });

        Assert.True(unknown.IsEmpty);
    }
}
