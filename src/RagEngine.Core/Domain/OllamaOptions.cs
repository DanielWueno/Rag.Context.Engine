namespace RagEngine.Core.Domain;

/// <summary>
/// Configuration options for the local Ollama LLM endpoint.
/// Bound from appsettings.json → "Ollama" section.
///
/// Ítem 9.8: vivía en <c>RagEngine.Core.Extensions.GenerationServiceExtensions</c>
/// (composición del host) aunque lo consumen adaptadores de aplicación —
/// <see cref="RagEngine.Core.Infrastructure.Summary.OllamaBusinessSummaryGenerator"/>,
/// <see cref="RagEngine.Core.Pipeline.DefaultIngestionPipeline"/> — y ambos hosts
/// directamente. Un contrato de opciones consumido fuera de Extensions/ no puede
/// vivir en Extensions/: la dirección correcta es que Extensions/ (la composición)
/// dependa de este tipo, nunca al revés. Mismo lugar que ya usa
/// <see cref="IngestionOptions"/> para el mismo propósito (config bindeada,
/// sin lógica de DI). El binding (<c>services.Configure&lt;OllamaOptions&gt;</c>) y el
/// valor de cada campo NO cambian — sólo la carpeta/namespace del tipo.
/// </summary>
public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    /// <summary>Base URL of the Ollama OpenAI-compatible API endpoint.</summary>
    public string Endpoint { get; init; } = "http://localhost:11434/v1";

    /// <summary>Model tag to use (must be pulled in Ollama beforehand).</summary>
    public string ModelId { get; init; } = "qwen2.5-coder";

    /// <summary>Request timeout in seconds for long LLM generations.</summary>
    public int TimeoutSeconds { get; init; } = 120;
}
