using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;

namespace RagEngine.Cli.Commands;

/// <summary>
/// CLI command: rag ingest &lt;path&gt; [options]
///
/// Ingests a codebase into the Qdrant vector store, displaying a live
/// Spectre.Console progress dashboard with real-time stats.
///
/// Usage examples:
///   rag ingest /path/to/repo
///   rag ingest /path/to/repo --collection my-project --force
///   rag ingest /path/to/repo --collection my-project --lang csharp
/// </summary>
public sealed class IngestCommand : AsyncCommand<IngestCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<path>")]
        [Description("Root path of the repository to ingest.")]
        public string RepositoryPath { get; set; } = string.Empty;

        [CommandOption("-c|--collection")]
        [Description("Qdrant collection name. Defaults to 'rag-engine'.")]
        public string Collection { get; set; } = "rag-engine";

        [CommandOption("-r|--repo-name")]
        [Description("Repository name used in chunk context headers.")]
        public string RepositoryName { get; set; } = "my-repo";

        [CommandOption("-b|--batch-size")]
        [Description("Number of chunks per ONNX batch. Default: 32.")]
        public int BatchSize { get; set; } = 32;

        [CommandOption("-f|--force")]
        [Description("Force re-index: deletes and recreates the Qdrant collection.")]
        public bool ForceReindex { get; set; } = false;

        [CommandOption("-l|--lang")]
        [Description("Restrict to a language: csharp, typescript, sql, markdown.")]
        public string? LanguageFilter { get; set; }

        public override ValidationResult Validate()
        {
            if (!Directory.Exists(RepositoryPath))
                return ValidationResult.Error(
                    $"[red]Repository path not found:[/] {RepositoryPath}");

            if (BatchSize is < 1 or > 256)
                return ValidationResult.Error(
                    "[red]--batch-size must be between 1 and 256.[/]");

            return ValidationResult.Success();
        }
    }

    private readonly IIngestionPipeline _pipeline;
    private readonly ILogger<IngestCommand> _logger;

    public IngestCommand(IIngestionPipeline pipeline, ILogger<IngestCommand> logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // \u2500\u2500 Header Banner \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        AnsiConsole.Write(
            new FigletText("RAG Engine")
                .Centered()
                .Color(Color.Blue));

        AnsiConsole.Write(new Rule("[blue]Sprint 1 \u2014 Ingestion Pipeline[/]").RuleStyle("blue dim"));
        AnsiConsole.WriteLine();

        // \u2500\u2500 Configuration Summary \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        var configTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Setting[/]")
            .AddColumn("[white]Value[/]");

        configTable.AddRow("[grey]Path[/]",       $"[green]{settings.RepositoryPath}[/]");
        configTable.AddRow("[grey]Collection[/]", $"[cyan]{settings.Collection}[/]");
        configTable.AddRow("[grey]Repo Name[/]",  $"[cyan]{settings.RepositoryName}[/]");
        configTable.AddRow("[grey]Batch Size[/]", $"[yellow]{settings.BatchSize}[/]");
        configTable.AddRow("[grey]Force Re-index[/]",
            settings.ForceReindex ? "[red]YES \u26a0\ufe0f[/]" : "[green]No[/]");

        AnsiConsole.Write(configTable);
        AnsiConsole.WriteLine();

        // \u2500\u2500 ForceReindex confirmation \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        if (settings.ForceReindex)
        {
            var confirmed = AnsiConsole.Confirm(
                $"[red]\u26a0\ufe0f  This will DELETE collection '[cyan]{settings.Collection}[/]' and re-index from scratch. Continue?[/]",
                defaultValue: false);

            if (!confirmed)
            {
                AnsiConsole.MarkupLine("[yellow]Aborted.[/]");
                return 0;
            }
        }

        // \u2500\u2500 Build Ingestion Request \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        var request = new IngestionRequest(
            RepositoryPath: settings.RepositoryPath,
            CollectionName: settings.Collection,
            Profile: ScanProfile.DotNetEnterprise,
            Options: new ChunkingOptions
            {
                MaxTokensPerChunk = 512,
                OverlapTokens = 64,
                RepositoryName = settings.RepositoryName,
                BatchSize = settings.BatchSize
            },
            ForceReindex: settings.ForceReindex
        );

        // \u2500\u2500 Live Progress Display \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        IngestionSummary? summary = null;
        Exception? error = null;

        var liveProgress = new LiveProgressTracker();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            AnsiConsole.MarkupLine("\n[yellow]Cancellation requested...[/]");
            cts.Cancel();
        };

        try
        {
            await AnsiConsole.Live(liveProgress.GetLayout())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async ctx =>
                {
                    var progress = new Progress<IngestionProgress>(p =>
                    {
                        liveProgress.Update(p);
                        ctx.Refresh();
                    });

                    summary = await _pipeline.IngestRepositoryAsync(request, progress, cts.Token);
                });
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Ingestion cancelled.[/]");
            return 1;
        }
        catch (FileNotFoundException ex) when (ex.FileName?.Contains(".onnx") == true ||
                                                ex.FileName?.Contains("vocab") == true)
        {
            AnsiConsole.MarkupLine($"[red]\u274c Model files not found.[/]");
            AnsiConsole.MarkupLine("[grey]Run: [white]bash infra/download-model.sh[/][/]");
            return 2;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }

        // \u2500\u2500 Final Summary Table \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]\u2705 Ingestion Complete[/]").RuleStyle("green"));
        AnsiConsole.WriteLine();

        if (summary is not null)
        {
            var summaryTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Green)
                .AddColumn("[green]Metric[/]")
                .AddColumn("[white]Value[/]");

            summaryTable.AddRow("Files Scanned",    $"[cyan]{summary.FilesScanned:N0}[/]");
            summaryTable.AddRow("Chunks Generated", $"[cyan]{summary.ChunksGenerated:N0}[/]");
            summaryTable.AddRow("Chunks Indexed",   $"[green]{summary.ChunksIndexed:N0}[/]");
            summaryTable.AddRow("Files Skipped",    $"[yellow]{summary.FilesSkipped:N0}[/]");
            summaryTable.AddRow("Duration",         $"[white]{summary.TotalDuration:mm\\:ss\\.ff}[/]");
            summaryTable.AddRow("Memory Peak",
                $"[grey]{summary.EstimatedMemoryPeakBytes / 1_048_576.0:F1} MB[/]");

            AnsiConsole.Write(summaryTable);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[dim]Collection [cyan]{settings.Collection}[/] is ready for semantic search.[/]");
        AnsiConsole.MarkupLine(
            $"[dim]Run: [white]rag search \"your query\"[/][/]");

        return 0;
    }

    // \u2500\u2500 Live progress tracker using Spectre.Console Table \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    private sealed class LiveProgressTracker
    {
        private readonly Table _table;
        private int _filesProcessed;
        private int _chunksProduced;
        private int _chunksIndexed;
        private string _stage = "Starting...";
        private string _currentFile = string.Empty;

        public LiveProgressTracker()
        {
            _table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Blue)
                .Title("[blue bold]\ud83d\ude80 Ingestion in Progress[/]")
                .AddColumn("[grey]Metric[/]")
                .AddColumn("[white]Value[/]");
        }

        public void Update(IngestionProgress p)
        {
            _filesProcessed = p.FilesProcessed;
            _chunksProduced = p.ChunksProduced;
            _chunksIndexed  = p.ChunksIndexed;
            _stage          = p.Stage.ToString();
            _currentFile    = p.CurrentFile.Length > 60
                ? "\u2026" + p.CurrentFile[^57..]
                : p.CurrentFile;
        }

        public Table GetLayout()
        {
            _table.Rows.Clear();
            _table.AddRow("Stage",           $"[yellow]{_stage}[/]");
            _table.AddRow("Files Processed", $"[cyan]{_filesProcessed:N0}[/]");
            _table.AddRow("Chunks Produced", $"[cyan]{_chunksProduced:N0}[/]");
            _table.AddRow("Chunks Indexed",  $"[green]{_chunksIndexed:N0}[/]");
            _table.AddRow("Current File",    $"[grey]{Markup.Escape(_currentFile)}[/]");
            return _table;
        }
    }
}
