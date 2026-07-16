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
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Error obteniendo info de '{collectionName}':[/] " +
                                   Markup.Escape(ex.Message));
            return;
        }

        var pointCount  = info.PointsCount;
        var vectorsCount = info.VectorsCount;
        var status      = info.Status.ToString();
        var statusColor = info.Status == CollectionStatus.Green ? "green"
                        : info.Status == CollectionStatus.Yellow ? "yellow"
                        : "red";

        // Disk size from optimizer status
        var diskBytes = info.OptimizerStatus is not null
            ? 0L   // not directly exposed; show N/A
            : 0L;

        // Build the info table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[dim]Métrica[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Valor[/]").RightAligned());

        table.AddRow("Colección",       $"[bold cyan]{Markup.Escape(collectionName)}[/]");
        table.AddRow("Estado",          $"[{statusColor} bold]{status}[/]");
        table.AddRow("Puntos totales",  $"[white]{pointCount:N0}[/]");
        table.AddRow("Vectores totales",$"[white]{vectorsCount:N0}[/]");

        // Vector config
        if (info.Config?.Params?.VectorsConfig?.ConfigCase == VectorsConfig.ConfigOneofCase.Params)
        {
            var vp = info.Config.Params.VectorsConfig.Params;
            table.AddRow("Dimensión vector", $"[white]{vp.Size}[/]");
            table.AddRow("Métrica",          $"[white]{vp.Distance}[/]");
        }

        // Segments count
        var segmentCount = info.SegmentsCount;
        table.AddRow("Segmentos",        $"[dim]{segmentCount}[/]");

        AnsiConsole.Write(
            new Panel(table)
            {
                Header = new PanelHeader($" 📊 [bold]{Markup.Escape(collectionName)}[/] "),
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse(statusColor)
            });
    }
}
