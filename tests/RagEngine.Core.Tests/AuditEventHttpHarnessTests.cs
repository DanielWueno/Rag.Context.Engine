using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Audit;
using RagEngine.Core.Infrastructure.Authorization;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 12.11: fija el oráculo de auditoría vía HTTP real sobre <c>/api/search</c> —
/// los cuatro outcomes que exige la ficha (éxito, denegada, fallida, cancelada) dejan
/// EXACTAMENTE un evento persistido con <c>actor_type=local_operator</c>, y reintentar
/// la misma escritura (mismo EventId) nunca duplica la fila, incluso tras "reabrir"
/// el store (nuevo <see cref="SqliteAuditEventStore.Open"/> sobre el mismo archivo,
/// simulando un reinicio del proceso).
///
/// Se usa <c>/api/search</c> (no <c>/api/ask</c>) porque sólo depende de
/// <see cref="IVectorStoreAdmin"/> + <see cref="ISemanticRetriever"/> — sin necesidad
/// de simular el stack completo de generación (Kernel/Ollama) para fijar el
/// comportamiento de auditoría, que es transversal a ambos endpoints (misma función
/// <c>RecordQueryAuditAsync</c> en <c>Program.cs</c>).
/// </summary>
public sealed class AuditEventHttpHarnessTests : IAsyncLifetime
{
    private readonly string _auditDbPath =
        Path.Combine(Path.GetTempPath(), $"rag-audit-http-{Guid.NewGuid():N}.sqlite3");
    private readonly string _cachePath =
        Path.Combine(Path.GetTempPath(), $"rag-audit-http-cache-{Guid.NewGuid():N}.sqlite3");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var path in new[] { _auditDbPath, _cachePath })
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
        return Task.CompletedTask;
    }

    private AuditFactory NewFactory(AuthorizationMode mode, FixtureStore store, FixtureRetriever retriever) =>
        new(mode, store, retriever, _auditDbPath, _cachePath);

    private async Task<List<AuditEvent>> ReadEventsAsync() =>
        (await SqliteAuditEventStore.Open(_auditDbPath).ListAsync()).ToList();

    [Fact]
    public async Task Consulta_exitosa_deja_un_evento_success_con_actor_local()
    {
        var retriever = new FixtureRetriever();
        await using var factory = NewFactory(AuthorizationMode.Local, new FixtureStore(null), retriever);
        using var http = factory.CreateClient();

        using var response = await http.PostAsJsonAsync("/api/search", new { query = "hola", collection = "col-a" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var evt = Assert.Single(await ReadEventsAsync());
        Assert.Equal(AuditOutcome.Success, evt.Outcome);
        Assert.Equal(AuditOperations.QuerySearch, evt.Operation);
        Assert.Equal(AuditActor.TypeLocalOperator, evt.ActorType);
        Assert.Equal("col-a", evt.Collection);
        Assert.False(string.IsNullOrWhiteSpace(evt.ActorId));
        Assert.False(string.IsNullOrWhiteSpace(evt.CorrelationId));
        Assert.False(string.IsNullOrWhiteSpace(evt.EventId));
        Assert.Equal(AuditEvent.CurrentVersion, evt.Version);
    }

    /// <summary>
    /// Sin manifiesto publicado y en modo Empresarial, CollectionAuthorizationService
    /// rechaza SIEMPRE (salvo administrador) — el caso más simple para forzar un 403
    /// real sin tener que simular un actor con scopes insuficientes.
    /// </summary>
    [Fact]
    public async Task Consulta_denegada_en_modo_empresarial_deja_un_evento_denied_sin_invocar_el_retriever()
    {
        var retriever = new FixtureRetriever();
        await using var factory = NewFactory(AuthorizationMode.Empresarial, new FixtureStore(null), retriever);
        using var http = factory.CreateClient();

        using var response = await http.PostAsJsonAsync("/api/search", new { query = "hola", collection = "col-b" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, retriever.Calls);
        var evt = Assert.Single(await ReadEventsAsync());
        Assert.Equal(AuditOutcome.Denied, evt.Outcome);
        // Contrato del ítem: ni siquiera en modo Empresarial se infiere una identidad
        // corporativa — el actor auditado sigue siendo local_operator.
        Assert.Equal(AuditActor.TypeLocalOperator, evt.ActorType);
    }

    [Fact]
    public async Task Consulta_fallida_deja_un_evento_failed_con_detalle_del_error()
    {
        var retriever = new FixtureRetriever { Failure = new InvalidOperationException("fixture boom") };
        await using var factory = NewFactory(AuthorizationMode.Local, new FixtureStore(null), retriever);
        using var http = factory.CreateClient();

        using var response = await http.PostAsJsonAsync("/api/search", new { query = "hola", collection = "col-c" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var evt = Assert.Single(await ReadEventsAsync());
        Assert.Equal(AuditOutcome.Failed, evt.Outcome);
        Assert.Contains("fixture boom", evt.Detail);
    }

    [Fact]
    public async Task Consulta_cancelada_por_el_cliente_deja_un_evento_cancelled()
    {
        var retriever = new FixtureRetriever { HangUntilCancelled = true };
        await using var factory = NewFactory(AuthorizationMode.Local, new FixtureStore(null), retriever);
        using var http = factory.CreateClient();

        using var cts = new CancellationTokenSource();
        var postTask = http.PostAsJsonAsync("/api/search", new { query = "hola", collection = "col-d" }, cts.Token);
        await retriever.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => postTask);

        // El cliente ya recibió la cancelación; el servidor termina de escribir el
        // evento de forma asíncrona — se sondea en vez de asumir que ya está.
        AuditEvent? evt = null;
        for (var i = 0; i < 50 && evt is null; i++)
        {
            await Task.Delay(100);
            evt = (await ReadEventsAsync()).SingleOrDefault();
        }
        Assert.NotNull(evt);
        Assert.Equal(AuditOutcome.Cancelled, evt!.Outcome);
    }

    /// <summary>
    /// El escenario de "reinicio/reintento" que exige la ficha: reabrir el store sobre
    /// el mismo archivo (como si el proceso hubiera reiniciado) y reintentar la MISMA
    /// escritura no debe duplicar la fila ni pisar el registro original.
    /// </summary>
    [Fact]
    public async Task Reintentar_el_mismo_event_id_tras_reabrir_el_store_no_duplica_ni_sobrescribe()
    {
        var evento = new AuditEvent
        {
            EventId = "evt-fixed",
            CorrelationId = "corr-1",
            Operation = AuditOperations.QuerySearch,
            ActorType = AuditActor.TypeLocalOperator,
            ActorId = "tester",
            Collection = "col-x",
            Outcome = AuditOutcome.Success,
            Timestamp = DateTimeOffset.UtcNow,
            Version = AuditEvent.CurrentVersion
        };
        await SqliteAuditEventStore.Open(_auditDbPath).RecordAsync(evento);

        // "Reinicio": una instancia NUEVA del store sobre el mismo archivo.
        var reabierto = SqliteAuditEventStore.Open(_auditDbPath);
        await reabierto.RecordAsync(evento with { Detail = "reintento tras respuesta perdida" });

        var eventos = await reabierto.ListAsync();
        var unico = Assert.Single(eventos);
        Assert.Null(unico.Detail); // se conserva la escritura ORIGINAL, no la del reintento
    }

    private sealed class AuditFactory(
        AuthorizationMode mode, FixtureStore store, FixtureRetriever retriever, string auditDbPath, string cachePath)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("AuditHarness");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authorization:Mode"] = mode.ToString(),
                ["Audit:DbPath"] = auditDbPath,
                ["Ingestion:ResumenCachePath"] = cachePath
            }));
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IVectorStoreAdmin>(store));
                services.Replace(ServiceDescriptor.Singleton<ISemanticRetriever>(retriever));
            });
        }
    }

    private sealed class FixtureRetriever : ISemanticRetriever
    {
        internal int Calls;
        internal Exception? Failure;
        internal bool HangUntilCancelled;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
            string query, RetrievalOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            if (Failure is not null)
                throw Failure;
            if (HangUntilCancelled)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }
    }

    /// <summary>Sólo GetManifestAsync importa para /api/search — el resto no debería invocarse.</summary>
    private sealed class FixtureStore(CollectionManifest? manifest) : IVectorStoreAdmin
    {
        public Task<CollectionManifest?> GetManifestAsync(string collectionName, CancellationToken ct = default) =>
            Task.FromResult(manifest);
        public Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CollectionHealthReport> GetCollectionHealthAsync(string collectionName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task EnsureCollectionAsync(string collectionName, int dimension, bool includeSummaryVector = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecreateCollectionAsync(string collectionName, int dimension, bool includeSummaryVector = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasSummaryVectorAsync(string collectionName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasDefinedSymbolsIndexAsync(string collectionName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CollectionSchemaReport>> InspectCollectionSchemasAsync(int? expectedDimension, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertManifestAsync(string collectionName, CollectionManifest manifest, CancellationToken ct = default) => throw new NotSupportedException();
        public Task EnsureModelCompatibleAsync(string collectionName, string expectedModelOnnxSha256, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
