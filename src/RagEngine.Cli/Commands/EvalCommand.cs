using System.Text.Json;
using System.Text.Json.Serialization;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RagEngine.Cli.Commands;

/// <summary>
/// Corre un eval-set de ground-truth (docs/eval/*.json) contra la búsqueda híbrida real
/// y calcula recall@K por categoría, comparable entre corridas.
///
/// Usage examples:
///   rag eval
///   rag eval --collection innovapp-docs --rerank
///   rag eval --eval-set docs/eval/innovapp-docs.eval-set.json --output json > baseline.json
/// </summary>
public sealed class EvalCommand : Command<EvalCommand.Settings>
{
    private static readonly int[] Cutoffs = [1, 3, 5, 10];

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--eval-set|-e")]
        public string EvalSetPath { get; init; } = "docs/eval/innovapp-docs.eval-set.json";

        [CommandOption("--collection|-c")]
        public string Collection { get; init; } = "innovapp-docs";

        [CommandOption("--top-k|-k")]
        public int TopK { get; init; } = 10;

        [CommandOption("--min-score|-s")]
        public float MinScore { get; init; } = 0.10f;

        [CommandOption("--rerank|-r")]
        public bool Rerank { get; init; }

        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    private readonly ISemanticRetriever _retriever;

    public EvalCommand(ISemanticRetriever retriever)
    {
        _retriever = retriever;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!File.Exists(settings.EvalSetPath))
        {
            AnsiConsole.MarkupLine($"[red]✗ No se encontró el eval-set:[/] {Markup.Escape(settings.EvalSetPath)}");
            return 1;
        }

        var items = JsonSerializer.Deserialize<List<EvalItem>>(
            File.ReadAllText(settings.EvalSetPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? [];

        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]⚠  El eval-set está vacío.[/]");
            return 0;
        }

        var cutoffs = Cutoffs.Where(k => k <= settings.TopK).ToArray();
        var results = new List<EvalItemResult>();

