using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.State;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 13.1: corrección mecánica del backend SQLite de estado de ingesta, aislada
/// del pipeline completo (cubierto por <see cref="IngestionStateHarnessTests"/>).
/// Mismo criterio que <c>AuditEventStoreTests</c> para su contraparte de auditoría:
/// idempotencia de escrituras y forma exacta de las consultas.
/// </summary>
public sealed class IngestionStateStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"rag-engine-test-state-{Guid.NewGuid():N}.sqlite3");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var f = _dbPath + suffix;
            if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task StartRunAsync_reintentado_con_el_mismo_RunId_no_duplica_la_fila()
    {
        var store = SqliteIngestionStateStore.Open(_dbPath);
        var run = new IngestionRunRecord
        {
            RunId = "run-1", Collection = "demo", RepositoryPath = "/repo", ActorId = "operador",
            Status = IngestionRunStatus.Running, StartedAt = DateTimeOffset.UnixEpoch
        };

        await store.StartRunAsync(run);
        await store.StartRunAsync(run with { Status = IngestionRunStatus.Failed }); // reintento, valores distintos: se ignora

        var runs = await store.ListRunsAsync("demo");
        var only = Assert.Single(runs);
        Assert.Equal(IngestionRunStatus.Running, only.Status); // conserva el primero, no el reintento
    }

    [Fact]
    public async Task FinishRunAsync_actualiza_estado_y_fecha_de_cierre()
    {
        var store = SqliteIngestionStateStore.Open(_dbPath);
        await store.StartRunAsync(new IngestionRunRecord
        {
            RunId = "run-1", Collection = "demo", RepositoryPath = "/repo", ActorId = "operador",
            Status = IngestionRunStatus.Running, StartedAt = DateTimeOffset.UnixEpoch
        });

        var finishedAt = DateTimeOffset.UnixEpoch.AddMinutes(5);
        await store.FinishRunAsync("run-1", IngestionRunStatus.Succeeded, finishedAt);

        var run = await store.GetRunAsync("run-1");
        Assert.NotNull(run);
        Assert.Equal(IngestionRunStatus.Succeeded, run!.Status);
        Assert.Equal(finishedAt, run.FinishedAt);
    }

    [Fact]
    public async Task FinishRunAsync_de_una_corrida_inexistente_no_lanza_ni_crea_nada()
    {
        var store = SqliteIngestionStateStore.Open(_dbPath);
        await store.FinishRunAsync("no-existe", IngestionRunStatus.Failed, DateTimeOffset.UnixEpoch);

        Assert.Null(await store.GetRunAsync("no-existe"));
    }

    [Fact]
    public async Task UpsertDocumentStateAsync_transiciona_pending_running_succeeded_sin_duplicar_fila()
    {
        var store = SqliteIngestionStateStore.Open(_dbPath);
        var baseState = new DocumentIngestionState
        {
            RunId = "run-1", DocumentKey = "repo/Foo.cs", Contract = "c1", ActorId = "operador",
            Status = DocumentIngestionStatus.Pending, StartedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch
        };
        await store.UpsertDocumentStateAsync(baseState);
        await store.UpsertDocumentStateAsync(baseState with
        {
            Status = DocumentIngestionStatus.Running, ContentHash = "hash-1", UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1)
        });
        await store.UpsertDocumentStateAsync(baseState with
        {
            Status = DocumentIngestionStatus.Succeeded, ContentHash = "hash-1", ChunksExpected = 3, ChunksIndexed = 3,
            UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(2)
        });

        var states = await store.GetDocumentStatesAsync("run-1");
        var only = Assert.Single(states);
        Assert.Equal(DocumentIngestionStatus.Succeeded, only.Status);
        Assert.Equal("hash-1", only.ContentHash);
        Assert.Equal(3, only.ChunksExpected);
        Assert.Equal(3, only.ChunksIndexed);
    }

    [Fact]
    public async Task GetDocumentStatesAsync_no_mezcla_documentos_de_otra_corrida()
    {
        var store = SqliteIngestionStateStore.Open(_dbPath);
        await store.UpsertDocumentStateAsync(new DocumentIngestionState
        {
            RunId = "run-1", DocumentKey = "repo/A.cs", Contract = "c1", ActorId = "operador",
            Status = DocumentIngestionStatus.Succeeded, StartedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch
        });
        await store.UpsertDocumentStateAsync(new DocumentIngestionState
        {
            RunId = "run-2", DocumentKey = "repo/A.cs", Contract = "c1", ActorId = "operador",
            Status = DocumentIngestionStatus.Failed, StartedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch
        });

        var run1States = await store.GetDocumentStatesAsync("run-1");
        var run2States = await store.GetDocumentStatesAsync("run-2");

        Assert.Equal(DocumentIngestionStatus.Succeeded, Assert.Single(run1States).Status);
        Assert.Equal(DocumentIngestionStatus.Failed, Assert.Single(run2States).Status);
    }

    [Fact]
    public async Task ListRunsAsync_ordena_mas_recientes_primero_y_respeta_take()
    {
        var store = SqliteIngestionStateStore.Open(_dbPath);
        for (var i = 0; i < 5; i++)
        {
            await store.StartRunAsync(new IngestionRunRecord
            {
                RunId = $"run-{i}", Collection = "demo", RepositoryPath = "/repo", ActorId = "operador",
                Status = IngestionRunStatus.Succeeded, StartedAt = DateTimeOffset.UnixEpoch.AddMinutes(i)
            });
        }

        var runs = await store.ListRunsAsync("demo", take: 3);
        Assert.Equal(3, runs.Count);
        Assert.Equal(["run-4", "run-3", "run-2"], runs.Select(r => r.RunId));
    }

    [Fact]
    public async Task ListRunsAsync_no_devuelve_corridas_de_otra_coleccion()
    {
        var store = SqliteIngestionStateStore.Open(_dbPath);
        await store.StartRunAsync(new IngestionRunRecord
        {
            RunId = "run-a", Collection = "coleccion-a", RepositoryPath = "/repo", ActorId = "operador",
            Status = IngestionRunStatus.Succeeded, StartedAt = DateTimeOffset.UnixEpoch
        });
        await store.StartRunAsync(new IngestionRunRecord
        {
            RunId = "run-b", Collection = "coleccion-b", RepositoryPath = "/repo", ActorId = "operador",
            Status = IngestionRunStatus.Succeeded, StartedAt = DateTimeOffset.UnixEpoch
        });

        var runs = await store.ListRunsAsync("coleccion-a");
        Assert.Equal("run-a", Assert.Single(runs).RunId);
    }
}
