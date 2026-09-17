using RagEngine.Core.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RagEngine.Cli.Commands;

/// <summary>
/// Activa o libera la retención legal de un evento de auditoría (ítem 12.9). Mientras
/// esté activa, <c>rag audit purge</c> nunca borra ese evento, sin importar cuán
/// vencido esté su plazo operativo.
///
/// Usage:
///   rag audit hold evt-1234
///   rag audit hold evt-1234 --release
/// </summary>
public sealed class AuditHoldCommand : Command<AuditHoldCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<EVENT_ID>")]
        public string EventId { get; init; } = string.Empty;

        [CommandOption("--release")]
        [System.ComponentModel.Description("Libera la retención legal en vez de activarla.")]
        public bool Release { get; init; }
    }

    private readonly IAuditEventStore _auditStore;

    public AuditHoldCommand(IAuditEventStore auditStore)
    {
        _auditStore = auditStore;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        await _auditStore.SetLegalHoldAsync(settings.EventId, legalHold: !settings.Release);
        AnsiConsole.MarkupLine(settings.Release
            ? $"[green]✓ Retención legal liberada[/] para {Markup.Escape(settings.EventId)}."
            : $"[green]✓ Retención legal activada[/] para {Markup.Escape(settings.EventId)}.");
        return 0;
    }
}
