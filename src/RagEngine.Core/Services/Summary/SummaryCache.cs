using Microsoft.Data.Sqlite;

namespace RagEngine.Core.Services.Summary;

/// <summary>
/// Caché SQLite de resúmenes de negocio, compartida entre TODAS las colecciones
/// (clave = content_hash + prompt_version, no por colección: dos repos distintos
/// pueden compartir un chunk de boilerplate idéntico). Sobrevive a un `--force`
/// (a diferencia de guardar el resumen como payload dentro de Qdrant, que se
/// pierde al recrear la colección).
///
/// Cada operación abre su propia conexión (pooled por Microsoft.Data.Sqlite bajo
/// el mismo connection string) — evita compartir un único SqliteConnection entre
/// los workers concurrentes de la Fase 2 de ingesta, que no es thread-safe.
/// </summary>
public sealed class SummaryCache
{
    private readonly string _connectionString;
    private readonly string _promptVersion;

    private SummaryCache(string connectionString, string promptVersion)
    {
        _connectionString = connectionString;
        _promptVersion = promptVersion;
    }

    /// <summary>Abre (creando si hace falta) la base y garantiza el esquema. Segura para llamar concurrentemente.</summary>
    public static SummaryCache Open(string path, string promptVersion)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA busy_timeout = 5000;");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS resumen_cache (
                content_hash   TEXT NOT NULL,
                prompt_version TEXT NOT NULL,
                resumen        TEXT NOT NULL,
                sin_negocio    INTEGER NOT NULL,
                generated_at   TEXT NOT NULL,
                PRIMARY KEY (content_hash, prompt_version)
            );
            """);

        return new SummaryCache(connectionString, promptVersion);
    }

    /// <summary>
    /// Hit → (true, resumen) o (true, null) si el hit fue el centinela SIN_CONTENIDO_DE_NEGOCIO.
    /// Miss → (false, null): nunca generado, o el intento anterior falló (los fallos no se cachean).
    /// </summary>
    public async Task<(bool Found, string? Summary)> TryGetAsync(string contentHash, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT resumen, sin_negocio FROM resumen_cache WHERE content_hash = $hash AND prompt_version = $pv;";
        cmd.Parameters.AddWithValue("$hash", contentHash);
        cmd.Parameters.AddWithValue("$pv", _promptVersion);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (false, null);

        var sinNegocio = reader.GetInt64(1) != 0;
        return (true, sinNegocio ? null : reader.GetString(0));
    }

    /// <summary>Guarda un resumen (o null para el centinela). No llamar en fallos: un fallo debe poder reintentarse.</summary>
    public async Task SetAsync(string contentHash, string? summaryOrSentinel, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO resumen_cache (content_hash, prompt_version, resumen, sin_negocio, generated_at)
            VALUES ($hash, $pv, $resumen, $sinNegocio, $now)
            ON CONFLICT(content_hash, prompt_version) DO UPDATE SET
                resumen = excluded.resumen, sin_negocio = excluded.sin_negocio, generated_at = excluded.generated_at;
            """;
        cmd.Parameters.AddWithValue("$hash", contentHash);
        cmd.Parameters.AddWithValue("$pv", _promptVersion);
        cmd.Parameters.AddWithValue("$resumen", summaryOrSentinel ?? "");
        cmd.Parameters.AddWithValue("$sinNegocio", summaryOrSentinel is null ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
