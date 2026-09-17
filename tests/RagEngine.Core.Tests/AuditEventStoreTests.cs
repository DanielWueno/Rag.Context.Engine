using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Audit;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 12.11: fija las dos propiedades de las que depende la auditoría local —
/// persiste sin depender de un IDP corporativo, y reintentar la misma escritura
/// (mismo EventId, p. ej. tras un reinicio con la respuesta original perdida) NUNCA
/// duplica la fila ni pisa el registro original. Son hermeticos a propósito, igual
/// que <see cref="SummaryCacheTests"/>: cada test usa su propio archivo temporal.
/// </summary>
public class AuditEventStoreTests : IDisposable
{
    private readonly string _rutaTemporal =
        Path.Combine(Path.GetTempPath(), $"ragengine-audit-test-{Guid.NewGuid():N}.sqlite3");

    public void Dispose()
    {
        foreach (var sufijo in new[] { "", "-wal", "-shm" })
        {
            var archivo = _rutaTemporal + sufijo;
            if (File.Exists(archivo)) File.Delete(archivo);
        }
    }

    private static AuditEvent Evento(
        string eventId, AuditOutcome outcome, string? detail = null, string? collection = "coleccion-fixture") => new()
    {
        EventId = eventId,
        CorrelationId = $"corr-{eventId}",
        Operation = AuditOperations.QuerySearch,
        ActorType = AuditActor.TypeLocalOperator,
        ActorId = "operador-de-prueba",
        Collection = collection,
        Outcome = outcome,
        Detail = detail,
        Timestamp = DateTimeOffset.UtcNow,
        Version = AuditEvent.CurrentVersion
    };

    [Fact]
    public async Task LoPersistido_SeRecuperaConTodosSusCampos()
    {
        var store = SqliteAuditEventStore.Open(_rutaTemporal);
        var evento = Evento("evt-1", AuditOutcome.Success, detail: "todo bien");

        await store.RecordAsync(evento);
        var recuperado = Assert.Single(await store.ListAsync());

        Assert.Equal(evento.EventId, recuperado.EventId);
        Assert.Equal(evento.CorrelationId, recuperado.CorrelationId);
        Assert.Equal(evento.Operation, recuperado.Operation);
        Assert.Equal(evento.ActorType, recuperado.ActorType);
        Assert.Equal(evento.ActorId, recuperado.ActorId);
        Assert.Equal(evento.Collection, recuperado.Collection);
        Assert.Equal(evento.Outcome, recuperado.Outcome);
        Assert.Equal(evento.Detail, recuperado.Detail);
        Assert.Equal(evento.Version, recuperado.Version);
        // Timestamp viaja como texto ISO ("O") — comparar con tolerancia de milisegundos
        // en vez de exigir igualdad exacta de un roundtrip de DateTimeOffset por string.
        Assert.True((recuperado.Timestamp - evento.Timestamp).Duration() < TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// La propiedad económica del ítem: reintentar la MISMA escritura (mismo EventId)
    /// no debe duplicar la fila. Simula justo el escenario que motiva la ficha —
    /// una respuesta de escritura perdida por un reinicio, seguida de un reintento.
    /// </summary>
    [Fact]
    public async Task ReintentarElMismoEventId_NoDuplicaLaFila()
    {
        var store = SqliteAuditEventStore.Open(_rutaTemporal);
        var original = Evento("evt-reintento", AuditOutcome.Success, detail: "primera escritura");

        await store.RecordAsync(original);
        await store.RecordAsync(original with { Detail = "reintento con la misma escritura" });
        await store.RecordAsync(original with { Detail = "segundo reintento" });

        var eventos = await store.ListAsync();
        var unico = Assert.Single(eventos);
        // Se conserva la escritura ORIGINAL — el evento es inmutable, no hay "última
        // escritura gana".
        Assert.Equal("primera escritura", unico.Detail);
    }

    /// <summary>Reabrir el archivo (simula un reinicio del proceso) conserva lo ya escrito.</summary>
    [Fact]
    public async Task ReabrirElArchivo_ConservaLosEventosYaEscritos()
    {
        var primera = SqliteAuditEventStore.Open(_rutaTemporal);
        await primera.RecordAsync(Evento("evt-persistente", AuditOutcome.Success));

        var segunda = SqliteAuditEventStore.Open(_rutaTemporal);
        var eventos = await segunda.ListAsync();

        var unico = Assert.Single(eventos);
        Assert.Equal("evt-persistente", unico.EventId);
    }

    /// <summary>
    /// Reabrir el store y reintentar con el mismo EventId (el escenario completo de
    /// "reinicio + reintento" que exige la ficha) tampoco duplica.
    /// </summary>
    [Fact]
    public async Task ReabrirYReintentarElMismoEventId_NoDuplica()
    {
        var primera = SqliteAuditEventStore.Open(_rutaTemporal);
        var evento = Evento("evt-reinicio", AuditOutcome.Failed, detail: "fallo antes del reinicio");
        await primera.RecordAsync(evento);

        var segunda = SqliteAuditEventStore.Open(_rutaTemporal);
        await segunda.RecordAsync(evento); // idéntico: mismo EventId, mismo contenido

        var eventos = await segunda.ListAsync();
        Assert.Single(eventos);
    }

    [Theory]
    [InlineData(AuditOutcome.Success)]
    [InlineData(AuditOutcome.Denied)]
    [InlineData(AuditOutcome.Failed)]
    [InlineData(AuditOutcome.Cancelled)]
    public async Task CadaOutcome_SePersisteYSeRecuperaSinConfundirseConOtro(AuditOutcome outcome)
    {
        var store = SqliteAuditEventStore.Open(_rutaTemporal);
        await store.RecordAsync(Evento($"evt-{outcome}", outcome));

        var recuperado = Assert.Single(await store.ListAsync());
        Assert.Equal(outcome, recuperado.Outcome);
    }

    [Fact]
    public async Task EventosDeDosOperacionesDistintas_NoSeMezclanNiSeDuplican()
    {
        var store = SqliteAuditEventStore.Open(_rutaTemporal);
        var consulta = Evento("evt-consulta", AuditOutcome.Success) with { Operation = AuditOperations.QuerySearch };
        var ingesta = Evento("evt-ingesta", AuditOutcome.Success) with
        {
            Operation = AuditOperations.IngestRepository,
            Collection = "coleccion-ingesta"
        };

        await store.RecordAsync(consulta);
        await store.RecordAsync(ingesta);

        var eventos = (await store.ListAsync()).ToDictionary(e => e.EventId);
        Assert.Equal(2, eventos.Count);
        Assert.Equal(AuditOperations.QuerySearch, eventos["evt-consulta"].Operation);
        Assert.Equal(AuditOperations.IngestRepository, eventos["evt-ingesta"].Operation);
        Assert.Equal("coleccion-ingesta", eventos["evt-ingesta"].Collection);
    }

    [Fact]
    public async Task ColeccionNull_SePersisteYSeRecuperaComoNull()
    {
        // No toda operación auditable tiene una colección concreta asociada.
        var store = SqliteAuditEventStore.Open(_rutaTemporal);
        await store.RecordAsync(Evento("evt-sin-coleccion", AuditOutcome.Success, collection: null));

        var recuperado = Assert.Single(await store.ListAsync());
        Assert.Null(recuperado.Collection);
    }
}
