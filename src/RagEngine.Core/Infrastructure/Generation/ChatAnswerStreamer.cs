using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Polly;
using Polly.Registry;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.Generation;

/// <summary>
/// Habla con el LLM: arma el <see cref="ChatHistory"/> (prompt de sistema + turnos
/// previos + pregunta nueva), fija los parámetros de muestreo y emite los fragmentos
/// según llegan.
///
/// Separado de <see cref="RagGenerationService"/> por el ítem 2.2 del plan. Es la única
/// pieza que toca el <see cref="Kernel"/>, y no sabe nada de chunks, scores ni resúmenes:
/// los dos caminos que llegan a generación —el anclado y el conversacional— se
/// diferencian sólo en qué prompt de sistema le pasan.
/// </summary>
internal sealed class ChatAnswerStreamer
{
    /// <summary>Nombre del pipeline registrado con <c>AddResiliencePipeline</c> (ítem 8.e).</summary>
    public const string ResiliencePipelineName = "ollama-chat";

    private readonly Kernel _kernel;
    private readonly ILogger<ChatAnswerStreamer> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public ChatAnswerStreamer(
        Kernel kernel,
        ILogger<ChatAnswerStreamer> logger,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(pipelineProvider);
        _resiliencePipeline = pipelineProvider.GetPipeline(ResiliencePipelineName);
    }

    /// <summary>
    /// Streams the LLM's response for the given system prompt, prior turns and query.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        string query,
        IReadOnlyList<ChatTurn>? history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Build a ChatHistory so the system prompt is correctly separated
        // from the conversation turns — Semantic Kernel respects this structure.
        // Prior turns (if any) come from the caller on every request — this service
        // is stateless and keeps no session, so retrieval only ever searched
        // the latest `query`, never the older turns.
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(systemPrompt);

        if (history is not null)
        {
            foreach (var turn in history)
            {
                if (turn.Role == ChatRole.User)
                    chatHistory.AddUserMessage(turn.Content);
                else
                    chatHistory.AddAssistantMessage(turn.Content);
            }
        }

        chatHistory.AddUserMessage(query);

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();

        var executionSettings = new PromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["temperature"] = 0.1,
                ["top_p"]       = 0.95,
                ["max_tokens"]  = 2048
            }
        };

        _logger.LogInformation("[RAG] Streaming LLM response for query: {Query}", query);

        // Ítem 8.e: antes de este cambio, el HttpClient de Ollama sólo tenía Timeout —
        // ningún retry, ningún circuit breaker (a diferencia de Qdrant y del resumen
        // de negocio, que ya usan AddResiliencePipeline). La resiliencia NO se puede
        // envolver alrededor de todo el `await foreach` como en esos dos casos: acá el
        // fallo puede llegar DESPUÉS de que ya se emitieron fragmentos al llamador
        // (que a su vez ya los mandó por SSE al cliente), y reintentar desde cero
        // duplicaría texto ya visto. Por eso el pipeline sólo cubre el intento de abrir
        // el stream (crear un enumerador NUEVO y pedirle su primer elemento — un
        // enumerador que ya lanzó una excepción no se puede "continuar", hay que
        // reconstruirlo): una vez que ese primer elemento llegó, cualquier fallo
        // posterior se deja propagar tal cual, sin reintentar.
        IAsyncEnumerator<StreamingChatMessageContent>? enumerator = null;
        try
        {
            var hasNext = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                if (enumerator is not null)
                    await enumerator.DisposeAsync();

                enumerator = chatService
                    .GetStreamingChatMessageContentsAsync(chatHistory, executionSettings, _kernel, ct)
                    .GetAsyncEnumerator(ct);
                return await enumerator.MoveNextAsync();
            }, cancellationToken);

            while (hasNext)
            {
                var content = enumerator!.Current.Content;
                if (!string.IsNullOrEmpty(content))
                {
                    yield return content;
                }

                hasNext = await enumerator.MoveNextAsync();
            }
        }
        finally
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync();
        }

        _logger.LogInformation("[RAG] Streaming complete for query: {Query}", query);
    }
}
