using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using RagEngine.Core.Domain;

namespace RagEngine.Poc.FreeSearch;

/// <summary>
/// Fase 2 del PoC: genera un resumen de negocio por chunk con el modelo LOCAL
/// (qwen2.5-coder vía Ollama). Reproduce el prompt universal, agnóstico del stack,
/// del documento de diseño, incluido el centinela SIN_CONTENIDO_DE_NEGOCIO.
///
/// No pasa por RagGenerationService (que acopla retrieval+generación): habla
/// directamente con IChatCompletionService, igual que hace el motor pero sin la fase
/// de recuperación. temperature=0.15 porque el resumen es un artefacto del índice,
/// no generación creativa.
/// </summary>
public sealed class SummaryGenerator
{
    public const string Sentinel = "SIN_CONTENIDO_DE_NEGOCIO";

    // Versión del prompt: cámbiala si editas el texto de abajo. En producción esto
    // formaría parte de la prompt_version del caché SQLite (Reto A).
    public const string PromptVersion = "v1";

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

    public SummaryGenerator(PocSettings settings)
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(settings.Ollama.Endpoint),
            Timeout = TimeSpan.FromSeconds(settings.Ollama.TimeoutSeconds),
        };

        var builder = Kernel.CreateBuilder();
        builder.AddOpenAIChatCompletion(
            modelId: settings.Ollama.ModelId,
            apiKey: "ollama", // placeholder requerido pero ignorado por Ollama
            httpClient: httpClient);

        _kernel = builder.Build();
        _chat = _kernel.GetRequiredService<IChatCompletionService>();

        _settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.15,
            TopP = 0.95,
            MaxTokens = 160, // el resumen debe caber holgadamente bajo los 256 del embedder
        };
    }

    public sealed record SummaryResult(string Text, bool SinNegocio);

    /// <summary>
    /// Genera el resumen de un chunk. Aísla fallos: si Ollama revienta, devuelve null
    /// (ese chunk queda sin dense-resumen esta corrida) en vez de abortar la muestra,
    /// mismo principio que "un archivo malformado no detiene la ingesta".
    /// </summary>
    public async Task<SummaryResult?> GenerateAsync(CodeChunk chunk, CancellationToken ct = default)
    {
        var userPrompt =
            $"[Contexto: Lenguaje: {chunk.Metadata.Language}, Ruta: {chunk.Metadata.RelativeFilePath}]\n\n{chunk.Content}";

        var history = new ChatHistory();
        history.AddSystemMessage(SystemPrompt);
        history.AddUserMessage(userPrompt);

        try
        {
            var response = await _chat.GetChatMessageContentAsync(history, _settings, _kernel, ct);
            var text = (response.Content ?? "").Trim();

            if (text.Length == 0)
                return null;

            var sinNegocio = text.StartsWith(Sentinel, StringComparison.Ordinal);
            return new SummaryResult(text, sinNegocio);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [ollama-fail] {chunk.Metadata.RelativeFilePath}: {ex.Message}");
            return null;
        }
    }
}
