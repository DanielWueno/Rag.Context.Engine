using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RagEngine.Cli.Commands;

/// <summary>
/// Consulta el estado de ingesta persistido por corrida/documento (ítem 13.1).
/// Sin argumentos muestra las últimas corridas de la colección; con --run-id
/// detalla el estado de cada documento de esa corrida específica — la vía para
/// "listar exactamente faltantes/fallidos" tras una ingesta interrumpida.
///
/// Usage:
///   rag ingest-status --collection mi-proyecto
///   rag ingest-status --collection mi-proyecto --run-id 3f9c...
///   rag ingest-status --collection mi-proyecto --run-id 3f9c... --failed-only
/// </summary>
public sealed class IngestStatusCommand : Command<IngestStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--collection|-c")]
        public string Collection { get; init; } = "default";

        [CommandOption("--run-id")]
        [System.ComponentModel.Description("Corrida específica a detallar. Sin este valor, lista las últimas corridas de la colección.")]
        public string? RunId { get; init; }

        [CommandOption("--failed-only")]
        [System.ComponentModel.Description("Con --run-id: sólo documentos que no terminaron Succeeded (pending/running/failed).")]
        public bool FailedOnly { get; init; }

        [CommandOption("--take")]
        public int Take { get; init; } = 10;
    }

    private readonly IIngestionStateStore _stateStore;

    public IngestStatusCommand(IIngestionStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (settings.RunId is not null)
            return await RenderRunDetailAsync(settings.RunId, settings.FailedOnly);

        return await RenderRunListAsync(settings.Collection, settings.Take);
    }

    private async Task<int> RenderRunListAsync(string collection, int take)
    {
        var runs = await _stateStore.ListRunsAsync(collection, take);
        if (runs.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No hay corridas de ingesta registradas para '{Markup.Escape(collection)}'.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Run Id").AddColumn("Estado").AddColumn("Actor")
            .AddColumn("Iniciada").AddColumn("Finalizada");

        foreach (var run in runs)
        {
            table.AddRow(
                Markup.Escape(run.RunId),
                ColorizeRunStatus(run.Status),
                Markup.Escape(run.ActorId),
                run.StartedAt.ToString("u"),
                run.FinishedAt?.ToString("u") ?? "[dim]—[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[dim]Usa --run-id <id> para ver el estado por documento de una corrida.[/]");
        return 0;
    }

    private async Task<int> RenderRunDetailAsync(string runId, bool failedOnly)
    {
        var run = await _stateStore.GetRunAsync(runId);
        if (run is null)
        {
            AnsiConsole.MarkupLine($"[red]✗ No existe la corrida '{Markup.Escape(runId)}'.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"Corrida [bold]{Markup.Escape(run.RunId)}[/] — {ColorizeRunStatus(run.Status)} " +
            $"(colección [bold cyan]{Markup.Escape(run.Collection)}[/], actor {Markup.Escape(run.ActorId)})");

        var states = await _stateStore.GetDocumentStatesAsync(runId);
        var filtered = failedOnly
            ? states.Where(s => s.Status != DocumentIngestionStatus.Succeeded).ToList()
            : states;

        if (filtered.Count == 0)
        {
            AnsiConsole.MarkupLine(failedOnly
                ? "[green]Todos los documentos de esta corrida terminaron Succeeded.[/]"
                : "[yellow]Esta corrida no tiene documentos registrados.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Documento").AddColumn("Estado").AddColumn("Chunks").AddColumn("Detalle");

        foreach (var state in filtered)
        {
            var chunks = state.ChunksExpected is int expected
                ? $"{state.ChunksIndexed}/{expected}"
                : $"{state.ChunksIndexed}/?";

            table.AddRow(
                Markup.Escape(state.DocumentKey),
                ColorizeDocumentStatus(state.Status),
                chunks,
                state.Detail is null ? "[dim]—[/]" : Markup.Escape(state.Detail));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static string ColorizeRunStatus(IngestionRunStatus status) => status switch
    {
        IngestionRunStatus.Succeeded => "[green]Succeeded[/]",
        IngestionRunStatus.Running => "[yellow]Running[/]",
        IngestionRunStatus.Cancelled => "[yellow]Cancelled[/]",
        IngestionRunStatus.Failed => "[red]Failed[/]",
        _ => Markup.Escape(status.ToString())
    };

    private static string ColorizeDocumentStatus(DocumentIngestionStatus status) => status switch
    {
        DocumentIngestionStatus.Succeeded => "[green]Succeeded[/]",
        DocumentIngestionStatus.Pending => "[dim]Pending[/]",
        DocumentIngestionStatus.Running => "[yellow]Running[/]",
        DocumentIngestionStatus.Failed => "[red]Failed[/]",
        _ => Markup.Escape(status.ToString())
    };
}
