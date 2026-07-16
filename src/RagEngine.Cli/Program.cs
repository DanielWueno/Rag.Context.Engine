using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;
using RagEngine.Cli.Commands;
using RagEngine.Cli.Infrastructure;
using RagEngine.Core.Extensions;
using Spectre.Console;
using Spectre.Console.Cli;

// ──────────────────────────────────────────────────────────────────────────────
// RagEngine CLI — Entry Point
// Wires Microsoft.Extensions.Hosting (DI + config) → Spectre.Console.Cli (UX)
// ──────────────────────────────────────────────────────────────────────────────

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} <s:{SourceContext}>{NewLine}{Exception}")
    .WriteTo.File(
        formatter: new CompactJsonFormatter(),
        path: "logs/rag-engine-.json",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7)
    .CreateLogger();

try
{
    Log.Information("Arrancando proceso Rag.Context.Engine CLI...");

    var host = Host.CreateDefaultBuilder(args)
        .UseContentRoot(AppContext.BaseDirectory)
        .ConfigureLogging(logging => 
        {
            logging.ClearProviders();
            logging.AddSerilog(dispose: true);
        })
        .ConfigureServices((ctx, services) =>
        {
            // Core services: ONNX brain, Qdrant store, chunking pipeline
            services.AddRagEngineCore(ctx.Configuration);

            // Generation pipeline: Semantic Kernel + Ollama connector + RagGenerationService
            services.AddRagEngineGeneration(ctx.Configuration);

            // CLI commands registered for DI
            services.AddTransient<IngestCommand>();
            services.AddTransient<SearchCommand>();
            services.AddTransient<StatusCommand>();
            services.AddTransient<AskCommand>();
            services.AddTransient<DoctorCommand>();
        })
        .Build();

// Hand off to Spectre.Console.Cli with host DI container
var app = new CommandApp(new SpectreHostTypeRegistrar(host.Services));

app.Configure(config =>
{
    config.SetApplicationName("rag");
    config.SetApplicationVersion("1.0.0-sprint4");

    config.AddCommand<IngestCommand>("ingest")
        .WithDescription("Indexa un repositorio de código en la base vectorial Qdrant.")
        .WithExample(["ingest", "/path/to/repo"])
        .WithExample(["ingest", "/path/to/repo", "--collection", "mi-proyecto", "--force"]);

    config.AddCommand<SearchCommand>("search")
        .WithDescription("Busca código semánticamente relevante mediante lenguaje natural.")
        .WithExample(["search", "validación de pedidos", "--collection", "rag-test"])
        .WithExample(["search", "event handler", "--language", "CSharp", "--top-k", "5"])
        .WithExample(["search", "DbContext usage", "--output", "markdown"]);

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Muestra estadísticas de la colección vectorial en Qdrant.")
        .WithExample(["status"])
        .WithExample(["status", "--collection", "mi-proyecto"])
        .WithExample(["status", "--all"]);

    config.AddCommand<AskCommand>("ask")
        .WithDescription("Realiza una pregunta en lenguaje natural y obtiene una respuesta generada por el LLM local (Ollama).")
        .WithExample(["ask", "\"¿Cómo funciona el pipeline de ingestión?\""])
        .WithExample(["ask", "\"Explica AuthController\"", "--collection", "mi-proyecto", "--top-k", "8"])
        .WithExample(["ask", "\"¿Dónde se registra QdrantClient?\"", "--no-stream"]);

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Verifica las dependencias del sistema (Qdrant, ONNX, Disco).")
        .WithExample(["doctor"]);

    config.SetExceptionHandler((ex, _) =>
    {
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
        return -1;
    });
});

    return await app.RunAsync(args);
}
catch (Exception ex)
{
    Log.Fatal(ex, "El host terminó inesperadamente debido a una excepción fatal.");
    return -1;
}
finally
{
    Log.CloseAndFlush();
}
