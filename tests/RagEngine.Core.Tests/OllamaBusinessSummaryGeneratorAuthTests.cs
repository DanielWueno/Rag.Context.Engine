using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Polly;
using Polly.Registry;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure;
using RagEngine.Core.Infrastructure.Summary;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 10.6.2 (porte del 17.1 local), punto 5: un 401/403 de Ollama durante la Fase 2 de
/// ingesta (resumen de negocio) debe abortar la ingesta como fallo de CONFIGURACIÓN, no caer
/// al catch genérico de <see cref="OllamaBusinessSummaryGenerator"/> que hoy devuelve
/// <c>Resumen=null</c> por chunk en silencio. Punto 6 (relacionado): la clave nunca debe
/// aparecer en un log, ni siquiera cuando el log documenta el propio fallo de autenticación.
///
/// Extiende el porte original (que solo cubría 401, con un pipeline sin reintentos) con tres
/// casos nuevos que no existían en la rama local porque ésta no tenía los pipelines Polly
/// "ollama-chat"/"ollama-summary" (ítem 8.e) que main sí tiene:
///   - 403 además de 401 (mismo tratamiento de autenticación).
///   - Control negativo: un 500 SÍ sigue la política de reintentos normal (≥2 peticiones),
///     y el resultado NO es la nueva excepción de autenticación.
///   - Exactamente UNA petición HTTP para 401/403, ejercitando el pipeline de reintentos
///     REAL (mismo ShouldHandle que ServiceCollectionExtensions registra para
///     "ollama-summary") para demostrar que la exclusión de 401/403 realmente evita el
///     reintento — no es un efecto casual de usar un pipeline sin retry.
/// </summary>
public class OllamaBusinessSummaryGeneratorAuthTests
{
    /// <summary>
    /// Handler en memoria — nunca toca la red real. Responde SIEMPRE con el código de
    /// estado indicado y cuenta cuántas peticiones recibió (y con qué cabecera Authorization),
    /// lo que permite afirmar "exactamente una petición" o "al menos dos" según el caso.
    /// </summary>
    private sealed class FakeStatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<string?> _cabecerasAuthorizationVistas = [];

        public IReadOnlyList<string?> CabecerasAuthorizationVistas
        {
            get { lock (_lock) return _cabecerasAuthorizationVistas.ToList(); }
        }

