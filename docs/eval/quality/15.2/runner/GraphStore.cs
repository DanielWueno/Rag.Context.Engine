using Microsoft.Data.Sqlite;

namespace RagEngine.Experiment152;

public sealed record GraphNode(string Id, long Version, string Collection, string? Tenant, string Module);

public sealed record GraphEdge(string Source, long SourceVersion, string Target, long TargetVersion, long EdgeVersion);

public sealed record GraphContext(string Mode, string? Tenant, string? Module);

/// <summary>
/// Almacen durable del brazo "graph": nodos y aristas VERSIONADOS en SQLite, con log de
/// eventos aplicados para que un replay sea idempotente incluso despues de un reinicio o
/// de un borrado.
///
/// Tres propiedades que el contrato de graph-fixtures.json exige y que no salen gratis:
/// (1) replace_document es atomico - una interrupcion antes del commit deja el estado
/// anterior intacto, no un grafo a medias; (2) reemplazar un documento retira las aristas
/// superadas, de modo que no quedan extremos apuntando a versiones que ya no existen ni
/// huerfanos tras un borrado; (3) la autorizacion se comprueba en CADA salto y tambien
/// sobre la semilla, no solo al entregar el resultado.
/// </summary>
public sealed class GraphStore : IDisposable
{
    private readonly string _path;
    private SqliteConnection _connection;

