using RagEngine.Core.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RagEngine.Cli.Commands;

/// <summary>
/// CLI command that runs the full RAG pipeline against the local Ollama LLM
/// and streams the answer token-by-token to the terminal.
///
/// Usage examples:
///   rag ask "¿Cómo funciona el pipeline de ingestión?"
///   rag ask "Explica AuthController" --collection mi-proyecto --top-k 8
///   rag ask "¿Dónde se registra QdrantClient?" --min-score 0.70 --no-stream
/// </summary>
public sealed class AskCommand : AsyncCommand<AskCommand.Settings>
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Settings (CLI arguments + options)
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<query>")]
        [System.ComponentModel.Description("Natural-language question about the indexed codebase.")]
        public required string Query { get; init; }

        [CommandOption("--collection|-c")]
        [System.ComponentModel.Description("Qdrant collection to search. Defaults to 'default'.")]
        public string Collection { get; init; } = "default";

        [CommandOption("--top-k|-k")]
        [System.ComponentModel.Description("Maximum number of code chunks to retrieve (default: 5).")]
        public int TopK { get; init; } = 5;

        [CommandOption("--min-score|-s")]
        [System.ComponentModel.Description("Minimum dense cosine threshold for retrieved chunks (default: 0.10; relevant question↔code pairs score ~0.12-0.25 with the multilingual model).")]
        public float MinScore { get; init; } = 0.10f;

        [CommandOption("--no-stream")]
        [System.ComponentModel.Description("Buffer the full response and print it at once instead of streaming.")]
        public bool NoStream { get; init; } = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Dependencies
    // ─────────────────────────────────────────────────────────────────────────

    private readonly IRagGenerationService _generation;

    public AskCommand(IRagGenerationService generation)
    {
        _generation = generation;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Entry point
    // ─────────────────────────────────────────────────────────────────────────

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();

        // Allow Ctrl+C to gracefully cancel the streaming response.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;          // prevent abrupt process kill
            cts.Cancel();
        };

        PrintHeader(settings);

        return settings.NoStream
            ? await RunBufferedAsync(settings, cts.Token)
            : await RunStreamingAsync(settings, cts.Token);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Streaming mode (default)
    //  Each token is written to stdout as it arrives — zero buffering.
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<int> RunStreamingAsync(Settings settings, CancellationToken ct)
    {
        // Phase 1 — Retrieval phase: show a spinner while Qdrant + ONNX run.
        // We cannot start streaming yet because we don't know if retrieval will
        // succeed. The spinner stops the moment the first LLM token arrives.
        var retrievalDone = false;

        // Start a background status display that we'll stop on first token.
        var spinnerTask = AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots2)
            .SpinnerStyle(Style.Parse("cyan bold"))
            .StartAsync(
                "[cyan]Recuperando contexto y contactando Ollama...[/]",
                async statusCtx =>
                {
                    // The spinner lives until retrievalDone is flipped.
                    while (!retrievalDone && !ct.IsCancellationRequested)
                        await Task.Delay(50, ct).ConfigureAwait(false);

                    statusCtx.Status("[green]✓ Contexto listo — generando respuesta[/]");
                });

        try
        {
            PrintAnswerHeader();

            var tokenCount  = 0;
            var firstToken  = true;

            await foreach (var fragment in _generation
                .AskStreamingAsync(settings.Query, settings.Collection, settings.TopK, settings.MinScore, ct)
                .ConfigureAwait(false))
            {
                if (firstToken)
                {
                    // Signal spinner to stop and transition to streaming output.
                    retrievalDone = true;
                    firstToken    = false;

                    // Small pause so the spinner has time to render its final state
                    // before we start overwriting the console with streaming text.
                    await Task.Delay(80, ct).ConfigureAwait(false);
                    AnsiConsole.WriteLine();
                }

                // Write raw to stdout — no Markup escaping — so Markdown code fences
                // and formatting characters pass through untouched.
                Console.Write(fragment);
                tokenCount++;
            }

            // Ensure cursor moves to a new line after the last token.
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine();

            PrintFooter(tokenCount, settings);
            return 0;
        }
        catch (OperationCanceledException)
        {
            retrievalDone = true;
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("\n[yellow]⚡ Generación cancelada por el usuario.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            retrievalDone = true;
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red bold]✗ Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }
        finally
        {
            // Always terminate the spinner task even if we throw.
            retrievalDone = true;
            await spinnerTask.ConfigureAwait(false);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Buffered mode (--no-stream)
    //  Collects the full response then renders it as Spectre Markup (with
    //  a Markdown-like panel). Useful for piping output or CI contexts.
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<int> RunBufferedAsync(Settings settings, CancellationToken ct)
    {
        var buffer = new System.Text.StringBuilder();

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots2)
                .SpinnerStyle(Style.Parse("cyan bold"))
                .StartAsync("[cyan]Generando respuesta completa...[/]", async _ =>
                {
                    await foreach (var fragment in _generation
                        .AskStreamingAsync(settings.Query, settings.Collection, settings.TopK, settings.MinScore, ct)
                        .ConfigureAwait(false))
                    {
                        buffer.Append(fragment);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]⚡ Cancelado.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]✗ Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }

        var fullResponse = buffer.ToString();

        PrintAnswerHeader();
        AnsiConsole.WriteLine();

        // Render inside a panel for a premium buffered look.
        var panel = new Panel(new Markup(Markup.Escape(fullResponse)))
        {
            Border      = BoxBorder.Rounded,
            BorderStyle = Style.Parse("cyan"),
            Padding     = new Padding(1, 0)
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        PrintFooter(fullResponse.Length, settings);
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  UI helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void PrintHeader(Settings s)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan bold] RAG · Ask [/]") { Justification = Justify.Left });
        AnsiConsole.MarkupLine($"[dim]  Pregunta  :[/] [bold white]{Markup.Escape(s.Query)}[/]");
        AnsiConsole.MarkupLine($"[dim]  Colección :[/] [white]{Markup.Escape(s.Collection)}[/]  " +
                               $"[dim]Top-K:[/] [white]{s.TopK}[/]  " +
                               $"[dim]Min-Score:[/] [white]{s.MinScore:F2}[/]  " +
                               $"[dim]Modo:[/] [white]{(s.NoStream ? "buffered" : "streaming")}[/]");
        AnsiConsole.Write(new Rule { Style = Style.Parse("grey dim") });
        AnsiConsole.WriteLine();
    }

    private static void PrintAnswerHeader()
    {
        AnsiConsole.Write(new Rule("[green bold] Respuesta [/]") { Justification = Justify.Left });
    }

    private static void PrintFooter(int count, Settings s)
    {
        var unit = s.NoStream ? "chars" : "tokens";
        AnsiConsole.Write(new Rule { Style = Style.Parse("grey dim") });
        AnsiConsole.MarkupLine(
            $"[dim]  {count} {unit} generados  ·  colección: [bold]{Markup.Escape(s.Collection)}[/]  " +
            $"·  Ctrl+C para cancelar[/]");
        AnsiConsole.WriteLine();
    }
}