        public int PeticionesRecibidas
        {
            get { lock (_lock) return _cabecerasAuthorizationVistas.Count; }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock) _cabecerasAuthorizationVistas.Add(request.Headers.Authorization?.ToString());

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    """{"error": {"message": "fake error", "type": "fake_error"}}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>Captura todo lo que se formatea a través de ILogger, exception incluida, sin tocar disco.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Mensajes { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Mensajes.Add(formatter(state, exception));
            if (exception is not null)
                Mensajes.Add(exception.ToString());
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static ResiliencePipelineProvider<string> SinReintentos()
    {
        // Pipeline vacío (sin retry): la real ("ollama-summary", ver
        // ServiceCollectionExtensions) reintenta 2 veces con 1s de delay sobre
        // cualquier excepción salvo 401/403 — correcto en producción, pero añadiría
        // tiempo muerto a este test sin cambiar lo que se verifica (el 401 original,
        // de un solo caso, no necesita probar la exclusión de reintentos: eso lo hace
        // ConPoliticaDeReintentosReal de abajo). ResiliencePipelineRegistry<TKey>
        // (Polly.Core, Polly.Registry) implementa ResiliencePipelineProvider<TKey>
        // directamente — no hace falta levantar un IServiceCollection solo para esto.
        var registry = new ResiliencePipelineRegistry<string>();
        registry.TryAddBuilder(OllamaBusinessSummaryGenerator.ResiliencePipelineName, (_, _) => { });
        return registry;
    }

    /// <summary>
    /// Pipeline con la MISMA regla ShouldHandle que ServiceCollectionExtensions registra en
    /// producción para "ollama-summary" (excluye HttpOperationException 401/403 de
    /// reintento/conteo) — el delay se acelera a 20ms (en vez del 1s real) para que el
    /// control negativo de 500 no pague ~2s muertos sin cambiar el comportamiento que se
    /// verifica: cuántas veces se invoca el handler y con qué predicado.
    /// </summary>
    private static ResiliencePipelineProvider<string> ConPoliticaDeReintentosReal()
    {
        var registry = new ResiliencePipelineRegistry<string>();
        registry.TryAddBuilder(OllamaBusinessSummaryGenerator.ResiliencePipelineName, (builder, _) =>
        {
            builder.AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex is not HttpOperationException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }),
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(20),
                BackoffType = DelayBackoffType.Constant,
            });
        });
        return registry;
    }

    private static CodeChunk ChunkDePrueba() => new()
    {
        Id = Guid.NewGuid(),
        Content = "public class Foo { public void Bar() {} }",
        EnrichedContent = "// archivo.cs\npublic class Foo { public void Bar() {} }",
        Metadata = new CodeChunkMetadata(
            FilePath: "/repo/src/archivo.cs",
            RelativeFilePath: "src/archivo.cs",
            Language: SourceLanguage.CSharp,
            Namespace: "Repo",
            ClassName: "Foo",
            MethodName: null,
            StartLine: 1,
            EndLine: 1,
            LastModified: DateTimeOffset.UtcNow,
            RepositoryName: "repo-prueba"),
        Type = ChunkType.Class,
        ContentHash = "hash-de-prueba",
    };

    [Fact]
    public async Task Fallo401_AbortaEnVezDeDevolverNull()
    {
        const string claveSintetica = "clave-sintetica-de-test-NO-REAL-9f3a7c";

        var opts = Microsoft.Extensions.Options.Options.Create(new OllamaOptions
        {
            Endpoint = "https://servidor-ia-de-prueba.invalid/v1",
            ModelId = "qwen2.5-coder:14b",
            ApiKey = claveSintetica,
        });

        var handler = new FakeStatusHandler(HttpStatusCode.Unauthorized);
        var logger = new CapturingLogger<OllamaBusinessSummaryGenerator>();

        // Mismo HttpClient que construye producción (OllamaHttpClientFactory.Create),
        // solo con el transporte real sustituido por el handler en memoria — ejercita
        // la resolución de la clave efectiva (ResolveSdkApiKey) igual que el código real.
        using var httpClient = OllamaHttpClientFactory.Create(opts.Value, handler);

        var generator = CrearGeneradorConHttpClientPropio(opts, httpClient, SinReintentos(), logger);

        var excepcion = await Assert.ThrowsAsync<BusinessSummaryAuthenticationException>(
            () => generator.GenerateAsync(ChunkDePrueba()));

        Assert.Contains("401", excepcion.Message);

        // La clave viajó de verdad en la petición (ResolveSdkApiKey + AddOpenAIChatCompletion)...
        Assert.Contains(handler.CabecerasAuthorizationVistas, h => h == $"Bearer {claveSintetica}");

        // ...pero jamás apareció en ningún mensaje de log, ni siquiera en el que
        // documenta el propio 401 (ítem 10.6.2, punto 6).
        Assert.DoesNotContain(logger.Mensajes, m => m.Contains(claveSintetica, StringComparison.Ordinal));
    }

    /// <summary>
    /// Extiende el caso anterior a 403 y, sobre todo, prueba con el pipeline de
    /// reintentos REAL (misma regla ShouldHandle que producción) que un 401/403 termina
    /// en EXACTAMENTE una petición HTTP — la exclusión de la regla realmente evita el
    /// reintento, no es casualidad de usar un pipeline sin retry (como hace el test de
    /// arriba, que sólo cubre el caso simple de 401).
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FalloDeAutenticacion_ConPoliticaDeReintentosReal_AbortaConUnaSolaPeticion(HttpStatusCode statusCode)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new OllamaOptions
        {
            Endpoint = "https://servidor-ia-de-prueba.invalid/v1",
            ModelId = "qwen2.5-coder:14b",
            ApiKey = "clave-sintetica-de-test-irrelevante",
        });

        var handler = new FakeStatusHandler(statusCode);
        var logger = new CapturingLogger<OllamaBusinessSummaryGenerator>();

        using var httpClient = OllamaHttpClientFactory.Create(opts.Value, handler);
        var generator = CrearGeneradorConHttpClientPropio(opts, httpClient, ConPoliticaDeReintentosReal(), logger);

        await Assert.ThrowsAsync<BusinessSummaryAuthenticationException>(
            () => generator.GenerateAsync(ChunkDePrueba()));

        Assert.Equal(1, handler.PeticionesRecibidas);
    }

    /// <summary>
    /// Control negativo del criterio literal del ítem: un 500 (a diferencia de 401/403) NO
    /// se excluye de la política de reintentos de 8.e — el handler fake ve varias peticiones,
    /// y el resultado NUNCA es <see cref="BusinessSummaryAuthenticationException"/> (ni
    /// aborta la ingesta por ese camino): cae al tratamiento de siempre para fallos que no
    /// son de autenticación (un <see cref="BusinessSummaryConnectionException"/> aislado por
    /// la Fase 2, o null si el catch genérico lo absorbe).
    /// </summary>
    [Fact]
    public async Task Fallo500_SigueLaPoliticaDeReintentosNormal_NoEsFalloDeAutenticacion()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new OllamaOptions
        {
            Endpoint = "https://servidor-ia-de-prueba.invalid/v1",
            ModelId = "qwen2.5-coder:14b",
            ApiKey = "clave-sintetica-de-test-irrelevante",
        });

        var handler = new FakeStatusHandler(HttpStatusCode.InternalServerError);
        var logger = new CapturingLogger<OllamaBusinessSummaryGenerator>();

        using var httpClient = OllamaHttpClientFactory.Create(opts.Value, handler);
        var generator = CrearGeneradorConHttpClientPropio(opts, httpClient, ConPoliticaDeReintentosReal(), logger);

        BusinessSummaryResult? resultado = null;
        var excepcionNoDeAutenticacion = await Record.ExceptionAsync(async () =>
        {
            resultado = await generator.GenerateAsync(ChunkDePrueba());
        });

        // Nunca la excepción nueva de autenticación: un 500 es un problema del servidor,
        // no una credencial mal configurada.
        Assert.IsNotType<BusinessSummaryAuthenticationException>(excepcionNoDeAutenticacion);
        if (excepcionNoDeAutenticacion is not null)
            Assert.IsType<BusinessSummaryConnectionException>(excepcionNoDeAutenticacion);
        else
            Assert.Null(resultado); // aislado por el catch genérico: Resumen=null, recuperable al reanudar.

        // La política de 8.e sí reintenta un 500: MaxRetryAttempts=2 sobre el intento
        // inicial produce 3 peticiones; se exige un mínimo de 2 para no acoplar el test
        // a ese número exacto si el ajuste de reintentos cambia en el futuro.
        Assert.True(handler.PeticionesRecibidas >= 2,
            $"Se esperaban al menos 2 peticiones HTTP (retry de la política 'ollama-summary'), hubo {handler.PeticionesRecibidas}.");
    }

    /// <summary>
    /// Construye el generador reusando el constructor real, pero con un HttpClient ya
    /// armado (el de arriba, con el handler fake) en vez de dejar que el constructor cree
    /// el suyo — exige una pequeña puerta de prueba porque el constructor real abre su
    /// propio HttpClient internamente. Se resuelve agregando un segundo constructor
    /// internal visible solo a RagEngine.Core.Tests (mismo patrón que SanitizeSimpleAnswer
    /// y ConfidenceGate, ver RagEngine.Core.csproj InternalsVisibleTo).
    /// </summary>
    private static OllamaBusinessSummaryGenerator CrearGeneradorConHttpClientPropio(
        Microsoft.Extensions.Options.IOptions<OllamaOptions> opts,
        HttpClient httpClient,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<OllamaBusinessSummaryGenerator> logger) =>
        new(opts, httpClient, pipelineProvider, logger);
}
