using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Pipeline;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

namespace RagEngine.Cli.Commands;

/// <summary>
/// Spectre.Console command that performs semantic search against the Qdrant collection.
///
/// Usage examples:
///   rag search "validación de pedidos" --collection rag-test
///   rag search "inyección de dependencias" --language CSharp --top-k 5
///   rag search "event handler" --output markdown --min-score 0.65
///   rag search "DbContext usage" --output json
/// </summary>
public sealed class SearchCommand : Command<SearchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<query>")]
        public required string Query { get; init; }

        [CommandOption("--collection|-c")]
        public string Collection { get; init; } = "default";

        [CommandOption("--top-k|-k")]
        public int TopK { get; init; } = 10;

        [CommandOption("--min-score|-s")]
        public float MinScore { get; init; } = 0.10f;

        [CommandOption("--language|-l")]
        public SourceLanguage? Language { get; init; }

        [CommandOption("--namespace|-n")]
        public string? Namespace { get; init; }

        [CommandOption("--tenant")]
        [System.ComponentModel.Description("Filtra por tenant explícito de payload (ítem 5.e). Sin valor: sin restricción.")]
        public string? Tenant { get; init; }

        [CommandOption("--module")]
        [System.ComponentModel.Description("Filtra por módulo lógico derivado de namespace/ruta relativa. Sin valor: sin restricción.")]
        public string? Module { get; init; }

        [CommandOption("--rerank|-r")]
        public bool Rerank { get; init; }

        [CommandOption("--output|-o")]
        public OutputFormat Output { get; init; } = OutputFormat.Rich;

        [CommandOption("--max-tokens")]
        public int MaxContextTokens { get; init; } = 8_000;
    }

    private readonly ISemanticRetriever _retriever;
    private readonly IAuditEventStore _auditStore;
    private readonly IOptions<AuditOptions> _auditOptions;

    public SearchCommand(ISemanticRetriever retriever, IAuditEventStore auditStore, IOptions<AuditOptions> auditOptions)
    {
        _retriever = retriever;
        _auditStore = auditStore;
        _auditOptions = auditOptions;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        AnsiConsole.MarkupLine($"[dim]🔍 Buscando:[/] [bold cyan]{Markup.Escape(settings.Query)}[/]");
        AnsiConsole.MarkupLine($"[dim]   Colección:[/] [white]{settings.Collection}[/] " +
                               $"[dim]| Top-K:[/] [white]{settings.TopK}[/] " +
                               $"[dim]| Score mín:[/] [white]{settings.MinScore:F2}[/]");
        AnsiConsole.WriteLine();

        var options = new RetrievalOptions
        {
            Context = RetrievalContext.Local,
            CollectionName = settings.Collection,
            TopK = settings.TopK,
            MinimumSimilarityScore = settings.MinScore,
            FilterByLanguage = settings.Language,
            FilterByNamespace = settings.Namespace,
            FilterByTenant = settings.Tenant,
            FilterByModule = settings.Module,
            UseReRanking = settings.Rerank
        };

        IReadOnlyList<RetrievalResult> results = [];
        var auditCorrelationId = Guid.NewGuid().ToString();

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots2)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("[cyan]Vectorizando query y buscando en Qdrant...[/]", async ctx =>
                {
                    results = await _retriever.SearchAsync(settings.Query, options);
                    ctx.Status($"[green]✓ Búsqueda completa — {results.Count} resultado(s)[/]");
                });
        }
        catch (Exception ex)
        {
            await RecordQueryAuditAsync(settings.Collection, AuditOutcome.Failed, ex.Message);
            AnsiConsole.MarkupLine($"[red]✗ Error durante la búsqueda:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }

        await RecordQueryAuditAsync(settings.Collection, AuditOutcome.Success, detail: null);

        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]⚠  No se encontraron resultados.[/]");
            AnsiConsole.MarkupLine($"[dim]Intenta reducir [bold]--min-score[/] (actual: {settings.MinScore:F2}) " +
                                   "o ampliar la consulta.[/]");
            return 0;
        }

        async Task RecordQueryAuditAsync(string collection, AuditOutcome outcome, string? detail)
        {
            try
            {
                await _auditStore.RecordAsync(new AuditEvent
                {
                    EventId = Guid.NewGuid().ToString(),
                    CorrelationId = auditCorrelationId,
                    Operation = AuditOperations.QuerySearch,
                    ActorType = AuditActor.TypeLocalOperator,
                    ActorId = AuditActor.ResolveId(_auditOptions.Value),
                    Collection = collection,
                    Outcome = outcome,
                    Detail = detail,
                    Timestamp = DateTimeOffset.UtcNow,
                    Version = AuditEvent.CurrentVersion
                });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[grey](auditoría no persistida: {Markup.Escape(ex.Message)})[/]");
            }
        }

        switch (settings.Output)
        {
            case OutputFormat.Rich:
                RenderRichResults(results);
                break;

            case OutputFormat.Markdown:
                var md = new ContextAssembler().Assemble(
                    results, settings.Query, settings.MaxContextTokens);
                Console.Write(md);
                break;

            case OutputFormat.Json:
                var jsonOpts = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                };
                Console.Write(JsonSerializer.Serialize(results, jsonOpts));
                break;
        }

        return 0;
    }

    private static void RenderRichResults(IReadOnlyList<RetrievalResult> results)
    {
        foreach (var (result, i) in results.Select((r, idx) => (r, idx + 1)))
        {
            var m = result.Metadata;

            // Ítem 4.9: las bandas de color 0.85/0.70 sólo significan algo si el score es
            // comparable entre consultas. Sin --rerank el número es RRF —función del
            // puesto, típicamente ~0.03— y pintarlo de rojo con un "% similitud" al lado
            // le decía al lector que el resultado era malo cuando lo que pasaba es que la
            // escala era otra. Cuando no hay escala absoluta se muestra el número crudo
            // con el nombre de su escala y sin semáforo.
            var escalaEsAbsoluta = result.ScoreScale.IsComparableAcrossQueries();
            var scoreColor = !escalaEsAbsoluta                ? "grey"
                           : result.SimilarityScore >= 0.85f ? "green"
                           : result.SimilarityScore >= 0.70f ? "yellow"
                           : "red";
            var scoreTexto = escalaEsAbsoluta
                ? $"{(result.SimilarityScore * 100):F1}% similitud"
                : $"{result.SimilarityScore:F4} {result.ScoreScale.ToDisplayName()}";

            // Build header rows
            var rows = new List<IRenderable>
            {
                new Markup($"[dim]{Markup.Escape(m.RelativeFilePath)}[/] " +
                           $"[dim]L{m.StartLine}–{m.EndLine}[/]   " +
                           $"[{scoreColor} bold]{Markup.Escape(scoreTexto)}[/]")
            };

            if (!string.IsNullOrEmpty(m.Namespace) || !string.IsNullOrEmpty(m.ClassName))
            {
                var ns = string.IsNullOrEmpty(m.Namespace) ? "" : $"[dim]{Markup.Escape(m.Namespace)}.[/]";
                var cls = string.IsNullOrEmpty(m.ClassName) ? "" : $"[bold]{Markup.Escape(m.ClassName)}[/]";
                var method = string.IsNullOrEmpty(m.MethodName) ? "" : $"[dim]::[/][cyan]{Markup.Escape(m.MethodName)}[/]";
                rows.Add(new Markup($"{ns}{cls}{method}"));
            }

            rows.Add(new Rule { Style = Style.Parse("grey dim") });

            // Truncate content for display (full content in markdown/json output)
            var displayContent = result.Content.Length > 1000
                ? result.Content[..1000] + "\n[dim]... (truncado — usa --output markdown para ver completo)[/]"
                : result.Content;

            rows.Add(new Markup($"[green]{Markup.Escape(displayContent)}[/]"));

            var panel = new Panel(new Rows(rows))
            {
                Header = new PanelHeader($" [bold]#{i}[/]  [dim]{m.Language}[/] "),
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse("grey"),
                Padding = new Padding(1, 0)
            };

            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine(
            $"[dim]Mostrando [bold]{results.Count}[/] resultado(s). " +
            "Usa [bold]--output markdown[/] para formato de prompt LLM " +
            "o [bold]--output json[/] para integración programática.[/]");
    }
}

/// <summary>Output format for the search command.</summary>
public enum OutputFormat
{
    Rich,       // Spectre.Console panels with syntax highlighting
    Markdown,   // ContextAssembler output — ready for LLM system prompt
    Json        // JSON array of RetrievalResult objects
}
