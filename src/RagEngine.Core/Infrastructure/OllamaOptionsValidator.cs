using Microsoft.Extensions.Options;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure;

/// <summary>
/// Ítem 10.6.2 (porte del perfil servidor de Ollama): arranque negativo si el perfil
/// (local/servidor) queda a medias — Endpoint https sin Ollama:ApiKey, o
/// CaCertificatePath apuntando a un archivo que no existe. Reusa exactamente la misma
/// validación que ya ejercitan los dos consumidores reales del HttpClient compartido
/// (<see cref="OllamaHttpClientFactory.Validate"/>) para que el arranque del host falle
/// con el mismo mensaje accionable que vería `rag doctor`, en vez de que el primer
/// request descubra un 401/403 confuso o una excepción de validación de certificado sin
/// contexto con el proceso ya sirviendo tráfico. Vive en Infrastructure/ (no en
/// RagEngine.Api.Observability, donde viven MetricsOptionsValidator/TracingOptionsValidator)
/// porque <c>ServiceCollectionExtensions.AddRagEngineCore</c> (en Core) lo registra junto con
/// <c>AddOptions&lt;OllamaOptions&gt;().ValidateOnStart()</c> — un host de Core no puede
/// depender de un tipo que viva en Api sin invertir la dirección de dependencias.
/// </summary>
public sealed class OllamaOptionsValidator : IValidateOptions<OllamaOptions>
{
    public ValidateOptionsResult Validate(string? name, OllamaOptions options)
    {
        try
        {
            OllamaHttpClientFactory.Validate(options);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
