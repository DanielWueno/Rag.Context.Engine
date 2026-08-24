using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Services.Generation;

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
    private readonly Kernel _kernel;
    private readonly ILogger<ChatAnswerStreamer> _logger;

    public ChatAnswerStreamer(Kernel kernel, ILogger<ChatAnswerStreamer> logger)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        await foreach (var streamChunk in chatService
            .GetStreamingChatMessageContentsAsync(
                chatHistory,
                executionSettings,
                _kernel,
                cancellationToken))
        {
            if (!string.IsNullOrEmpty(streamChunk.Content))
            {
                yield return streamChunk.Content;
            }
        }

        _logger.LogInformation("[RAG] Streaming complete for query: {Query}", query);
    }
}
