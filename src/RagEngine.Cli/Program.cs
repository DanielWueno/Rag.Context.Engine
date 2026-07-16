using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qdrant.Client;
using RagEngine.Cli.Commands;
using RagEngine.Cli.Infrastructure;
using RagEngine.Core.Extensions;
using Spectre.Console;
using Spectre.Console.Cli;

// ──────────────────────────────────────────────────────────────────────────────
// RagEngine CLI — Entry Point
// Wires Microsoft.Extensions.Hosting (DI + config) → Spectre.Console.Cli (UX)
// ──────────────────────────────────────────────────────────────────────────────

var host = Host.CreateDefaultBuilder(args)
    .UseContentRoot(AppContext.BaseDirectory)
    .ConfigureServices((ctx, services) =>
    {
        // Core services: ONNX brain, Qdrant store, chunking pipeline
        services.AddRagEngineCore(ctx.Configuration);

        // CLI commands registered for DI
        services.AddTransient<IngestCommand>();
        services.AddTransient<SearchCommand>();
        services.AddTransient<StatusCommand>();
    })
    .Build();

// Hand off to Spectre.Console.Cli with host DI container
var app = new CommandApp(new SpectreHostTypeRegistrar(host.Services));

app.Configure(config =>
{
    config.SetApplicationName("rag");
    config.SetApplicationVersion("1.0.0-sprint2");

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

    config.SetExceptionHandler((ex, _) =>
    {
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
        return -1;
    });
});

return await app.RunAsync(args);
