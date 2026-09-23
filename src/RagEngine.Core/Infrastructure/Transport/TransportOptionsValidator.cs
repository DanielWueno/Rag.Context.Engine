using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace RagEngine.Core.Infrastructure.Transport;

/// <summary>
/// Ítem 12.8: arranque negativo del perfil publicado. Con <c>Transport:Published=true</c>
/// exige credencial contra Qdrant y TLS resuelto (en el proceso o corriente arriba) ANTES
/// de aceptar tráfico — un perfil publicado sin ninguna de las dos cosas queda expuesto
/// sin autenticar sobre un canal sin cifrar. El perfil local (default, <c>Published=false</c>)
/// no cambia: no exige nada de esto, igual que antes de este ítem.
/// </summary>
public sealed class TransportOptionsValidator(IConfiguration configuration) : IValidateOptions<TransportOptions>
{
    public ValidateOptionsResult Validate(string? name, TransportOptions options)
    {
        if (!options.Published)
            return ValidateOptionsResult.Success;

        var missing = new List<string>();

        var qdrantApiKey = configuration["Qdrant:ApiKey"];
        if (string.IsNullOrWhiteSpace(qdrantApiKey))
        {
            missing.Add(
                "Qdrant:ApiKey vacío — un perfil publicado sin credencial contra Qdrant queda " +
                "abierto a cualquiera que lo alcance.");
        }

        if (!options.TlsTerminatedUpstream && !HasInProcessTlsCertificate(configuration))
        {
            missing.Add(
                "Ni Kestrel:Certificates:Default:Path (TLS en el proceso) ni " +
                "Transport:TlsTerminatedUpstream=true (TLS resuelto por un proxy/Ingress " +
                "externo) están configurados — un perfil publicado sin TLS resuelto por " +
                "ninguna de las dos vías viaja sin cifrar.");
        }

        return missing.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                "Transport:Published=true exige lo siguiente antes de arrancar: " +
                string.Join(" ", missing));
    }

    private static bool HasInProcessTlsCertificate(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Kestrel:Certificates:Default:Path"]);
}
