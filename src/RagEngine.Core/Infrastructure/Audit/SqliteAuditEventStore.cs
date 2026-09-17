using Microsoft.Data.Sqlite;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.Audit;

/// <summary>
/// Almacén SQLite de eventos de auditoría (ítem 12.11): un único escritor lógico (esta
/// clase), event_id como llave primaria para que reintentar la misma escritura nunca
/// duplique la fila, y WAL para que un lector concurrente (p. ej. una futura CLI de
/// consulta) no bloquee al escritor. Mismo patrón que
/// <see cref="Summary.SummaryCache"/>: cada operación abre su propia conexión —
/// evita compartir un SqliteConnection entre escritores concurrentes (API + CLI +
/// ingesta), que no es thread-safe.
/// </summary>
public sealed class SqliteAuditEventStore : IAuditEventStore
{
    /// <summary>Igual justificación que en SummaryCache: ajuste por conexión, no de la base.</summary>
    private const int BusyTimeoutMs = 5000;

    private readonly string _connectionString;

    private SqliteAuditEventStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>Abre (creando si hace falta) la base y garantiza el esquema. Segura para llamar concurrentemente.</summary>
    public static SqliteAuditEventStore Open(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMs};");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS audit_events (
                event_id       TEXT PRIMARY KEY,
                correlation_id TEXT NOT NULL,
                operation      TEXT NOT NULL,
                actor_type     TEXT NOT NULL,
                actor_id       TEXT NOT NULL,
                collection     TEXT NULL,
                outcome        TEXT NOT NULL,
                detail         TEXT NULL,
                timestamp      TEXT NOT NULL,
                version        INTEGER NOT NULL
            );
            """);
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_audit_events_timestamp ON audit_events(timestamp);");

        // Ítem 12.9: columna añadida después del esquema original de 12.11. Una base
        // creada antes de este ítem no la tiene todavía — se agrega con default 0
        // (sin retención legal) en vez de exigir recrear la base, para no perder
        // eventos ya auditados.
        if (!HasColumn(connection, "audit_events", "legal_hold"))
            Execute(connection, "ALTER TABLE audit_events ADD COLUMN legal_hold INTEGER NOT NULL DEFAULT 0;");

        return new SqliteAuditEventStore(connectionString);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMs};";
        await pragma.ExecuteNonQueryAsync(ct);

        return connection;
    }

    /// <inheritdoc />
    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);

        await using var cmd = connection.CreateCommand();
        // INSERT OR IGNORE, no ON CONFLICT UPDATE: un evento es inmutable una vez
        // escrito. Si el mismo EventId llega dos veces (reintento tras un reinicio o
        // una respuesta perdida), la segunda escritura se descarta silenciosamente en
        // vez de pisar el registro original.
        cmd.CommandText = """
            INSERT OR IGNORE INTO audit_events
                (event_id, correlation_id, operation, actor_type, actor_id, collection, outcome, detail, timestamp, version, legal_hold)
            VALUES
                ($eventId, $correlationId, $operation, $actorType, $actorId, $collection, $outcome, $detail, $timestamp, $version, $legalHold);
            """;
        cmd.Parameters.AddWithValue("$eventId", auditEvent.EventId);
        cmd.Parameters.AddWithValue("$correlationId", auditEvent.CorrelationId);
        cmd.Parameters.AddWithValue("$operation", auditEvent.Operation);
        cmd.Parameters.AddWithValue("$actorType", auditEvent.ActorType);
        cmd.Parameters.AddWithValue("$actorId", auditEvent.ActorId);
        cmd.Parameters.AddWithValue("$collection", (object?)auditEvent.Collection ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome", auditEvent.Outcome.ToString());
        cmd.Parameters.AddWithValue("$detail", (object?)auditEvent.Detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$timestamp", auditEvent.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$version", auditEvent.Version);
        cmd.Parameters.AddWithValue("$legalHold", auditEvent.LegalHold ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEvent>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, correlation_id, operation, actor_type, actor_id, collection, outcome, detail, timestamp, version, legal_hold
            FROM audit_events
            ORDER BY timestamp ASC;
            """;

        var results = new List<AuditEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new AuditEvent
            {
                EventId = reader.GetString(0),
                CorrelationId = reader.GetString(1),
                Operation = reader.GetString(2),
                ActorType = reader.GetString(3),
                ActorId = reader.GetString(4),
                Collection = reader.IsDBNull(5) ? null : reader.GetString(5),
                Outcome = Enum.Parse<AuditOutcome>(reader.GetString(6)),
                Detail = reader.IsDBNull(7) ? null : reader.GetString(7),
                Timestamp = DateTimeOffset.Parse(reader.GetString(8)),
                Version = reader.GetInt32(9),
                LegalHold = reader.GetInt32(10) != 0
            });
        }
        return results;
    }

    /// <inheritdoc />
    public async Task SetLegalHoldAsync(string eventId, bool legalHold, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE audit_events SET legal_hold = $legalHold WHERE event_id = $eventId;";
        cmd.Parameters.AddWithValue("$legalHold", legalHold ? 1 : 0);
        cmd.Parameters.AddWithValue("$eventId", eventId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);

        await using var cmd = connection.CreateCommand();
        // legal_hold = 0 en el WHERE, no en un paso previo: una sola sentencia
        // atómica, sin ventana donde otra escritura pudiera cambiar el hold entre
        // "leer candidatos" y "borrar".
        cmd.CommandText = "DELETE FROM audit_events WHERE timestamp < $olderThan AND legal_hold = 0;";
        cmd.Parameters.AddWithValue("$olderThan", olderThan.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // Columna "name" es el índice 1 en PRAGMA table_info.
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
