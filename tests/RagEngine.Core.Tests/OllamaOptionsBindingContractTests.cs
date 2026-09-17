using Microsoft.Extensions.Configuration;
using RagEngine.Core.Domain;
using RagEngine.Core.Utilities;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 9.8: <see cref="OllamaOptions"/> se movió de
/// <c>RagEngine.Core.Extensions.GenerationServiceExtensions</c> a
/// <c>RagEngine.Core.Domain</c> porque adaptadores de aplicación
/// (<c>Infrastructure/Summary/OllamaBusinessSummaryGenerator.cs</c>,
/// <c>Pipeline/DefaultIngestionPipeline.cs</c>) la necesitaban y no pueden depender
/// de Extensions/ (la composición del host). Este test fija el CONTRATO OBSERVABLE
/// (nombre de sección, defaults, binding vía IConfiguration/variables de entorno) que
/// NO debía cambiar con el movimiento — sólo la carpeta/namespace del tipo cambió.
/// Fixture de valores previos al movimiento: capturados directamente de
/// src/RagEngine.Api/appsettings.json y src/RagEngine.Cli/appsettings.json (idénticos
/// entre ambos hosts, iguales a los defaults del tipo antes de moverlo).
/// </summary>
public sealed class OllamaOptionsBindingContractTests
{
    private const string ExpectedSectionName = "Ollama";
    private const string ExpectedDefaultEndpoint = "http://localhost:11434/v1";
    private const string ExpectedDefaultModelId = "qwen2.5-coder";
    private const int ExpectedDefaultTimeoutSeconds = 120;

    [Fact]
    public void La_seccion_y_los_defaults_no_cambiaron_al_mover_el_tipo()
    {
        Assert.Equal(ExpectedSectionName, OllamaOptions.SectionName);

        var defaults = new OllamaOptions();
        Assert.Equal(ExpectedDefaultEndpoint, defaults.Endpoint);
        Assert.Equal(ExpectedDefaultModelId, defaults.ModelId);
        Assert.Equal(ExpectedDefaultTimeoutSeconds, defaults.TimeoutSeconds);
    }

    [Fact]
    public void Los_appsettings_de_Api_y_Cli_siguen_declarando_los_mismos_valores_entre_si_y_con_los_defaults()
    {
        var repoRoot = RagEnginePaths.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.True(repoRoot is not null, "No se encontró RagEngine.slnx subiendo desde el binario de test.");

        var apiOptions = BindFromAppsettings(Path.Combine(repoRoot!, "src", "RagEngine.Api", "appsettings.json"));
        var cliOptions = BindFromAppsettings(Path.Combine(repoRoot!, "src", "RagEngine.Cli", "appsettings.json"));

        Assert.Equal(apiOptions.Endpoint, cliOptions.Endpoint);
        Assert.Equal(apiOptions.ModelId, cliOptions.ModelId);
        Assert.Equal(apiOptions.TimeoutSeconds, cliOptions.TimeoutSeconds);

        Assert.Equal(ExpectedDefaultEndpoint, apiOptions.Endpoint);
        Assert.Equal(ExpectedDefaultModelId, apiOptions.ModelId);
        Assert.Equal(ExpectedDefaultTimeoutSeconds, apiOptions.TimeoutSeconds);
    }

    /// <summary>
    /// El binding por variable de entorno (<c>Ollama__ModelId</c>) es el mecanismo real
    /// que usan ambos hosts en despliegue (ver infra/docker-compose.yml). Se prueba
    /// aquí, no sólo el binding JSON, porque un traslado de tipo podría romper
    /// silenciosamente el nombre de sección que ambos mecanismos comparten.
    /// </summary>
    [Fact]
    public void El_binding_por_variable_de_entorno_sigue_sobrescribiendo_ModelId()
    {
        var overrideVarName = $"{OllamaOptions.SectionName}__ModelId";
        Environment.SetEnvironmentVariable(overrideVarName, "mutante-modelo-de-prueba");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{OllamaOptions.SectionName}:Endpoint"] = ExpectedDefaultEndpoint,
                    [$"{OllamaOptions.SectionName}:ModelId"] = ExpectedDefaultModelId,
                    [$"{OllamaOptions.SectionName}:TimeoutSeconds"] = ExpectedDefaultTimeoutSeconds.ToString()
                })
                .AddEnvironmentVariables()
                .Build();

            var options = new OllamaOptions();
            config.GetSection(OllamaOptions.SectionName).Bind(options);

            Assert.Equal("mutante-modelo-de-prueba", options.ModelId);
            Assert.Equal(ExpectedDefaultEndpoint, options.Endpoint);
            Assert.Equal(ExpectedDefaultTimeoutSeconds, options.TimeoutSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable(overrideVarName, null);
        }
    }

    private static OllamaOptions BindFromAppsettings(string appsettingsPath)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(appsettingsPath, optional: false, reloadOnChange: false)
            .Build();

        var options = new OllamaOptions();
        config.GetSection(OllamaOptions.SectionName).Bind(options);
        return options;
    }
}
