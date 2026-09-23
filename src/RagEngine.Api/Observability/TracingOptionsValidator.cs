using Microsoft.Extensions.Options;

namespace RagEngine.Api.Observability;

/// <summary>
/// Ítem 13.4: arranque negativo si <c>Tracing:Enabled=true</c> sin un
/// <see cref="TracingOptions.OtlpEndpoint"/> válido — mismo criterio que
/// <see cref="MetricsOptionsValidator"/>: mejor fallar al arrancar que exportar en
/// silencio a ningún lado.
/// </summary>
public sealed class TracingOptionsValidator : IValidateOptions<TracingOptions>
{
    public ValidateOptionsResult Validate(string? name, TracingOptions options)
    {
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        if (string.IsNullOrWhiteSpace(options.OtlpEndpoint) ||
            !Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail(
                "Tracing:Enabled=true exige Tracing:OtlpEndpoint con una URI absoluta " +
                "válida (p. ej. http://localhost:4317).");
        }

        return ValidateOptionsResult.Success;
    }
}
