using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Polly;
using Polly.Registry;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Extensions;

namespace RagEngine.Core.Services.Summary;

/// <summary>
/// Fallo de conexión con Ollama (host inalcanzable, timeout de transporte). Se
/// distingue de un fallo de contenido/aplicación porque el pool de la Fase 2 de
/// ingesta usa fallos de conexión CONSECUTIVOS para activar un circuit breaker
/// propio y abortar ordenadamente en vez de degradarse chunk a chunk.
/// </summary>
public sealed class BusinessSummaryConnectionException(string message, Exception inner)
    : Exception(message, inner);

/// <summary>
/// Genera resúmenes de negocio vía Ollama, usando un Kernel/IChatCompletionService
/// propio (no el de <c>RagGenerationService</c>): el resumen es un artefacto del
/// índice (determinista, corto) con parámetros de muestreo muy distintos de la
/// generación conversacional. Reproduce el prompt universal y el centinela
/// SIN_CONTENIDO_DE_NEGOCIO validados en el PoC (poc/RagEngine.Poc.FreeSearch/SummaryGenerator.cs).
/// </summary>
public sealed class OllamaBusinessSummaryGenerator : IBusinessSummaryGenerator
{
    public const string Sentinel = "SIN_CONTENIDO_DE_NEGOCIO";

    /// <summary>
    /// Versión del prompt: cambiarla invalida automáticamente la caché de resúmenes
    /// (la clave de caché combina content_hash + prompt_version).
    /// </summary>
    public const string PromptVersion = "v1";

    public const string ResiliencePipelineName = "ollama-summary";

    private const string SystemPrompt = """
        Eres un analista de negocio que traduce fragmentos de código — en cualquier lenguaje de
        programación — a descripciones de comportamiento para usuarios sin conocimiento técnico.

        No expliques sintaxis, nombres de frameworks ni construcciones del lenguaje. A partir de tu
        propio conocimiento de cómo funciona el lenguaje indicado en el contexto, identifica QUÉ hace
        este fragmento, QUÉ dato o acción involucra, y QUÉ regla o condición aplica — en el lenguaje
        que usaría alguien que jamás vio código.

        Responde en español, en 1 a 3 oraciones (máximo 60 palabras). Empieza nombrando la entidad,
        pantalla o archivo, seguido de dos puntos — sin frases como "este componente" o "esta clase
        representa". No inventes campos, reglas o comportamientos que no estén explícitos en el
        fragmento: si no está en el código, no existe.

        Si el fragmento es puramente técnico sin significado de negocio visible (imports,
        configuración, boilerplate, getters/setters triviales), responde exactamente:
        SIN_CONTENIDO_DE_NEGOCIO
        """;

    private readonly IChatCompletionService _chat;
    private readonly Kernel _kernel;
    private readonly OpenAIPromptExecutionSettings _settings;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly ILogger<OllamaBusinessSummaryGenerator> _logger;

    public OllamaBusinessSummaryGenerator(
        IOptions<OllamaOptions> options,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<OllamaBusinessSummaryGenerator> logger)
    {
        var opts = options.Value;
        _logger = logger;
        _resiliencePipeline = pipelineProvider.GetPipeline(ResiliencePipelineName);

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(opts.Endpoint),
            Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds),
        };

        var builder = Kernel.CreateBuilder();
        builder.AddOpenAIChatCompletion(
            modelId: opts.ModelId,
            apiKey: "ollama", // placeholder requerido pero ignorado por Ollama
            httpClient: httpClient);

        _kernel = builder.Build();
        _chat = _kernel.GetRequiredService<IChatCompletionService>();

        // El resumen es un artefacto determinista del índice, no generación creativa —
        // parámetros muy distintos de los usados por RagGenerationService.
        _settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.15,
            TopP = 0.95,
            MaxTokens = 160, // debe caber holgadamente bajo los 256 tokens del embedder
        };
    }

    public async Task<BusinessSummaryResult?> GenerateAsync(CodeChunk chunk, CancellationToken cancellationToken = default)
    {
        var userPrompt =
            $"[Contexto: Lenguaje: {chunk.Metadata.Language}, Ruta: {chunk.Metadata.RelativeFilePath}]\n\n{chunk.Content}";

        var history = new ChatHistory();
        history.AddSystemMessage(SystemPrompt);
        history.AddUserMessage(userPrompt);

        try
        {
            var response = await _resiliencePipeline.ExecuteAsync(
                async ct => await _chat.GetChatMessageContentAsync(history, _settings, _kernel, ct),
                cancellationToken);

            var text = (response.Content ?? "").Trim();
            if (text.Length == 0)
                return null;

            var sinNegocio = text.StartsWith(Sentinel, StringComparison.Ordinal);
            return new BusinessSummaryResult(text, sinNegocio);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // cancelación real del llamador, no un fallo de Ollama
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            _logger.LogWarning(ex, "Fallo de conexión con Ollama al resumir {File}", chunk.Metadata.RelativeFilePath);
            throw new BusinessSummaryConnectionException(
                $"No se pudo conectar con Ollama para resumir {chunk.Metadata.RelativeFilePath}.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al generar resumen para {File}", chunk.Metadata.RelativeFilePath);
            return null; // aislado: este chunk queda sin resumen esta corrida, recuperable al reanudar
        }
    }

    private static bool IsConnectionFailure(Exception ex) =>
        ex is HttpRequestException or SocketException or TimeoutException or TaskCanceledException;

    /// <summary>
    /// Clave de invalidación de la caché de resúmenes: cambia si se edita el system
    /// prompt o se cambia de modelo, sin necesitar una migración manual de la tabla.
    /// </summary>
    public static string ComputePromptVersion(string modelId)
    {
        var input = $"{PromptVersion}|{modelId}|{SystemPrompt}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}
