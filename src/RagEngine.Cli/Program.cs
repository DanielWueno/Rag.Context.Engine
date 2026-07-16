using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RagEngine.Cli.Commands;
using RagEngine.Cli.Infrastructure;
using RagEngine.Core.Extensions;
using Spectre.Console.Cli;

// ──────────────────────────────────────────────────────────────────────────────
// RagEngine CLI — Entry Point
// ──────────────────────────────────────────────────────────────────────────────
// Uses Microsoft.Extensions.Hosting for configuration + DI, then hands off
// to Spectre.Console.Cli for command parsing and execution.
// ──────────────────────────────────────────────────────────────────────────────

// Build the host for configuration and DI
var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddRagEngineCore(ctx.Configuration);

        // Register CLI commands for DI injection
        services.AddTransient<IngestCommand>();
    })
    .Build();

// Create the Spectre.Console.Cli app with DI-backed type registrar
var registrar = new SpectreTypeRegistrar(new ServiceCollection());

// Hydrate the registrar with the already-built host services
// (so commands get fully resolved services from the host container)
var app = new CommandApp(new SpectreHostTypeRegistrar(host.Services));

app.Configure(config =>
{
    config.SetApplicationName("rag");

    config.AddCommand<IngestCommand>("ingest")
        .WithDescription("Ingest a codebase into the Qdrant vector store.")
        .WithExample(["ingest", "/path/to/repo"])
        .WithExample(["ingest", "/path/to/repo", "--collection", "my-project", "--force"]);

    config.SetExceptionHandler((ex, _) =>
    {
        Spectre.Console.AnsiConsole.WriteException(ex, Spectre.Console.ExceptionFormats.ShortenEverything);
        return -1;
    });
});

return await app.RunAsync(args);
