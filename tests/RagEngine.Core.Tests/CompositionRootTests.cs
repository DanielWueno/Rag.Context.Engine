using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Extensions;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Comprueba que el grafo de DI se puede construir y que la generación se resuelve.
///
/// Por qué existe: al partir <c>RagGenerationService</c> (ítem 2.2), sus registros
/// pasaron de ser por tipo a fábricas explícitas —los colaboradores son <c>internal</c>
/// y <c>ActivatorUtilities</c> sólo mira constructores públicos—. <c>ValidateOnBuild</c>
/// no puede introspeccionar un delegado, así que ese cambio dejó el camino de generación
/// fuera de la validación estática del arranque: borrar el registro de
/// <c>SummaryCache</c> ya no reventaría al arrancar, sino como un 500 en el primer
/// <c>/api/ask</c>. Este test devuelve esa red, y encima cubre lo que
/// <c>ValidateOnBuild</c> nunca cubrió: resolver de verdad el nodo raíz.
///
/// No necesita Qdrant, ni Ollama, ni los modelos ONNX en disco: las sesiones de
/// inferencia se cargan perezosamente, así que los constructores resuelven sin ficheros.
/// </summary>
public class CompositionRootTests
{
    private static IConfiguration Configuracion() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Qdrant:Host"]        = "localhost",
            ["Qdrant:GrpcPort"]    = "6334",
            ["Ollama:Endpoint"]    = "http://localhost:11434/v1",
            ["Ollama:ModelId"]     = "qwen2.5-coder",
        }).Build();

    private static ServiceProvider Construir()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRagEngineCore(Configuracion());
        services.AddRagEngineGeneration(Configuracion());

        // Las dos validaciones que el host de producción NO tiene encendidas: aquí sí,
        // porque es exactamente lo que este test viene a cubrir.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes  = true,
        });
    }

    [Fact]
    public void ElGrafoDeDiSeConstruyeYValida()
    {
        using var proveedor = Construir();
        Assert.NotNull(proveedor);
    }

    /// <summary>
    /// La parte que <c>ValidateOnBuild</c> no puede hacer por nosotros: ejecutar las
    /// fábricas. Si a un colaborador le falta una dependencia, o si alguno pasara a
    /// depender de algo Scoped siendo Singleton, esto falla aquí y no en producción.
    /// </summary>
    [Fact]
    public void LaGeneracionSeResuelveDesdeUnScope()
    {
        using var proveedor = Construir();
        using var scope = proveedor.CreateScope();

        var generacion = scope.ServiceProvider.GetRequiredService<IRagGenerationService>();

        Assert.NotNull(generacion);
    }

    /// <summary>
    /// Ítem 9.4: al extraer <c>ISummaryCache</c>, el camino de generación ya estaba
    /// cubierto (<see cref="LaGeneracionSeResuelveDesdeUnScope"/>) pero el de ingesta no
    /// — <c>DefaultIngestionPipeline</c> es <c>AddScoped&lt;IIngestionPipeline, ...&gt;</c>,
    /// así que un registro roto de <c>ISummaryCache</c> ahí también pasaría <c>ValidateOnBuild</c>
    /// sin invocar el constructor real. Sobrescribe <c>Ingestion:ResumenCachePath</c> a un
    /// archivo temporal: el default apunta a la caché SQLite real compartida entre
    /// colecciones, y este test no debe abrirla ni tocar su WAL.
    /// </summary>
    [Fact]
    public void LaIngestionSeResuelveDesdeUnScope()
    {
        var rutaTemporal = Path.Combine(
            Path.GetTempPath(), $"ragengine-composition-root-test-{Guid.NewGuid():N}.sqlite3");
        try
        {
            var configuracion = new ConfigurationBuilder()
                .AddConfiguration(Configuracion())
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ingestion:ResumenCachePath"] = rutaTemporal,
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddRagEngineCore(configuracion);

            using var proveedor = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            using var scope = proveedor.CreateScope();

            var pipeline = scope.ServiceProvider.GetRequiredService<IIngestionPipeline>();

            Assert.NotNull(pipeline);
        }
        finally
        {
            foreach (var sufijo in new[] { "", "-wal", "-shm" })
            {
                var archivo = rutaTemporal + sufijo;
                if (File.Exists(archivo)) File.Delete(archivo);
            }
        }
    }
}
