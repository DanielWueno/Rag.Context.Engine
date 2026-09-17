using Microsoft.Extensions.Options;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
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

        [CommandOption("--rerank|-r")]
        [System.ComponentModel.Description("Re-score a 3×top-k candidate pool with the multilingual Cross-Encoder before building the LLM context (higher precision, ~1-2s extra).")]
        public bool Rerank { get; init; } = false;

        [CommandOption("--no-stream")]
        [System.ComponentModel.Description("Buffer the full response and print it at once instead of streaming.")]
        public bool NoStream { get; init; } = false;

        [CommandOption("--technical|-t")]
        [System.ComponentModel.Description("Answer with code citations and fenced code blocks, for developers. Default (off) answers in plain, non-technical language with no raw code shown.")]
        public bool Technical { get; init; } = false;

        public ResponseMode ResponseMode => Technical ? ResponseMode.Technical : ResponseMode.Simple;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Dependencies
    // ─────────────────────────────────────────────────────────────────────────

    private readonly IRagGenerationService _generation;
    private readonly IAuditEventStore _auditStore;
    private readonly IOptions<AuditOptions> _auditOptions;

    public AskCommand(IRagGenerationService generation, IAuditEventStore auditStore, IOptions<AuditOptions> auditOptions)
    {
        _generation = generation;
        _auditStore = auditStore;
        _auditOptions = auditOptions;
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
        var auditCorrelationId = Guid.NewGuid().ToString();

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

            await foreach (var update in _generation
                .AskStreamingAsync(settings.Query, settings.Collection, RetrievalContext.Local, settings.TopK, settings.MinScore, settings.Rerank, responseMode: settings.ResponseMode, cancellationToken: ct)
                .ConfigureAwait(false))
            {
                if (update is not GenerationEvent.TextDelta fragment)
                    continue;

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
                Console.Write(fragment.Text);
                tokenCount++;
            }

            // Ensure cursor moves to a new line after the last token.
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine();

            PrintFooter(tokenCount, settings);
            await RecordQueryAuditAsync(settings, auditCorrelationId, AuditOutcome.Success, detail: null);
            return 0;
        }
        catch (OperationCanceledException)
        {
            retrievalDone = true;
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("\n[yellow]⚡ Generación cancelada por el usuario.[/]");
            await RecordQueryAuditAsync(settings, auditCorrelationId, AuditOutcome.Cancelled, detail: null);
            return 0;
        }
        catch (Exception ex)
        {
            retrievalDone = true;
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red bold]✗ Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            await RecordQueryAuditAsync(settings, auditCorrelationId, AuditOutcome.Failed, ex.Message);
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
        var auditCorrelationId = Guid.NewGuid().ToString();
        var buffer = new System.Text.StringBuilder();

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots2)
                .SpinnerStyle(Style.Parse("cyan bold"))
                .StartAsync("[cyan]Generando respuesta completa...[/]", async _ =>
                {
                    await foreach (var update in _generation
                        .AskStreamingAsync(settings.Query, settings.Collection, RetrievalContext.Local, settings.TopK, settings.MinScore, settings.Rerank, responseMode: settings.ResponseMode, cancellationToken: ct)
                        .ConfigureAwait(false))
                    {
                        if (update is GenerationEvent.TextDelta fragment)
                            buffer.Append(fragment.Text);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]⚡ Cancelado.[/]");
            await RecordQueryAuditAsync(settings, auditCorrelationId, AuditOutcome.Cancelled, detail: null);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red bold]✗ Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            await RecordQueryAuditAsync(settings, auditCorrelationId, AuditOutcome.Failed, ex.Message);
            return 1;
        }

        await RecordQueryAuditAsync(settings, auditCorrelationId, AuditOutcome.Success, detail: null);

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
    //  Auditoría (ítem 12.11): un evento por invocación de `rag ask`, con
    //  CancellationToken.None a propósito — no debe abortarse por la misma
    //  cancelación que está registrando, y un fallo al persistir se avisa por
    //  consola pero nunca convierte una respuesta ya entregada en un error.
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RecordQueryAuditAsync(Settings settings, string correlationId, AuditOutcome outcome, string? detail)
    {
        try
        {
            await _auditStore.RecordAsync(new AuditEvent
            {
                EventId = Guid.NewGuid().ToString(),
                CorrelationId = correlationId,
                Operation = AuditOperations.QueryAsk,
                ActorType = AuditActor.TypeLocalOperator,
                ActorId = AuditActor.ResolveId(_auditOptions.Value),
                Collection = settings.Collection,
                Outcome = outcome,
                Detail = detail,
                Timestamp = DateTimeOffset.UtcNow,
                Version = AuditEvent.CurrentVersion
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[grey](auditoría no persistida: {Markup.Escape(ex.Message)})[/]");
        }
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
                               $"[dim]Rerank:[/] [white]{(s.Rerank ? "on" : "off")}[/]  " +
                               $"[dim]Respuesta:[/] [white]{(s.Technical ? "técnica" : "simple")}[/]  " +
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
