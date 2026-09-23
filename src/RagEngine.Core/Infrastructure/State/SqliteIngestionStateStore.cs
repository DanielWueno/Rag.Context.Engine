using Microsoft.Data.Sqlite;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.State;

/// <summary>
/// Almacén SQLite de estado de ingesta (ítem 13.1). Mismo patrón que
/// <see cref="Audit.SqliteAuditEventStore"/>: WAL + conexión por operación (sin
/// compartir un SqliteConnection entre escritores concurrentes), pero a diferencia
/// de la auditoría (append-only, inmutable), aquí <c>ingestion_documents</c> SÍ se
/// actualiza en el tiempo — un documento transita pending → running → succeeded/failed
/// dentro de la misma corrida, y esta clase hace UPSERT por (run_id, document_key).
/// </summary>
public sealed class SqliteIngestionStateStore : IIngestionStateStore
{
    private const int BusyTimeoutMs = 5000;

    private readonly string _connectionString;

    private SqliteIngestionStateStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>Abre (creando si hace falta) la base y garantiza el esquema. Segura para llamar concurrentemente.</summary>
    public static SqliteIngestionStateStore Open(string path)
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
            CREATE TABLE IF NOT EXISTS ingestion_runs (
                run_id          TEXT PRIMARY KEY,
                collection      TEXT NOT NULL,
                repository_path TEXT NOT NULL,
                actor_id        TEXT NOT NULL,
                status          TEXT NOT NULL,
                started_at      TEXT NOT NULL,
                finished_at     TEXT NULL
            );
            """);
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_ingestion_runs_collection ON ingestion_runs(collection, started_at);");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS ingestion_documents (
                run_id          TEXT NOT NULL,
                document_key    TEXT NOT NULL,
                content_hash    TEXT NULL,
                contract        TEXT NOT NULL,
                actor_id        TEXT NOT NULL,
                status          TEXT NOT NULL,
                chunks_expected INTEGER NULL,
                chunks_indexed  INTEGER NOT NULL DEFAULT 0,
                started_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                detail          TEXT NULL,
                PRIMARY KEY (run_id, document_key)
            );
            """);
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_ingestion_documents_status ON ingestion_documents(run_id, status);");

        return new SqliteIngestionStateStore(connectionString);
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
    public async Task StartRunAsync(IngestionRunRecord run, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO ingestion_runs
                (run_id, collection, repository_path, actor_id, status, started_at, finished_at)
            VALUES
                ($runId, $collection, $repositoryPath, $actorId, $status, $startedAt, $finishedAt);
            """;
        cmd.Parameters.AddWithValue("$runId", run.RunId);
        cmd.Parameters.AddWithValue("$collection", run.Collection);
        cmd.Parameters.AddWithValue("$repositoryPath", run.RepositoryPath);
        cmd.Parameters.AddWithValue("$actorId", run.ActorId);
        cmd.Parameters.AddWithValue("$status", run.Status.ToString());
        cmd.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$finishedAt", (object?)run.FinishedAt?.ToString("O") ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task FinishRunAsync(
        string runId, IngestionRunStatus status, DateTimeOffset finishedAt, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE ingestion_runs
            SET status = $status, finished_at = $finishedAt
            WHERE run_id = $runId;
            """;
        cmd.Parameters.AddWithValue("$status", status.ToString());
        cmd.Parameters.AddWithValue("$finishedAt", finishedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$runId", runId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task UpsertDocumentStateAsync(DocumentIngestionState state, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        // ON CONFLICT UPDATE (a diferencia de audit_events): el estado de un
        // documento SÍ cambia dentro de la misma corrida. Reaplicar el mismo estado
        // (reintento tras un timeout de escritura) dos veces converge al mismo valor
        // — la actualización es idempotente por construcción, no por ignorarla.
        cmd.CommandText = """
            INSERT INTO ingestion_documents
                (run_id, document_key, content_hash, contract, actor_id, status, chunks_expected, chunks_indexed, started_at, updated_at, detail)
            VALUES
                ($runId, $documentKey, $contentHash, $contract, $actorId, $status, $chunksExpected, $chunksIndexed, $startedAt, $updatedAt, $detail)
            ON CONFLICT (run_id, document_key) DO UPDATE SET
                content_hash    = excluded.content_hash,
                contract        = excluded.contract,
                actor_id        = excluded.actor_id,
                status          = excluded.status,
                chunks_expected = excluded.chunks_expected,
                chunks_indexed  = excluded.chunks_indexed,
                updated_at      = excluded.updated_at,
                detail          = excluded.detail;
            """;
        cmd.Parameters.AddWithValue("$runId", state.RunId);
        cmd.Parameters.AddWithValue("$documentKey", state.DocumentKey);
        cmd.Parameters.AddWithValue("$contentHash", (object?)state.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$contract", state.Contract);
        cmd.Parameters.AddWithValue("$actorId", state.ActorId);
        cmd.Parameters.AddWithValue("$status", state.Status.ToString());
        cmd.Parameters.AddWithValue("$chunksExpected", (object?)state.ChunksExpected ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$chunksIndexed", state.ChunksIndexed);
        cmd.Parameters.AddWithValue("$startedAt", state.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updatedAt", state.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$detail", (object?)state.Detail ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentIngestionState>> GetDocumentStatesAsync(
        string runId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, document_key, content_hash, contract, actor_id, status, chunks_expected, chunks_indexed, started_at, updated_at, detail
            FROM ingestion_documents
            WHERE run_id = $runId
            ORDER BY document_key ASC;
            """;
        cmd.Parameters.AddWithValue("$runId", runId);

        var results = new List<DocumentIngestionState>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new DocumentIngestionState
            {
                RunId = reader.GetString(0),
                DocumentKey = reader.GetString(1),
                ContentHash = reader.IsDBNull(2) ? null : reader.GetString(2),
                Contract = reader.GetString(3),
                ActorId = reader.GetString(4),
                Status = Enum.Parse<DocumentIngestionStatus>(reader.GetString(5)),
                ChunksExpected = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                ChunksIndexed = reader.GetInt32(7),
                StartedAt = DateTimeOffset.Parse(reader.GetString(8)),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(9)),
                Detail = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IngestionRunRecord>> ListRunsAsync(
        string collection, int take = 10, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, collection, repository_path, actor_id, status, started_at, finished_at
            FROM ingestion_runs
            WHERE collection = $collection
            ORDER BY started_at DESC
            LIMIT $take;
            """;
        cmd.Parameters.AddWithValue("$collection", collection);
        cmd.Parameters.AddWithValue("$take", take);

        var results = new List<IngestionRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRun(reader));
        }
        return results;
    }

    /// <inheritdoc />
    public async Task<IngestionRunRecord?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, collection, repository_path, actor_id, status, started_at, finished_at
            FROM ingestion_runs
            WHERE run_id = $runId;
            """;
        cmd.Parameters.AddWithValue("$runId", runId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapRun(reader) : null;
    }

    private static IngestionRunRecord MapRun(SqliteDataReader reader) => new()
    {
        RunId = reader.GetString(0),
        Collection = reader.GetString(1),
        RepositoryPath = reader.GetString(2),
        ActorId = reader.GetString(3),
        Status = Enum.Parse<IngestionRunStatus>(reader.GetString(4)),
        StartedAt = DateTimeOffset.Parse(reader.GetString(5)),
        FinishedAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6))
    };

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
