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

    /// <summary>
    /// Bearer token para endpoints remotos que lo exigen (p. ej. Servidor.IA). Null/vacío
    /// para el perfil local: Ollama nativo no valida ninguna credencial. Nunca va en un
    /// appsettings versionado — llega solo por variable de entorno (<c>Ollama__ApiKey</c>,
    /// alimentada en la máquina real desde <c>SERVIDOR_IA_CLAVE</c>). Ítem 10.6.2 (porte del
    /// perfil servidor de Ollama, 17.1 local).
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Ruta a un certificado de CA (PEM/CRT) a confiar EXCLUSIVAMENTE para validar el
    /// servidor Ollama remoto, cuando ese servidor usa una CA interna que el almacén del
    /// sistema no conoce. Null/vacío para el perfil local (confianza TLS del sistema, sin
    /// cambios). Llega solo por variable de entorno (<c>Ollama__CaCertificatePath</c>,
    /// alimentada desde <c>SERVIDOR_IA_CA</c>). Ver
    /// <see cref="RagEngine.Core.Infrastructure.OllamaHttpClientFactory"/>.
    /// </summary>
    public string? CaCertificatePath { get; init; }
}
