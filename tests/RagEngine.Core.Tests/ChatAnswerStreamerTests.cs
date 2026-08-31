using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using RagEngine.Core.Domain;
using RagEngine.Core.Services.Generation;
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
/// </summary>
public class ChatAnswerStreamerTests
{
    private static (ChatAnswerStreamer Streamer, FakeChatCompletionService Fake) Construir(
        IEnumerable<StreamingChatMessageContent> fragmentosAEmitir)
    {
        var fake = new FakeChatCompletionService(fragmentosAEmitir);

        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(fake);
        var kernel = builder.Build();

        var streamer = new ChatAnswerStreamer(kernel, NullLogger<ChatAnswerStreamer>.Instance);
        return (streamer, fake);
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
    /// </summary>
    private sealed class FakeChatCompletionService : IChatCompletionService
    {
        private readonly List<StreamingChatMessageContent> _fragmentosAEmitir;

        public FakeChatCompletionService(IEnumerable<StreamingChatMessageContent> fragmentosAEmitir)
        {
            _fragmentosAEmitir = fragmentosAEmitir.ToList();
        }

        /// <summary>Capturado en la (única) llamada de streaming, para aserciones de orden/rol.</summary>
        public ChatHistory? CapturedHistory { get; private set; }

        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException(
                "ChatAnswerStreamer sólo usa el camino de streaming; este método no debería invocarse.");

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CapturedHistory = chatHistory;

            foreach (var fragmento in _fragmentosAEmitir)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield(); // fuerza asincronía real, no una lista ya materializada
                yield return fragmento;
            }
        }
    }
}