    public GraphStore(string path)
    {
        _path = path;
        _connection = Open(path);
        Initialize();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS nodes (
                id TEXT PRIMARY KEY, version INTEGER NOT NULL, collection TEXT NOT NULL,
                tenant TEXT NULL, module TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS edges (
                source TEXT NOT NULL, source_version INTEGER NOT NULL,
                target TEXT NOT NULL, target_version INTEGER NOT NULL,
                edge_version INTEGER NOT NULL, PRIMARY KEY (source, target));
            CREATE TABLE IF NOT EXISTS applied_events (event_id TEXT PRIMARY KEY);
            CREATE INDEX IF NOT EXISTS edges_source ON edges (source);
            """);
    }

    private void Execute(string sql, Action<SqliteCommand>? bind = null, SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null) command.Transaction = transaction;
        bind?.Invoke(command);
        command.ExecuteNonQuery();
    }

    /// <summary>Cierra y reabre el archivo: lo que no se persistio, no sobrevive.</summary>
    public void Restart()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        _connection = Open(_path);
    }

    public bool IsApplied(string eventId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM applied_events WHERE event_id = $e";
        command.Parameters.AddWithValue("$e", eventId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Alta o actualizacion atomica de un documento. Devuelve "ok" o "interrupted".
    /// Un event_id ya aplicado es un no-op silencioso: es lo que hace idempotente al replay.
    /// </summary>
    public string ReplaceDocument(string eventId, IReadOnlyList<GraphNode> nodes,
                                  IReadOnlyList<GraphEdge> edges, bool crashBeforeCommit)
    {
        if (IsApplied(eventId)) return "ok";

        using var transaction = _connection.BeginTransaction();

        foreach (var node in nodes)
        {
            Execute("""
                INSERT INTO nodes (id, version, collection, tenant, module)
                VALUES ($i, $v, $c, $t, $m)
                ON CONFLICT(id) DO UPDATE SET version = $v, collection = $c, tenant = $t, module = $m;
                """, command =>
            {
                command.Parameters.AddWithValue("$i", node.Id);
                command.Parameters.AddWithValue("$v", node.Version);
                command.Parameters.AddWithValue("$c", node.Collection);
                command.Parameters.AddWithValue("$t", (object?)node.Tenant ?? DBNull.Value);
                command.Parameters.AddWithValue("$m", node.Module);
            }, transaction);

            // Las aristas que tocaban la version anterior de este nodo quedan superadas: se
            // retiran ANTES de insertar las nuevas para no dejar extremos desalineados.
            Execute("DELETE FROM edges WHERE source = $i OR target = $i;",
                command => command.Parameters.AddWithValue("$i", node.Id), transaction);
        }

        foreach (var edge in edges)
        {
            Execute("""
                INSERT INTO edges (source, source_version, target, target_version, edge_version)
                VALUES ($s, $sv, $t, $tv, $ev)
                ON CONFLICT(source, target) DO UPDATE SET
                    source_version = $sv, target_version = $tv, edge_version = $ev;
                """, command =>
            {
                command.Parameters.AddWithValue("$s", edge.Source);
                command.Parameters.AddWithValue("$sv", edge.SourceVersion);
                command.Parameters.AddWithValue("$t", edge.Target);
                command.Parameters.AddWithValue("$tv", edge.TargetVersion);
                command.Parameters.AddWithValue("$ev", edge.EdgeVersion);
            }, transaction);
        }

        Execute("INSERT OR IGNORE INTO applied_events (event_id) VALUES ($e);",
            command => command.Parameters.AddWithValue("$e", eventId), transaction);

        if (crashBeforeCommit)
        {
            transaction.Rollback();
            return "interrupted";
        }

        transaction.Commit();
        return "ok";
    }

    public string DeleteDocument(string eventId, IReadOnlyList<string> nodeIds)
    {
        if (IsApplied(eventId)) return "ok";

        using var transaction = _connection.BeginTransaction();
        foreach (var id in nodeIds)
        {
            Execute("DELETE FROM edges WHERE source = $i OR target = $i;",
                command => command.Parameters.AddWithValue("$i", id), transaction);
            Execute("DELETE FROM nodes WHERE id = $i;",
                command => command.Parameters.AddWithValue("$i", id), transaction);
        }
        Execute("INSERT OR IGNORE INTO applied_events (event_id) VALUES ($e);",
            command => command.Parameters.AddWithValue("$e", eventId), transaction);
        transaction.Commit();
        return "ok";
    }

    /// <summary>
    /// Carga inicial del grafo real (23k nodos): una sola transaccion y sentencias
    /// preparadas. No pasa por el log de eventos porque no es un documento del flujo de
    /// ingesta sino el estado de partida del brazo.
    /// </summary>
    public void BulkLoad(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        using var transaction = _connection.BeginTransaction();

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO nodes (id, version, collection, tenant, module) VALUES ($i, $v, $c, $t, $m)
                ON CONFLICT(id) DO UPDATE SET version = $v, collection = $c, tenant = $t, module = $m;
                """;
            var i = command.Parameters.Add("$i", Microsoft.Data.Sqlite.SqliteType.Text);
            var v = command.Parameters.Add("$v", Microsoft.Data.Sqlite.SqliteType.Integer);
            var c = command.Parameters.Add("$c", Microsoft.Data.Sqlite.SqliteType.Text);
            var t = command.Parameters.Add("$t", Microsoft.Data.Sqlite.SqliteType.Text);
            var m = command.Parameters.Add("$m", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var node in nodes)
            {
                i.Value = node.Id; v.Value = node.Version; c.Value = node.Collection;
                t.Value = (object?)node.Tenant ?? DBNull.Value; m.Value = node.Module;
                command.ExecuteNonQuery();
            }
        }

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO edges (source, source_version, target, target_version, edge_version)
                VALUES ($s, $sv, $t, $tv, $ev)
                ON CONFLICT(source, target) DO UPDATE SET
                    source_version = $sv, target_version = $tv, edge_version = $ev;
                """;
            var s = command.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Text);
            var sv = command.Parameters.Add("$sv", Microsoft.Data.Sqlite.SqliteType.Integer);
            var t = command.Parameters.Add("$t", Microsoft.Data.Sqlite.SqliteType.Text);
            var tv = command.Parameters.Add("$tv", Microsoft.Data.Sqlite.SqliteType.Integer);
            var ev = command.Parameters.Add("$ev", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var edge in edges)
            {
                s.Value = edge.Source; sv.Value = edge.SourceVersion;
                t.Value = edge.Target; tv.Value = edge.TargetVersion; ev.Value = edge.EdgeVersion;
                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    public List<GraphNode> Nodes()
    {
        var result = new List<GraphNode>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, version, collection, tenant, module FROM nodes ORDER BY id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new GraphNode(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
        return result;
    }

    public List<GraphEdge> Edges()
    {
        var result = new List<GraphEdge>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT source, source_version, target, target_version, edge_version FROM edges
            ORDER BY source, source_version, target, target_version, edge_version
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new GraphEdge(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4)));
        return result;
    }

    public GraphNode? Node(string id) => Nodes().FirstOrDefault(n => n.Id == id);

    /// <summary>
    /// Limite de modulo EXACTO: igualdad o descendiente separado por punto. "alpha" admite
    /// "alpha.child" pero NO "alpha-private" - un prefijo de cadena a secas habria dejado
    /// pasar el segundo, que es justo el negativo que el fixture congela.
    /// </summary>
    public static bool MatchesModuleBoundary(string? value, string required) =>
        !string.IsNullOrEmpty(value) &&
        (string.Equals(value, required, StringComparison.Ordinal) ||
         value.StartsWith(required + ".", StringComparison.Ordinal));

    public sealed record QueryResult(string Status, List<string> Hits, List<List<List<string>>> Traversed);

    public QueryResult Query(string seed, string collection, GraphContext? context, string? filterModule, int hops)
    {
        if (context is null || context.Mode is not ("authorized" or "local"))
            return new QueryResult("invalid_context", new List<string>(), new List<List<List<string>>>());

        if (context.Mode == "authorized" && (context.Tenant is null || context.Module is null))
            return new QueryResult("invalid_context", new List<string>(), new List<List<List<string>>>());

        var nodes = Nodes().ToDictionary(n => n.Id, StringComparer.Ordinal);

        bool Admit(GraphNode node)
        {
            if (!string.Equals(node.Collection, collection, StringComparison.Ordinal)) return false;
            if (context.Mode == "authorized")
            {
                if (!string.Equals(node.Tenant, context.Tenant, StringComparison.Ordinal)) return false;
                if (!MatchesModuleBoundary(node.Module, context.Module!)) return false;
            }
            return filterModule is null || MatchesModuleBoundary(node.Module, filterModule);
        }

        var empty = Enumerable.Range(0, hops).Select(_ => new List<List<string>>()).ToList();
        if (!nodes.TryGetValue(seed, out var seedNode) || !Admit(seedNode))
            return new QueryResult("denied_seed", new List<string>(), empty);

        var edgesBySource = Edges().GroupBy(e => e.Source, StringComparer.Ordinal)
                                   .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var visited = new HashSet<string>(StringComparer.Ordinal) { seed };
        var frontier = new List<string> { seed };
        var traversed = new List<List<List<string>>>();

        for (var hop = 0; hop < hops; hop++)
        {
            var hopEdges = new List<List<string>>();
            var next = new List<string>();

            foreach (var source in frontier.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!edgesBySource.TryGetValue(source, out var outgoing)) continue;
                foreach (var edge in outgoing.OrderBy(e => e.Target, StringComparer.Ordinal))
                {
                    if (visited.Contains(edge.Target)) continue;
                    if (!nodes.TryGetValue(edge.Target, out var target) || !Admit(target)) continue;
                    hopEdges.Add(new List<string> { source, edge.Target });
                    visited.Add(edge.Target);
                    next.Add(edge.Target);
                }
            }

            traversed.Add(hopEdges.OrderBy(e => e[0], StringComparer.Ordinal)
                                  .ThenBy(e => e[1], StringComparer.Ordinal).ToList());
            frontier = next;
        }

        var hits = visited.Where(id => id != seed).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new QueryResult("ok", hits, traversed);
    }

    /// <summary>
    /// Destinos directos de una semilla en modo local, resueltos en una sola consulta SQL.
    /// No reutiliza <see cref="Query"/> a proposito: aquel materializa el grafo entero para
    /// razonar sobre varios saltos, y con 23.058 nodos eso convertiria la latencia del brazo
    /// en una medida del runner y no del mecanismo.
    /// </summary>
    public List<string> Neighbours(string seed, string collection)
    {
        using var seedCommand = _connection.CreateCommand();
        seedCommand.CommandText = "SELECT 1 FROM nodes WHERE id = $i AND collection = $c";
        seedCommand.Parameters.AddWithValue("$i", seed);
        seedCommand.Parameters.AddWithValue("$c", collection);
        if (seedCommand.ExecuteScalar() is null) return new List<string>();

        var result = new List<string>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT e.target FROM edges e
            JOIN nodes n ON n.id = e.target
            WHERE e.source = $i AND n.collection = $c AND e.target <> $i
            ORDER BY e.target
            """;
        command.Parameters.AddWithValue("$i", seed);
        command.Parameters.AddWithValue("$c", collection);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public void Dispose() => _connection.Dispose();
}
