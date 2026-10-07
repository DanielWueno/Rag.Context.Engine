using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using RagEngine.Core.Domain;
using RagEngine.Core.Extensions;
using RagEngine.Core.Infrastructure;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 10.6.2 (porte del 17.1 local: perfiles local y servidor de Ollama). Cubre:
///   1. El perfil local (sin variables nuevas) liga a los mismos valores de siempre —
///      regresión byte-equivalente, nada de esto debe cambiar el comportamiento por
///      defecto.
///   2. El perfil servidor falla rápido (al resolverse el Kernel, el único punto que
///      construye el HttpClient — ver OllamaHttpClientFactory) si el Endpoint es https
///      sin ApiKey, o si CaCertificatePath no existe en disco.
///
/// Los dos últimos hechos (ResolverKernel_*) esperan <see cref="OptionsValidationException"/>
/// en vez del <see cref="InvalidOperationException"/> que lanzaba la rama local: main (ítem
/// 10.6.2) registra <c>OllamaOptionsValidator</c> como <c>IValidateOptions&lt;OllamaOptions&gt;</c>
/// dentro de <c>AddRagEngineCore</c> (mismo patrón que TransportOptionsValidator), así que
/// cualquier resolución de <c>IOptions&lt;OllamaOptions&gt;.Value</c> por DI — incluida la que
/// hace el Kernel singleton de abajo ANTES de llegar a OllamaHttpClientFactory.Create —
/// ya dispara esa validación y envuelve el mensaje en OptionsValidationException. El mensaje
/// (con "ApiKey"/"CaCertificatePath") es el mismo; solo cambia el tipo de excepción que ve
/// un consumidor vía DI, consistente con el resto de validadores de arranque del repo.
/// </summary>
public class OllamaProfileTests
{
    /// <summary>
    /// Configuración mínima sin ninguna de las dos claves nuevas — equivalente a un
    /// appsettings.json de perfil local al que nadie le tocó la sección Ollama.
    /// </summary>
    private static IConfiguration ConfiguracionLocalVacia() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    [Fact]
    public void PerfilLocal_SinVariablesNuevas_LigaLosMismosValoresDeSiempre()
    {
        var opts = ConfiguracionLocalVacia()
            .GetSection(OllamaOptions.SectionName)
            .Get<OllamaOptions>() ?? new OllamaOptions();

        // Byte-equivalente a los defaults declarados en OllamaOptions antes del ítem
        // 10.6.2 (y a los valores de appsettings.json, que no se tocaron).
        Assert.Equal("http://localhost:11434/v1", opts.Endpoint);
        Assert.Equal("qwen2.5-coder", opts.ModelId);
        Assert.Equal(120, opts.TimeoutSeconds);
        Assert.Null(opts.ApiKey);
        Assert.Null(opts.CaCertificatePath);
    }

    [Fact]
    public void PerfilLocal_ResuelveClaveDummyParaElSdk()
    {
        var opts = new OllamaOptions();
        Assert.Equal(OllamaHttpClientFactory.LocalPlaceholderApiKey, OllamaHttpClientFactory.ResolveSdkApiKey(opts));
    }

    [Fact]
    public void PerfilLocal_NoFallaAlValidar()
    {
        var opts = new OllamaOptions(); // defaults: http, sin ApiKey, sin CaCertificatePath
        var ex = Record.Exception(() => OllamaHttpClientFactory.Validate(opts));
        Assert.Null(ex);
    }

    [Fact]
    public void PerfilServidor_HttpsSinApiKey_FallaAlValidar()
    {
        var opts = new OllamaOptions
        {
            Endpoint = "https://10.97.0.19:8443/v1",
            ModelId = "qwen2.5-coder:14b",
            ApiKey = null,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => OllamaHttpClientFactory.Validate(opts));
        Assert.Contains("ApiKey", ex.Message);
    }

    [Fact]
    public void PerfilServidor_CaCertificatePathInexistente_FallaAlValidar()
    {
        var rutaInexistente = Path.Combine(Path.GetTempPath(), $"no-existe-{Guid.NewGuid():N}.crt");
        Assert.False(File.Exists(rutaInexistente));

        var opts = new OllamaOptions
        {
            Endpoint = "http://localhost:11434/v1", // http: no dispara la regla de ApiKey
            ApiKey = "clave-sintetica-de-test",
            CaCertificatePath = rutaInexistente,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => OllamaHttpClientFactory.Validate(opts));
        Assert.Contains("CaCertificatePath", ex.Message);
    }

    [Fact]
    public void PerfilServidor_ConApiKeyYCaValida_NoFallaAlValidar()
    {
        var caTemporal = Path.GetTempFileName();
        try
        {
            var opts = new OllamaOptions
            {
                Endpoint = "https://10.97.0.19:8443/v1",
                ModelId = "qwen2.5-coder:14b",
                ApiKey = "clave-sintetica-de-test",
                CaCertificatePath = caTemporal,
            };

            var ex = Record.Exception(() => OllamaHttpClientFactory.Validate(opts));
            Assert.Null(ex);
            Assert.Equal("clave-sintetica-de-test", OllamaHttpClientFactory.ResolveSdkApiKey(opts));
        }
        finally
        {
            File.Delete(caTemporal);
        }
    }

    /// <summary>
    /// El host no sube "a medias": resolver el Kernel singleton (el único punto que
    /// construye el HttpClient de Ollama para la generación conversacional) con un
    /// perfil servidor sin ApiKey debe fallar, no servir un Kernel que luego reviente
    /// en el primer request con un 401 silencioso.
    /// </summary>
    [Fact]
    public void ResolverKernel_ConPerfilServidorSinApiKey_Falla()
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Qdrant:Host"] = "localhost",
            ["Qdrant:GrpcPort"] = "6334",
            ["Ollama:Endpoint"] = "https://10.97.0.19:8443/v1",
            ["Ollama:ModelId"] = "qwen2.5-coder:14b",
            // Ollama:ApiKey deliberadamente ausente.
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRagEngineCore(configuracion);
        services.AddRagEngineGeneration(configuracion);

        using var proveedor = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => proveedor.GetRequiredService<Kernel>());
        Assert.Contains("ApiKey", ex.Message);
    }

    /// <summary>
    /// Mismo chequeo, pero con CaCertificatePath apuntando a un archivo que no existe:
    /// el host tampoco debe terminar de construir el Kernel en ese estado a medias.
    /// </summary>
    [Fact]
    public void ResolverKernel_ConCaCertificatePathInexistente_Falla()
    {
        var rutaInexistente = Path.Combine(Path.GetTempPath(), $"no-existe-{Guid.NewGuid():N}.crt");

        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Qdrant:Host"] = "localhost",
            ["Qdrant:GrpcPort"] = "6334",
            ["Ollama:Endpoint"] = "http://localhost:11434/v1",
            ["Ollama:ModelId"] = "qwen2.5-coder",
            ["Ollama:ApiKey"] = "clave-sintetica-de-test",
            ["Ollama:CaCertificatePath"] = rutaInexistente,
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRagEngineCore(configuracion);
        services.AddRagEngineGeneration(configuracion);

        using var proveedor = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => proveedor.GetRequiredService<Kernel>());
        Assert.Contains("CaCertificatePath", ex.Message);
    }
}
