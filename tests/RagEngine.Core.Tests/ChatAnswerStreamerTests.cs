using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Retry;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Generation;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 4.8 del plan — parte (c): <see cref="ChatAnswerStreamer"/> no tenía ningún
/// test. Es la única pieza que toca el <see cref="Kernel"/> de Semantic Kernel: arma
/// el <see cref="ChatHistory"/> (sistema + turnos previos + pregunta), lo pasa a
/// <see cref="IChatCompletionService.GetStreamingChatMessageContentsAsync"/> y filtra
/// los fragmentos vacíos del stream de salida.
///
/// El proyecto no referencia Moq — <see cref="FakeChatCompletionService"/> es una
/// implementación manual mínima de <see cref="IChatCompletionService"/> que sólo
/// resuelve el camino de streaming (el único que usa <see cref="ChatAnswerStreamer"/>)
/// y captura el <see cref="ChatHistory"/> recibido para poder hacer aserciones sobre
/// el orden y los roles.
///
/// Ítem 8.e — parte de resiliencia: <see cref="FakeChatCompletionService"/> ahora
/// también puede fallar por intento (via el <c>attemptFactory</c>), y hay una
/// <see cref="ManualTimeProvider"/> propia para probar el circuit breaker sin
/// esperas reales. No se detiene ningún Ollama servido — todo esto corre contra el
/// <see cref="Kernel"/> con un <see cref="IChatCompletionService"/> fake.
/// </summary>
public class ChatAnswerStreamerTests
{
    private static (ChatAnswerStreamer Streamer, FakeChatCompletionService Fake) Construir(
        IEnumerable<StreamingChatMessageContent> fragmentosAEmitir,
        ResiliencePipelineProvider<string>? pipelineProvider = null)
    {
        var fake = new FakeChatCompletionService(fragmentosAEmitir);
        return (ConstruirStreamer(fake, pipelineProvider), fake);
    }

    private static ChatAnswerStreamer ConstruirStreamer(
        FakeChatCompletionService fake,
        ResiliencePipelineProvider<string>? pipelineProvider = null)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(fake);
        var kernel = builder.Build();

