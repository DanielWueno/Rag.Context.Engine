using Microsoft.Extensions.Options;

namespace RagEngine.Api.Observability;

/// <summary>
/// Ítem 13.3: arranque negativo si <c>Metrics:Enabled=true</c> declara un
/// <see cref="MetricsOptions.Exporter"/> desconocido, o si elige "Otlp" sin un
/// <see cref="MetricsOptions.OtlpEndpoint"/> válido — mejor fallar al arrancar que
/// exportar en silencio a ningún lado.
/// </summary>
public sealed class MetricsOptionsValidator : IValidateOptions<MetricsOptions>
{
    private static readonly string[] KnownExporters = ["Prometheus", "Otlp"];

    public ValidateOptionsResult Validate(string? name, MetricsOptions options)
    {
        if (!KnownExporters.Contains(options.Exporter, StringComparer.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                $"Metrics:Exporter=\"{options.Exporter}\" no reconocido. Valores válidos: " +
                string.Join(", ", KnownExporters) + ".");
        }

        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        if (string.Equals(options.Exporter, "Otlp", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.OtlpEndpoint) ||
                !Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out _))
            {
                return ValidateOptionsResult.Fail(
                    "Metrics:Enabled=true con Metrics:Exporter=\"Otlp\" exige Metrics:OtlpEndpoint " +
                    "con una URI absoluta válida (p. ej. http://localhost:4317).");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
