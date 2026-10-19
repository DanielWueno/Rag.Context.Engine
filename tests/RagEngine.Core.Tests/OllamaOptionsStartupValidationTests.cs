using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Extensions;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 10.6.2 (porte del 17.1 local), criterio 5: el arranque del host debe rechazarse
/// (<see cref="OptionsValidationException"/> vía <see cref="IStartupValidator"/>, mismo
/// patrón que <c>TransportProfileTests</c>) si el perfil de Ollama queda a medias —
/// Endpoint https sin Ollama:ApiKey, o CaCertificatePath apuntando a un archivo que no
/// existe — y debe arrancar sin exigir nada nuevo en el perfil local (control negativo).
///
/// Usa solo <c>services.AddRagEngineCore(configuration)</c>, sin
/// <c>AddRagEngineGeneration</c>, igual que <c>TransportProfileTests</c>: el registro de
/// <c>IValidateOptions&lt;OllamaOptions&gt;</c> + <c>AddOptions&lt;OllamaOptions&gt;().ValidateOnStart()</c>
/// vive dentro de <c>AddRagEngineCore</c> (no en <c>RagEngine.Api/Program.cs</c>) precisamente
/// para que este chequeo se pueda probar sin levantar un host HTTP real.
/// </summary>
public sealed class OllamaOptionsStartupValidationTests
{
    [Fact]
    public void PerfilServidor_SinApiKey_FallaEnElArranque()
    {
        using var provider = Services(endpoint: "https://10.97.0.19:8443/v1", apiKey: null, caCertificatePath: null);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("ApiKey", ex.Message);
    }

    [Fact]
    public void PerfilServidor_ConCaCertificatePathInexistente_FallaEnElArranque()
    {
        var rutaInexistente = Path.Combine(Path.GetTempPath(), $"no-existe-{Guid.NewGuid():N}.crt");

        using var provider = Services(
            endpoint: "http://localhost:11434/v1",
            apiKey: "clave-sintetica-de-test",
            caCertificatePath: rutaInexistente);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("CaCertificatePath", ex.Message);
    }

    [Fact]
    public void PerfilLocal_SinApiKey_ArrancaSinExigirNadaNuevo()
    {
        using var provider = Services(endpoint: "http://localhost:11434/v1", apiKey: null, caCertificatePath: null);

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private static ServiceProvider Services(string endpoint, string? apiKey, string? caCertificatePath)
    {
        var values = new Dictionary<string, string?>
        {
            ["Ollama:Endpoint"] = endpoint,
        };
        if (apiKey is not null) values["Ollama:ApiKey"] = apiKey;
        if (caCertificatePath is not null) values["Ollama:CaCertificatePath"] = caCertificatePath;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddRagEngineCore(configuration);
        return services.BuildServiceProvider();
    }
}