        return new ChatAnswerStreamer(
            kernel,
            NullLogger<ChatAnswerStreamer>.Instance,
            pipelineProvider ?? BuildPipelineProvider(b => { })); // sin retry/breaker: pipeline "vacío", pasa todo tal cual
    }

    /// <summary>
    /// Registra el pipeline bajo el mismo nombre que usa
    /// <see cref="ChatAnswerStreamer.ResiliencePipelineName"/> — el mismo mecanismo que
    /// <c>GenerationServiceExtensions.AddRagEngineGeneration</c> usa en producción, sólo
    /// que aquí cada test arma su propio <see cref="IServiceProvider"/> aislado en vez de
    /// depender del contenedor completo.
    /// </summary>
    private static ResiliencePipelineProvider<string> BuildPipelineProvider(
        Action<ResiliencePipelineBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddResiliencePipeline(ChatAnswerStreamer.ResiliencePipelineName, configure);
        return services.BuildServiceProvider().GetRequiredService<ResiliencePipelineProvider<string>>();
    }

    /// <summary>
    /// Verifica que cada <see cref="ChatTurn"/> de <c>history</c> se mapea al
    /// <see cref="AuthorRole"/> correcto: <see cref="ChatRole.User"/> → <see cref="AuthorRole.User"/>,
    /// <see cref="ChatRole.Assistant"/> → <see cref="AuthorRole.Assistant"/>.
    ///
    /// Mutación que este test detecta: invertir la rama del <c>if (turn.Role == ChatRole.User)</c>
    /// en <c>ChatAnswerStreamer.StreamAsync</c> (llamar <c>AddAssistantMessage</c> cuando
    /// el turno es de usuario y viceversa) — los roles capturados saldrían intercambiados.
    /// </summary>
    [Fact]
    public async Task MapeaCadaTurnoAlAuthorRoleCorrecto()
    {
        var (streamer, fake) = Construir(new[]
        {
            new StreamingChatMessageContent(AuthorRole.Assistant, "respuesta del modelo"),
        });

        var history = new List<ChatTurn>
        {
            new(ChatRole.User, "primera pregunta"),
            new(ChatRole.Assistant, "primera respuesta"),
            new(ChatRole.User, "segunda pregunta"),
        };

        await foreach (var _ in streamer.StreamAsync(
            "prompt de sistema", "pregunta final", history, CancellationToken.None))
        {
        }

        Assert.NotNull(fake.CapturedHistory);
        var mensajes = fake.CapturedHistory!;

        // [0]=system, [1..3]=los tres turnos de history, [4]=query final (usuario).
        Assert.Equal(5, mensajes.Count);
        Assert.Equal(AuthorRole.User, mensajes[1].Role);
        Assert.Equal("primera pregunta", mensajes[1].Content);
        Assert.Equal(AuthorRole.Assistant, mensajes[2].Role);
        Assert.Equal("primera respuesta", mensajes[2].Content);
        Assert.Equal(AuthorRole.User, mensajes[3].Role);
        Assert.Equal("segunda pregunta", mensajes[3].Content);
    }

    /// <summary>
    /// El stream del fake mezcla fragmentos con contenido real, <c>null</c> y
    /// <c>""</c>. Sólo los fragmentos con contenido real deben llegar al
    /// <see cref="IAsyncEnumerable{T}"/> devuelto por <c>StreamAsync</c>, en el mismo
    /// orden relativo.
    ///
    /// Mutación que este test detecta: quitar el
    /// <c>if (!string.IsNullOrEmpty(streamChunk.Content))</c> de <c>ChatAnswerStreamer.StreamAsync</c>
    /// — los fragmentos vacíos/null aparecerían en la lista resultante.
    /// </summary>
    [Fact]
    public async Task FiltraFragmentosVaciosONulos_YConservaElOrdenDeLosReales()
    {
        var (streamer, _) = Construir(new[]
        {
            new StreamingChatMessageContent(AuthorRole.Assistant, "primero"),
            new StreamingChatMessageContent(AuthorRole.Assistant, null),
            new StreamingChatMessageContent(AuthorRole.Assistant, ""),
            new StreamingChatMessageContent(AuthorRole.Assistant, "segundo"),
        });

        var fragmentos = new List<string>();
        await foreach (var fragmento in streamer.StreamAsync(
            "prompt de sistema", "pregunta", history: null, CancellationToken.None))
        {
            fragmentos.Add(fragmento);
        }

        Assert.Equal(new[] { "primero", "segundo" }, fragmentos);
    }

    /// <summary>
    /// El primer mensaje del <see cref="ChatHistory"/> armado debe ser el
    /// <c>systemPrompt</c> con rol de sistema, y el último debe ser <c>query</c> como
    /// turno de usuario — ANTES de cualquier turno de <c>history</c> en el primer caso,
    /// DESPUÉS de todos ellos en el segundo.
    ///
    /// Mutación que este test detecta: mover <c>chatHistory.AddUserMessage(query)</c>
    /// antes del bucle de <c>history</c> en <c>ChatAnswerStreamer.StreamAsync</c> — el
    /// último mensaje capturado dejaría de ser el query.
    /// </summary>
    [Fact]
    public async Task ElSystemPromptVaPrimeroYElQueryVaAlFinal()
    {
        var (streamer, fake) = Construir(new[]
        {
            new StreamingChatMessageContent(AuthorRole.Assistant, "respuesta"),
        });

        var history = new List<ChatTurn>
        {
            new(ChatRole.User, "turno previo 1"),
            new(ChatRole.Assistant, "turno previo 2"),
        };

        await foreach (var _ in streamer.StreamAsync(
            "SYSTEM-PROMPT-X", "QUERY-FINAL", history, CancellationToken.None))
        {
        }

        Assert.NotNull(fake.CapturedHistory);
        var mensajes = fake.CapturedHistory!;

        Assert.Equal(4, mensajes.Count); // system + 2 turnos previos + query
        Assert.Equal(AuthorRole.System, mensajes[0].Role);
        Assert.Equal("SYSTEM-PROMPT-X", mensajes[0].Content);

        Assert.Equal(AuthorRole.User, mensajes[^1].Role);
        Assert.Equal("QUERY-FINAL", mensajes[^1].Content);
    }

    /// <summary>
    /// Implementación manual mínima de <see cref="IChatCompletionService"/>: sólo
    /// resuelve <see cref="GetStreamingChatMessageContentsAsync"/>, que es lo único
    /// que <see cref="ChatAnswerStreamer"/> invoca. Captura el <see cref="ChatHistory"/>
    /// recibido para que los tests puedan inspeccionarlo.
    ///
    /// Ítem 8.e: <c>attemptFactory</c> recibe el número de intento (1-based, cuenta
    /// TODAS las invocaciones de <see cref="GetStreamingChatMessageContentsAsync"/> —
    /// tanto reintentos dentro de un mismo <c>StreamAsync</c> como llamadas
    /// separadas) y decide qué stream (o qué falla) devolver para ese intento. El
    /// constructor de una sola lista sigue existiendo para no tocar los tests
    /// anteriores a este ítem.
    /// </summary>
    private sealed class FakeChatCompletionService : IChatCompletionService
    {
        private readonly Func<int, IAsyncEnumerable<StreamingChatMessageContent>> _attemptFactory;

        public FakeChatCompletionService(IEnumerable<StreamingChatMessageContent> fragmentosAEmitir)
        {
            var materializado = fragmentosAEmitir.ToList();
            _attemptFactory = _ => EmitirFragmentos(materializado);
        }

        public FakeChatCompletionService(Func<int, IAsyncEnumerable<StreamingChatMessageContent>> attemptFactory)
        {
            _attemptFactory = attemptFactory;
        }

        /// <summary>Cuántas veces se llamó a <see cref="GetStreamingChatMessageContentsAsync"/> — cada
        /// llamada abre un stream NUEVO, así que esto cuenta tanto reintentos del pipeline
        /// de resiliencia como invocaciones separadas de <c>StreamAsync</c>.</summary>
        public int CallCount { get; private set; }

        /// <summary>Capturado en la ÚLTIMA llamada de streaming, para aserciones de orden/rol.</summary>
        public ChatHistory? CapturedHistory { get; private set; }

        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException(
                "ChatAnswerStreamer sólo usa el camino de streaming; este método no debería invocarse.");

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            CapturedHistory = chatHistory;
            CallCount++;
            return _attemptFactory(CallCount);
        }

        public static async IAsyncEnumerable<StreamingChatMessageContent> EmitirFragmentos(
            IEnumerable<StreamingChatMessageContent> fragmentos)
        {
            foreach (var fragmento in fragmentos)
            {
                await Task.Yield(); // fuerza asincronía real, no una lista ya materializada
                yield return fragmento;
            }
        }

        /// <summary>Un intento que falla ANTES de emitir ningún fragmento (simula una conexión
        /// rechazada u otra falla al abrir la respuesta de Ollama).</summary>
        public static async IAsyncEnumerable<StreamingChatMessageContent> Falla(Exception ex)
        {
            await Task.Yield();
            throw ex;
#pragma warning disable CS0162 // inalcanzable a propósito: sólo hace de este método un iterador
            yield break;
#pragma warning restore CS0162
        }

        /// <summary>Un intento que emite un fragmento y LUEGO falla — simula una respuesta que
        /// ya se abrió (el consumidor ya recibió texto) antes de que la conexión se caiga.</summary>
        public static async IAsyncEnumerable<StreamingChatMessageContent> EmiteYLuegoFalla(
            StreamingChatMessageContent primerFragmento, Exception ex)
        {
            await Task.Yield();
            yield return primerFragmento;
            await Task.Yield();
            throw ex;
        }
    }

    /// <summary>
    /// <see cref="TimeProvider"/> con reloj manual, para probar el circuit breaker sin
    /// depender de esperas reales (<c>BreakDuration</c> real haría el test lento y frágil).
    /// Sólo sobreescribe <see cref="GetUtcNow"/> — el circuit breaker de Polly consulta
    /// el reloj ahí para decidir si ya pasó el <c>BreakDuration</c>.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Avanzar(TimeSpan delta) => _now += delta;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Ítem 8.e — resiliencia del camino de generación conversacional
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Un fallo ANTES de emitir ningún fragmento (conexión rechazada al abrir la
    /// respuesta) SÍ se reintenta: nada llegó al consumidor todavía, así que repetir
    /// la llamada completa no duplica nada.
    ///
    /// Mutación que este test detecta: quitar el <c>_resiliencePipeline.ExecuteAsync</c>
    /// que envuelve el primer <c>MoveNextAsync</c> en <c>ChatAnswerStreamer.StreamAsync</c>
    /// — la excepción de los dos primeros intentos se propagaría sin darle chance al
    /// tercero, que es el que success.
    /// </summary>
    [Fact]
    public async Task FallaAntesDeAbrirElStream_SeReintentaYTerminaEmitiendoLosFragmentos()
    {
        var fake = new FakeChatCompletionService(intento => intento switch
        {
            1 or 2 => FakeChatCompletionService.Falla(new InvalidOperationException("conexión rechazada")),
            _ => FakeChatCompletionService.EmitirFragmentos(new[]
            {
                new StreamingChatMessageContent(AuthorRole.Assistant, "hola"),
                new StreamingChatMessageContent(AuthorRole.Assistant, "mundo"),
            }),
        });

        var pipelineProvider = BuildPipelineProvider(builder =>
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
            });
        });

        var streamer = ConstruirStreamer(fake, pipelineProvider);

        var fragmentos = new List<string>();
        await foreach (var fragmento in streamer.StreamAsync(
            "prompt", "pregunta", history: null, CancellationToken.None))
        {
            fragmentos.Add(fragmento);
        }

        Assert.Equal(new[] { "hola", "mundo" }, fragmentos);
        Assert.Equal(3, fake.CallCount); // 2 fallidos + 1 exitoso
    }

    /// <summary>
    /// Un fallo DESPUÉS de que el stream ya emitió contenido NO se reintenta: la
    /// excepción se propaga tal cual, sin invocar a Ollama de nuevo (evita duplicar
    /// texto que el llamador —y, río abajo, el SSE del endpoint— ya recibió).
    ///
    /// Mutación que este test detecta: envolver TODO el bucle de enumeración (no sólo
    /// el primer <c>MoveNextAsync</c>) en <c>_resiliencePipeline.ExecuteAsync</c> —
    /// <c>CallCount</c> pasaría de 1 a más de 1, y "primero" aparecería duplicado.
    /// </summary>
    [Fact]
    public async Task FallaDespuesDeAbrirElStream_NoReintentaYPropagaLaExcepcionSinDuplicar()
    {
        var fallaEsperada = new InvalidOperationException("conexión caída a mitad de stream");
        var fake = new FakeChatCompletionService(_ => FakeChatCompletionService.EmiteYLuegoFalla(
            new StreamingChatMessageContent(AuthorRole.Assistant, "primero"), fallaEsperada));

        var pipelineProvider = BuildPipelineProvider(builder =>
        {
            // Reintentos generosos a propósito: si el código reintentara después de
            // abrir el stream, este pipeline se lo permitiría y el test lo detectaría
            // por CallCount/fragmentos duplicados.
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                MaxRetryAttempts = 5,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
            });
        });

        var streamer = ConstruirStreamer(fake, pipelineProvider);

        var fragmentos = new List<string>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var fragmento in streamer.StreamAsync(
                "prompt", "pregunta", history: null, CancellationToken.None))
            {
                fragmentos.Add(fragmento);
            }
        });

        Assert.Same(fallaEsperada, ex);
        Assert.Equal(new[] { "primero" }, fragmentos);
        Assert.Equal(1, fake.CallCount); // ni un reintento tras el primer fragmento
    }

    /// <summary>
    /// El circuit breaker abre tras fallos consecutivos (sin darle más chances a
    /// Ollama), y cierra solo cuando pasó <c>BreakDuration</c> Y la llamada de
    /// prueba (half-open) tiene éxito — verificado con un <see cref="ManualTimeProvider"/>
    /// en vez de esperar en tiempo real.
    ///
    /// Mutación que este test detecta: que el breaker no bloquee llamadas mientras está
    /// abierto (la tercera llamada invocaría a Ollama, <c>CallCount</c> subiría a 3 antes
    /// de tiempo) o que no se recupere tras <c>BreakDuration</c> (la cuarta llamada
    /// seguiría lanzando <see cref="BrokenCircuitException"/> en vez de tener éxito).
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_AbreTrasFallosConsecutivosYCierraDespuesDeBreakDurationConExito()
    {
        var timeProvider = new ManualTimeProvider();
        var fake = new FakeChatCompletionService(intento => intento switch
        {
            1 or 2 => FakeChatCompletionService.Falla(new InvalidOperationException("Ollama caído")),
            _ => FakeChatCompletionService.EmitirFragmentos(new[]
            {
                new StreamingChatMessageContent(AuthorRole.Assistant, "recuperado"),
            }),
        });

        var pipelineProvider = BuildPipelineProvider(builder =>
        {
            builder.TimeProvider = timeProvider;
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                FailureRatio = 1.0,
                SamplingDuration = TimeSpan.FromSeconds(10),
                MinimumThroughput = 2,
                BreakDuration = TimeSpan.FromSeconds(30),
            });
        });

        var streamer = ConstruirStreamer(fake, pipelineProvider);

        async Task<List<string>> EjecutarUnaVezAsync()
        {
            var resultado = new List<string>();
            await foreach (var fragmento in streamer.StreamAsync(
                "prompt", "pregunta", history: null, CancellationToken.None))
            {
                resultado.Add(fragmento);
            }
            return resultado;
        }

        // Dos fallos consecutivos alcanzan MinimumThroughput con FailureRatio=1.0: el
        // breaker abre.
        await Assert.ThrowsAsync<InvalidOperationException>(EjecutarUnaVezAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(EjecutarUnaVezAsync);

        // Con el breaker abierto, una tercera llamada NO debe tocar a Ollama.
        await Assert.ThrowsAsync<BrokenCircuitException>(EjecutarUnaVezAsync);
        Assert.Equal(2, fake.CallCount); // la llamada bloqueada no incrementó el contador

        // Pasado BreakDuration, el breaker deja pasar una llamada de prueba (half-open).
        timeProvider.Avanzar(TimeSpan.FromSeconds(31));

        var fragmentosRecuperados = await EjecutarUnaVezAsync();
        Assert.Equal(new[] { "recuperado" }, fragmentosRecuperados);
        Assert.Equal(3, fake.CallCount);

        // El breaker ya cerró: otra llamada exitosa no debería volver a bloquearse.
        var fragmentosSiguientes = await EjecutarUnaVezAsync();
        Assert.Equal(new[] { "recuperado" }, fragmentosSiguientes);
        Assert.Equal(4, fake.CallCount);
    }
}
