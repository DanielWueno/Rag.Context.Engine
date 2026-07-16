using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Domain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RagEngine.Cli.Commands;

/// <summary>
/// Displays statistics and health information about a Qdrant collection.
///
/// Usage:
///   rag status
///   rag status --collection my-project
/// </summary>
public sealed class StatusCommand : Command<StatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--collection|-c")]
        public string Collection { get; init; } = "default";

        [CommandOption("--all|-a")]
        public bool ShowAll { get; init; }
    }

    private readonly QdrantClient _qdrant;

    public StatusCommand(QdrantClient qdrant)
    {
        _qdrant = qdrant;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        // ── List all collections ─────────────────────────────────────────────────
        IReadOnlyList<string> allCollections;
        try
        {
            allCollections = await _qdrant.ListCollectionsAsync();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]✗ No se puede conectar a Qdrant:[/] " + Markup.Escape(ex.Message));
            AnsiConsole.MarkupLine("[dim]Verifica que Qdrant esté corriendo en Docker: [bold]docker compose up[/][/]");
            return 1;
        }

        if (allCollections.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No hay colecciones indexadas aún.[/]");
            AnsiConsole.MarkupLine("[dim]Ejecuta [bold]rag ingest <path>[/] para crear una colección.[/]");
            return 0;
        }

        // Determine which collections to show
        var targets = settings.ShowAll
            ? allCollections
            : allCollections.Contains(settings.Collection)
                ? [settings.Collection]
                : allCollections;

        foreach (var name in targets)
        {
            await RenderCollectionStatusAsync(name);
            AnsiConsole.WriteLine();
        }

        return 0;
    }

    private async Task RenderCollectionStatusAsync(string collectionName)
    {
        CollectionInfo? info;
        try
        {
            info = await _qdrant.GetCollectionInfoAsync(collectionName);
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine($"[red]La colección '{Markup.Escape(collectionName)}' no existe o no se pudo acceder.[/]");
            return;
        }

        var pointCount  = info.PointsCount;
        var status      = info.Status.ToString();
        var statusColor = info.Status == CollectionStatus.Green ? "green"
                        : info.Status == CollectionStatus.Yellow ? "yellow"
                        : "red";

        // Creamos una tabla sin bordes exteriores para meterla en un Panel
        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("Propiedad").LeftAligned())
            .AddColumn(new TableColumn("Valor").LeftAligned());

        table.AddRow("[dim]Estado[/]", $"[{statusColor}]{status}[/]");
        table.AddRow("[dim]Puntos totales[/]", $"[white]{pointCount:N0}[/]");

        // Extraer configuración de vectores
        if (info.Config?.Params?.VectorsConfig?.ConfigCase == VectorsConfig.ConfigOneofCase.Params)
        {
            var vp = info.Config.Params.VectorsConfig.Params;
            table.AddRow("[dim]Dimensiones vector[/]", $"[white]{vp.Size}[/]");
        }

        var panel = new Panel(table)
        {
            Header = new PanelHeader($" Colección: [bold cyan]{Markup.Escape(collectionName)}[/] ", Justify.Left),
            Border = BoxBorder.Square,
            Padding = new Padding(2, 0),
            Expand = false
        };

        AnsiConsole.Write(panel);
    }
}
