using System.Text.Json.Nodes;

namespace RagEngine.Experiment152;

/// <summary>
/// Ejecuta de verdad los escenarios de graph-fixtures.json contra un almacen durable
/// aislado y devuelve lo OBSERVADO. Nunca lee el campo <c>expected</c>: el fixture es el
/// oraculo, y un runner que lo consultara estaria copiando la respuesta en vez de medir
/// el contrato. Cada escenario arranca con almacenamiento vacio.
/// </summary>
public static class GraphFixtures
{
    public static JsonObject Execute(JsonObject fixtures, string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);

        var observations = new JsonObject();

        foreach (var node in fixtures["scenarios"]!.AsArray())
        {
            var scenario = node!.AsObject();
            var id = scenario["id"]!.GetValue<string>();
            var path = Path.Combine(directory, id + ".sqlite3");

            var store = new GraphStore(path);
            try
            {
                if (scenario["setup"] is JsonObject setup)
                    store.BulkLoad(ReadNodes(setup["nodes"]), ReadEdges(setup["edges"]));

                var results = new JsonArray();
                foreach (var step in scenario["input"]!.AsArray())
                    results.Add(Apply(ref store, path, step!.AsObject()));

                observations[id] = results;
            }
            finally
            {
                store.Dispose();
            }
        }

        return observations;
    }

    private static JsonNode Apply(ref GraphStore store, string path, JsonObject step)
    {
        var op = step["op"]!.GetValue<string>();

        switch (op)
        {
            case "replace_document":
            {
                var status = store.ReplaceDocument(
                    step["event_id"]!.GetValue<string>(),
                    ReadNodes(step["nodes"]),
                    ReadEdges(step["edges"]),
                    crashBeforeCommit: step["crash"]?.GetValue<string>() == "before_commit");
                return State(store, status);
            }

            case "delete_document":
            {
                var ids = step["node_ids"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                var status = store.DeleteDocument(step["event_id"]!.GetValue<string>(), ids);
                return State(store, status);
            }

            case "restart":
                store.Restart();
                return State(store, "ok");

            case "restart_and_replay":
            {
                store.Restart();
                // Replay del MISMO event_id ya aplicado: debe ser un no-op incluso despues de
                // un borrado posterior. Se reenvia vacio a proposito — la idempotencia la
                // decide el log de eventos, no el contenido que se vuelva a mandar.
                var status = store.ReplaceDocument(
                    step["event_id"]!.GetValue<string>(),
                    Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), crashBeforeCommit: false);
                return State(store, status);
            }

            case "query":
            case "restart_and_query":
            {
                if (op == "restart_and_query") store.Restart();
                var context = step["context"] is JsonObject c
                    ? new GraphContext(
                        c["mode"]!.GetValue<string>(),
                        c["tenant"]?.GetValue<string>(),
                        c["module"]?.GetValue<string>())
                    : null;

                var result = store.Query(
                    step["seed"]!.GetValue<string>(),
                    step["collection"]!.GetValue<string>(),
                    context,
                    step["filter_module"]?.GetValue<string>(),
                    step["hops"]!.GetValue<int>());

                return new JsonObject
                {
                    ["status"] = result.Status,
                    ["hits"] = new JsonArray(result.Hits.Select(h => (JsonNode)h!).ToArray()),
                    ["traversed"] = new JsonArray(result.Traversed
                        .Select(hop => (JsonNode)new JsonArray(hop
                            .Select(edge => (JsonNode)new JsonArray(edge.Select(x => (JsonNode)x!).ToArray()))
                            .ToArray()))
                        .ToArray()),
                };
            }

            default:
                throw new InvalidOperationException($"Operacion de fixture desconocida: {op}");
        }
    }

    private static JsonObject State(GraphStore store, string status) => new()
    {
        ["status"] = status,
        ["nodes"] = new JsonArray(store.Nodes()
            .Select(n => (JsonNode)new JsonArray(n.Id, n.Version, n.Collection,
                n.Tenant is null ? null : JsonValue.Create(n.Tenant), n.Module))
            .ToArray()),
        ["edges"] = new JsonArray(store.Edges()
            .Select(e => (JsonNode)new JsonArray(e.Source, e.SourceVersion, e.Target,
                e.TargetVersion, e.EdgeVersion))
            .ToArray()),
    };

    private static List<GraphNode> ReadNodes(JsonNode? node) =>
        node is null
            ? new List<GraphNode>()
            : node.AsArray().Select(row =>
            {
                var r = row!.AsArray();
                return new GraphNode(
                    r[0]!.GetValue<string>(), r[1]!.GetValue<long>(), r[2]!.GetValue<string>(),
                    r[3] is null ? null : r[3]!.GetValue<string>(), r[4]!.GetValue<string>());
            }).ToList();

    private static List<GraphEdge> ReadEdges(JsonNode? node) =>
        node is null
            ? new List<GraphEdge>()
            : node.AsArray().Select(row =>
            {
                var r = row!.AsArray();
                return new GraphEdge(
                    r[0]!.GetValue<string>(), r[1]!.GetValue<long>(), r[2]!.GetValue<string>(),
                    r[3]!.GetValue<long>(), r[4]!.GetValue<long>());
            }).ToList();
}
