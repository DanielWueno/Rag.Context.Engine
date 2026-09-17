using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RagEngine.Core.Infrastructure.VectorStore;
using RagEngine.Core.Services.Generation;
using RagEngine.Core.Utilities;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 8.g: Api y Cli deben tomar <c>RetrievalFusion</c> y los umbrales del gate de
/// confianza (<c>RagGeneration:Low/HighConfidenceThreshold</c>) de la MISMA fuente
/// (<c>config/shared.appsettings.json</c>), no de copias literales por host. Estos
/// tests replican mecánicamente el orden de fuentes de configuración de cada
/// <c>Program.cs</c> (appsettings.json del host cargado primero, luego
/// <see cref="RagEnginePaths.InsertSharedConfigSource"/>) sin levantar Qdrant/Ollama:
/// lo que se prueba es el binding de <see cref="IConfiguration"/>, idéntico al que
/// hace <c>AddRagEngineCore</c>/<c>AddRagEngineGeneration</c> en ambos hosts.
/// </summary>
public sealed class SharedHostConfigTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string? _originalSharedConfigDir;

    public SharedHostConfigTests()
    {
        string? repoRoot = RagEnginePaths.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.True(repoRoot is not null, "No se encontró RagEngine.slnx subiendo desde el binario de test.");
        _repoRoot = repoRoot!;
        _originalSharedConfigDir = Environment.GetEnvironmentVariable(RagEnginePaths.SharedConfigDirVariable);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RagEnginePaths.SharedConfigDirVariable, _originalSharedConfigDir);
    }

    private string ApiAppsettingsPath => Path.Combine(_repoRoot, "src", "RagEngine.Api", "appsettings.json");
    private string CliAppsettingsPath => Path.Combine(_repoRoot, "src", "RagEngine.Cli", "appsettings.json");

    /// <summary>
    /// Reproduce el orden real: <c>WebApplication.CreateBuilder</c> ya cargó
    /// appsettings.json del host antes de que <c>Program.cs</c> llame a
    /// <see cref="RagEnginePaths.InsertSharedConfigSource"/>.
    /// </summary>
    private static IConfigurationRoot BuildAsHostWould(string hostAppsettingsPath)
    {
        var config = new ConfigurationBuilder();
        config.AddJsonFile(hostAppsettingsPath, optional: false, reloadOnChange: false);
        RagEnginePaths.InsertSharedConfigSource(config);
        return config.Build();
    }

    [Fact]
    public void Api_no_declara_RetrievalFusion_ni_umbrales_del_gate_en_su_propio_appsettings()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ApiAppsettingsPath));

        Assert.False(doc.RootElement.TryGetProperty("RetrievalFusion", out _),
            "Api/appsettings.json no debe redeclarar RetrievalFusion: su única fuente es config/shared.appsettings.json.");

        if (doc.RootElement.TryGetProperty("RagGeneration", out var ragGeneration))
        {
            Assert.False(ragGeneration.TryGetProperty("LowConfidenceThreshold", out _));
            Assert.False(ragGeneration.TryGetProperty("HighConfidenceThreshold", out _));
        }
    }

    [Fact]
    public void Cli_no_declara_RetrievalFusion_ni_umbrales_del_gate_en_su_propio_appsettings()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(CliAppsettingsPath));

        Assert.False(doc.RootElement.TryGetProperty("RetrievalFusion", out _),
            "Cli/appsettings.json no debe redeclarar RetrievalFusion: su única fuente es config/shared.appsettings.json.");

        if (doc.RootElement.TryGetProperty("RagGeneration", out var ragGeneration))
        {
            Assert.False(ragGeneration.TryGetProperty("LowConfidenceThreshold", out _));
            Assert.False(ragGeneration.TryGetProperty("HighConfidenceThreshold", out _));
        }
    }

    [Fact]
    public void Cli_Ingestion_no_declara_las_tres_claves_muertas()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(CliAppsettingsPath));

        if (doc.RootElement.TryGetProperty("Ingestion", out var ingestion))
        {
            Assert.False(ingestion.TryGetProperty("DefaultCollection", out _),
                "IngestionOptions no enlaza DefaultCollection — es Ingest.Settings.Collection, un CommandOption de rag ingest.");
            Assert.False(ingestion.TryGetProperty("RepositoryName", out _),
                "IngestionOptions no enlaza RepositoryName — es Ingest.Settings.RepositoryName, un CommandOption de rag ingest.");
            Assert.False(ingestion.TryGetProperty("BatchSize", out _),
                "IngestionOptions no enlaza BatchSize — es Ingest.Settings.BatchSize, un CommandOption de rag ingest.");
        }
    }

    [Fact]
    public void Ambos_hosts_toman_RetrievalFusion_y_gate_del_mismo_shared_appsettings()
    {
        var api = BuildAsHostWould(ApiAppsettingsPath);
        var cli = BuildAsHostWould(CliAppsettingsPath);

        var apiFusion = api.GetSection(RetrievalFusionOptions.SectionName).Get<RetrievalFusionOptions>();
        var cliFusion = cli.GetSection(RetrievalFusionOptions.SectionName).Get<RetrievalFusionOptions>();
        Assert.NotNull(apiFusion);
        Assert.NotNull(cliFusion);
        Assert.Equal(cliFusion!.WeightCodigo, apiFusion!.WeightCodigo);
        Assert.Equal(cliFusion.WeightSparse, apiFusion.WeightSparse);
        Assert.Equal(cliFusion.WeightResumen, apiFusion.WeightResumen);
        Assert.Equal(cliFusion.RrfK, apiFusion.RrfK);

        var apiGate = api.GetSection(RagGenerationOptions.SectionName).Get<RagGenerationOptions>();
        var cliGate = cli.GetSection(RagGenerationOptions.SectionName).Get<RagGenerationOptions>();
        Assert.NotNull(apiGate);
        Assert.NotNull(cliGate);
        Assert.Equal(cliGate!.LowConfidenceThreshold, apiGate!.LowConfidenceThreshold);
        Assert.Equal(cliGate.HighConfidenceThreshold, apiGate.HighConfidenceThreshold);
    }

    /// <summary>
    /// El criterio de verificación de 8.g: "cambiar un peso de RetrievalFusion o un
    /// umbral del gate en un solo sitio y verificar que ambos hosts lo toman". Apunta
    /// <see cref="RagEnginePaths.SharedConfigDirVariable"/> a un directorio temporal con
    /// un <c>shared.appsettings.json</c> con valores distintos a los calibrados y
    /// confirma que AMBOS hosts los reflejan.
    /// </summary>
    [Fact]
    public void Cambiar_RrfK_y_el_umbral_bajo_en_un_solo_sitio_lo_toman_ambos_hosts()
    {
        string tempDir = Directory.CreateTempSubdirectory("rag-engine-8g-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "shared.appsettings.json"), """
                {
                  "RetrievalFusion": { "WeightCodigo": 1.0, "WeightSparse": 1.3, "WeightResumen": 2.5, "RrfK": 999 },
                  "RagGeneration": { "LowConfidenceThreshold": 0.42, "HighConfidenceThreshold": 0.60 }
                }
                """);
            Environment.SetEnvironmentVariable(RagEnginePaths.SharedConfigDirVariable, tempDir);

            var api = BuildAsHostWould(ApiAppsettingsPath);
            var cli = BuildAsHostWould(CliAppsettingsPath);

            var apiFusion = api.GetSection(RetrievalFusionOptions.SectionName).Get<RetrievalFusionOptions>();
            var cliFusion = cli.GetSection(RetrievalFusionOptions.SectionName).Get<RetrievalFusionOptions>();
            Assert.Equal(999, apiFusion!.RrfK);
            Assert.Equal(999, cliFusion!.RrfK);

            var apiGate = api.GetSection(RagGenerationOptions.SectionName).Get<RagGenerationOptions>();
            var cliGate = cli.GetSection(RagGenerationOptions.SectionName).Get<RagGenerationOptions>();
            Assert.Equal(0.42f, apiGate!.LowConfidenceThreshold);
            Assert.Equal(0.42f, cliGate!.LowConfidenceThreshold);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// El appsettings.json del host conserva capacidad de override local (precedencia
    /// mayor que el shared): si un experimento puntual necesita un RrfK distinto solo
    /// en un host, sigue pudiendo declararlo sin tocar el archivo compartido.
    /// </summary>
    [Fact]
    public void Un_host_puede_seguir_sobreescribiendo_localmente_el_valor_compartido()
    {
        string tempDir = Directory.CreateTempSubdirectory("rag-engine-8g-override-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "shared.appsettings.json"), """
                { "RetrievalFusion": { "WeightCodigo": 1.0, "WeightSparse": 1.3, "WeightResumen": 2.5, "RrfK": 60 } }
                """);
            Environment.SetEnvironmentVariable(RagEnginePaths.SharedConfigDirVariable, tempDir);

            string hostAppsettingsWithOverride = Path.Combine(tempDir, "host.appsettings.json");
            File.WriteAllText(hostAppsettingsWithOverride, """
                { "RetrievalFusion": { "RrfK": 7 } }
                """);

            var config = BuildAsHostWould(hostAppsettingsWithOverride);
            var fusion = config.GetSection(RetrievalFusionOptions.SectionName).Get<RetrievalFusionOptions>();

            Assert.Equal(7, fusion!.RrfK);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