        // En modo --json, stdout es para el JSON final: cualquier progreso va a stderr
        // (el spinner de Spectre escribe directo a stdout y corrompería la salida).
        if (settings.Json)
        {
            int done = 0;
            foreach (var item in items)
            {
                Console.Error.WriteLine($"({++done}/{items.Count}) {item.Question}");
                results.Add(await RunOneAsync(item, settings, cutoffs));
            }
        }
        else
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots2)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("[cyan]Corriendo eval-set contra la búsqueda real...[/]", async ctx =>
                {
                    int done = 0;
                    foreach (var item in items)
                    {
                        ctx.Status($"[cyan]({++done}/{items.Count})[/] {Markup.Escape(item.Question)}");
                        results.Add(await RunOneAsync(item, settings, cutoffs));
                    }
                });
        }

        if (settings.Json)
        {
            var jsonOpts = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };
            Console.Write(JsonSerializer.Serialize(new
            {
                Collection = settings.Collection,
                settings.TopK,
                settings.Rerank,
                settings.MinScore,
                Results = results
            }, jsonOpts));
            return 0;
        }

        RenderReport(settings, results, cutoffs);
        return 0;
    }

    private async Task<EvalItemResult> RunOneAsync(EvalItem item, Settings settings, int[] cutoffs)
    {
        var options = new RetrievalOptions
        {
            CollectionName = settings.Collection,
            TopK = settings.TopK,
            MinimumSimilarityScore = settings.MinScore,
            UseReRanking = settings.Rerank
        };

        IReadOnlyList<RetrievalResult> hits;
        try
        {
            hits = await _retriever.SearchAsync(item.Question, options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Falló la búsqueda para \"{item.Question}\": {ex.Message}");
            hits = [];
        }

        return Evaluate(item, hits, cutoffs);
    }

    /// <summary>
    /// Calcula, para cada corte K, si al menos un anchor apareció (HitAny) y si TODOS
    /// los anchors aparecieron (HitFull) dentro de los primeros K resultados que
    /// coinciden con el archivo fuente esperado. Preguntas sin SourceFile (fuera-de-dominio)
    /// no tienen ground truth — solo se reporta el score más alto observado.
    /// </summary>
    private static EvalItemResult Evaluate(EvalItem item, IReadOnlyList<RetrievalResult> hits, int[] cutoffs)
    {
        float topScore = hits.Count > 0 ? hits[0].SimilarityScore : 0f;
        var targetFileNames = item.TargetFileNames;

        if (targetFileNames.Count == 0 || item.TargetContentContains.Count == 0)
        {
            return new EvalItemResult(item.Question, item.Category, item.SourceFile, topScore, [], []);
        }

        var hitAny = new Dictionary<int, bool>();
        var hitFull = new Dictionary<int, bool>();

        foreach (var k in cutoffs)
        {
            // Match por OR: cualquiera de los archivos objetivo cuenta como acierto —
            // preguntas con más de un documento igualmente válido (ej. portadas del PoC)
            // no se penalizan por evaluar contra un único SourceFile.
            var window = hits.Take(k)
                .Where(h => targetFileNames.Contains(Path.GetFileName(h.Metadata.RelativeFilePath), StringComparer.OrdinalIgnoreCase))
                .ToList();

            bool any = item.TargetContentContains.Any(anchor => window.Any(h => h.Content.Contains(anchor, StringComparison.Ordinal)));
            bool full = item.TargetContentContains.All(anchor => window.Any(h => h.Content.Contains(anchor, StringComparison.Ordinal)));

            hitAny[k] = any;
            hitFull[k] = full;
        }

        return new EvalItemResult(item.Question, item.Category, item.SourceFile, topScore, hitAny, hitFull);
    }

    private static void RenderReport(Settings settings, List<EvalItemResult> results, int[] cutoffs)
    {
        AnsiConsole.MarkupLine($"[dim]Eval-set:[/] [white]{settings.EvalSetPath}[/]  " +
                               $"[dim]Colección:[/] [white]{settings.Collection}[/]  " +
                               $"[dim]Top-K:[/] [white]{settings.TopK}[/]  " +
                               $"[dim]Rerank:[/] [white]{settings.Rerank}[/]");
        AnsiConsole.WriteLine();

        var anchored = results.Where(r => r.HitAnyAtK.Count > 0).ToList();
        var unanchored = results.Where(r => r.HitAnyAtK.Count == 0).ToList();

        // Tabla de recall@K por categoría (hit-any)
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Categoría");
        table.AddColumn("N");
        foreach (var k in cutoffs) table.AddColumn($"recall@{k}");

        foreach (var category in anchored.Select(r => r.Category).Distinct().OrderBy(c => c))
        {
            var group = anchored.Where(r => r.Category == category).ToList();
            var row = new List<string> { category, group.Count.ToString() };
            row.AddRange(cutoffs.Select(k => FormatPct(group.Count(r => r.HitAnyAtK[k]), group.Count)));
            table.AddRow(row.ToArray());
        }

        if (anchored.Count > 0)
        {
            var totalRow = new List<string> { "[bold]TOTAL (hit-any)[/]", anchored.Count.ToString() };
            totalRow.AddRange(cutoffs.Select(k => FormatPct(anchored.Count(r => r.HitAnyAtK[k]), anchored.Count)));
            table.AddRow(totalRow.ToArray());
        }

        // Cobertura completa (todos los anchors, relevante sobre todo para "ambigua")
        var ambiguous = anchored.Where(r => r.Category == "ambigua").ToList();
        if (ambiguous.Count > 0)
        {
            var fullRow = new List<string> { "[dim]ambigua (hit-full, todos los anchors)[/]", ambiguous.Count.ToString() };
            fullRow.AddRange(cutoffs.Select(k => FormatPct(ambiguous.Count(r => r.HitFullAtK[k]), ambiguous.Count)));
            table.AddRow(fullRow.ToArray());
        }

        AnsiConsole.Write(table);

        // Preguntas fallidas en el corte final (para inspección manual)
        var maxK = cutoffs[^1];
        var failed = anchored.Where(r => !r.HitAnyAtK[maxK]).ToList();
        if (failed.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]⚠ {failed.Count} pregunta(s) sin hit dentro de top-{maxK}:[/]");
            foreach (var f in failed)
                AnsiConsole.MarkupLine($"  [dim]•[/] {Markup.Escape($"[{f.Category}]")} {Markup.Escape(f.Question)}");
        }

        // Fuera de dominio: sin ground truth, solo se reporta el score más alto observado
        if (unanchored.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Fuera-de-dominio (sin ground truth — solo referencia del score más alto observado):[/]");
            foreach (var u in unanchored)
                AnsiConsole.MarkupLine($"  [dim]•[/] {Markup.Escape(u.Question)} [dim]→ top score:[/] {u.TopScore:F3}");
        }
    }

    private static string FormatPct(int hits, int total)
        => total == 0 ? "—" : $"{(100.0 * hits / total):F0}%";

    private sealed record EvalItem(
        string Question,
        string Category,
        string? SourceFile,
        string? TargetSectionHeader,
        List<string> TargetContentContains,
        string? Note,
        // Preguntas con más de un documento/chunk igualmente válido como objetivo
        // (ej. portadas del PoC de búsqueda libre, TargetRelativePaths[]). Si viene
        // poblado, gana sobre SourceFile — ver EvalItem.TargetFileNames.
        List<string>? SourceFiles = null)
    {
        /// <summary>Lista efectiva de nombres de archivo válidos como objetivo (match por OR).</summary>
        public IReadOnlyList<string> TargetFileNames =>
            SourceFiles is { Count: > 0 } multi ? multi
            : SourceFile is not null ? [SourceFile]
            : [];
    }

    private sealed record EvalItemResult(
        string Question,
        string Category,
        string? SourceFile,
        float TopScore,
        [property: JsonPropertyName("hit_any_at_k")] Dictionary<int, bool> HitAnyAtK,
        [property: JsonPropertyName("hit_full_at_k")] Dictionary<int, bool> HitFullAtK);
}
