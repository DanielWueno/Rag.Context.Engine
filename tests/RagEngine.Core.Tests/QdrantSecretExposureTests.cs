using Microsoft.Extensions.Logging;
using Qdrant.Client;
using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 12.8-secretos-y-tls: <c>Qdrant:ApiKey</c> viaja como metadata gRPC del cliente,
/// nunca como algo que <see cref="QdrantVectorStore"/> deba formatear en un mensaje de
/// log o en una excepción — pero eso es una garantía de comportamiento en TIEMPO DE
/// EJECUCIÓN, no algo que un escaneo textual del código fuente pueda demostrar (ítem
/// explícitamente NO acepta un grep como evidencia). Este test configura una clave
/// sintética real, ejecuta operaciones reales (éxito contra Qdrant local Y fallo contra
/// un puerto sin nadie escuchando) capturando cada mensaje logueado y cada excepción
/// completa (mensaje + stack + inner exceptions), y falla si la clave aparece en
/// cualquiera de los dos.
/// </summary>
public sealed class QdrantSecretExposureTests
{
    private const string SyntheticApiKey = "clave-sintetica-12-8-nunca-debe-aparecer-en-logs";

    [Fact]
    public async Task Operacion_exitosa_contra_qdrant_real_no_expone_la_api_key_en_los_logs()
    {
        var capturingLogger = new CapturingLogger<QdrantVectorStore>();
        using var client = new QdrantClient("localhost", 6334, https: false, apiKey: SyntheticApiKey);
        var store = new QdrantVectorStore(client, capturingLogger);
        var collection = $"rag-engine-test-secret-exposure-{Guid.NewGuid():N}";

        // Existencia de una colección que nunca se crea: ejercicio real contra Qdrant
        // real (mismo patrón que el resto de harnesses de este proyecto), sin dejar
        // datos huérfanos que limpiar.
        var exists = await store.CollectionExistsAsync(collection);

        Assert.False(exists);
        Assert.DoesNotContain(capturingLogger.Messages, m => m.Contains(SyntheticApiKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fallo_de_conexion_no_expone_la_api_key_en_la_excepcion_ni_en_los_logs()
    {
        var capturingLogger = new CapturingLogger<QdrantVectorStore>();
        // Puerto sin nadie escuchando en loopback: falla rápido con una excepción real
        // de gRPC (Unavailable), sin depender de un servidor de prueba adicional.
        using var client = new QdrantClient("localhost", 1, https: false, apiKey: SyntheticApiKey);
        var store = new QdrantVectorStore(client, capturingLogger);

        var ex = await Record.ExceptionAsync(() => store.ListCollectionsAsync());

        Assert.NotNull(ex);
        var fullExceptionText = ex!.ToString();
        Assert.DoesNotContain(SyntheticApiKey, fullExceptionText, StringComparison.Ordinal);
        Assert.DoesNotContain(capturingLogger.Messages, m => m.Contains(SyntheticApiKey, StringComparison.Ordinal));
    }

    /// <summary>Captura cada mensaje formateado (no la plantilla, el texto final) para inspección.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages => _messages;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
            if (exception is not null)
                _messages.Add(exception.ToString());
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
