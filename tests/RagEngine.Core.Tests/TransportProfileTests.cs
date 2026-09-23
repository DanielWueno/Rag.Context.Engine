using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Extensions;
using RagEngine.Core.Infrastructure.Transport;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 12.8-secretos-y-tls: criterio literal del perfil publicado. Sin
/// <c>Transport:Published</c> (el caso de siempre) el arranque no exige nada nuevo —
/// éste es el perfil local. Con <c>Transport:Published=true</c>, el arranque debe
/// RECHAZARSE (<see cref="OptionsValidationException"/> vía <see cref="IStartupValidator"/>,
/// igual que el resto de validaciones de arranque de este repo — ver
/// <c>CollectionAuthorizationServiceTests.Modo_numerico_desconocido_falla_en_validacion_de_arranque</c>)
/// mientras falte credencial de Qdrant o TLS resuelto, y aceptarse en cuanto ambas
/// condiciones quedan configuradas — sin recompilar, solo con configuración.
/// </summary>
public sealed class TransportProfileTests
{
    [Fact]
    public void Perfil_local_por_defecto_arranca_sin_credencial_ni_tls()
    {
        using var provider = Services(published: false, apiKey: null, tlsUpstream: false, kestrelCertPath: null);

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Publicado_sin_credencial_ni_tls_falla_en_el_arranque()
    {
        using var provider = Services(published: true, apiKey: null, tlsUpstream: false, kestrelCertPath: null);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("Qdrant:ApiKey", ex.Message);
        Assert.Contains("TlsTerminatedUpstream", ex.Message);
    }

    [Fact]
    public void Publicado_con_credencial_pero_sin_tls_sigue_fallando()
    {
        using var provider = Services(published: true, apiKey: "clave-sintetica", tlsUpstream: false, kestrelCertPath: null);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.DoesNotContain("Qdrant:ApiKey", ex.Message);
        Assert.Contains("TlsTerminatedUpstream", ex.Message);
    }

    [Fact]
    public void Publicado_con_tls_pero_sin_credencial_sigue_fallando()
    {
        using var provider = Services(published: true, apiKey: null, tlsUpstream: true, kestrelCertPath: null);

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("Qdrant:ApiKey", ex.Message);
    }

    [Fact]
    public void Publicado_con_credencial_y_ack_de_tls_corriente_arriba_arranca()
    {
        using var provider = Services(published: true, apiKey: "clave-sintetica", tlsUpstream: true, kestrelCertPath: null);

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Publicado_con_credencial_y_certificado_de_kestrel_en_el_proceso_arranca()
    {
        using var provider = Services(published: true, apiKey: "clave-sintetica", tlsUpstream: false, kestrelCertPath: "/tmp/cert-de-prueba.pfx");

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private static ServiceProvider Services(bool published, string? apiKey, bool tlsUpstream, string? kestrelCertPath)
    {
        var values = new Dictionary<string, string?>
        {
            ["Transport:Published"] = published.ToString(),
            ["Transport:TlsTerminatedUpstream"] = tlsUpstream.ToString()
        };
        if (apiKey is not null) values["Qdrant:ApiKey"] = apiKey;
        if (kestrelCertPath is not null) values["Kestrel:Certificates:Default:Path"] = kestrelCertPath;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddRagEngineCore(configuration);
        return services.BuildServiceProvider();
    }
}
