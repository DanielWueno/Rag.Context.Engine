using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RagEngine.Cli.Commands;

/// <summary>
/// Purga eventos de auditoría vencidos (ítem 12.9). Acción explícita del operador —
/// nunca se dispara automáticamente al arrancar la API/CLI, para que borrar historia
/// auditable sea siempre una decisión consciente, no un efecto lateral de configurar
/// un plazo. Respeta <see cref="AuditEvent.LegalHold"/>: un evento retenido nunca se
/// borra aquí, sin importar cuán vencido esté (ver <c>AuditHoldCommand</c> para
/// activar/desactivar la retención).
///
/// Usage:
///   rag audit purge --older-than-days 90
///   rag audit purge --dry-run
/// </summary>
public sealed class AuditPurgeCommand : Command<AuditPurgeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--older-than-days")]
        [System.ComponentModel.Description(
            "Plazo en días: se purgan los eventos con Timestamp anterior a (ahora - N días). " +
            "Sin este valor, se usa Audit:RetentionDays de la configuración; si tampoco está " +
            "configurado, el comando falla en vez de asumir un plazo arbitrario.")]
        public int? OlderThanDays { get; init; }

        [CommandOption("--dry-run")]
        [System.ComponentModel.Description("Muestra cuántos eventos SERÍAN purgados sin borrar nada.")]
        public bool DryRun { get; init; }
    }

    private readonly IAuditEventStore _auditStore;
    private readonly AuditOptions _auditOptions;

    public AuditPurgeCommand(IAuditEventStore auditStore, IOptions<AuditOptions> auditOptions)
    {
        _auditStore = auditStore;
        _auditOptions = auditOptions.Value;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var retentionDays = settings.OlderThanDays ?? _auditOptions.RetentionDays;
        if (retentionDays is null)
        {
            AnsiConsole.MarkupLine(
                "[red]✗ No hay plazo de retención.[/] Pasa [bold]--older-than-days N[/] o " +
                "configura [bold]Audit:RetentionDays[/] en appsettings.json.");
            return 1;
        }
        if (retentionDays < 0)
        {
            AnsiConsole.MarkupLine("[red]✗ --older-than-days no puede ser negativo.[/]");
            return 1;
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-retentionDays.Value);

        if (settings.DryRun)
        {
            var events = await _auditStore.ListAsync();
            var candidates = events.Count(e => e.Timestamp < cutoff && !e.LegalHold);
            var held = events.Count(e => e.Timestamp < cutoff && e.LegalHold);
            AnsiConsole.MarkupLine(
                $"[yellow]Dry-run:[/] {candidates} evento(s) anteriores a {cutoff:O} serían purgados " +
                $"({held} más quedan protegidos por retención legal).");
            return 0;
        }

        var purged = await _auditStore.PurgeExpiredAsync(cutoff);
        AnsiConsole.MarkupLine($"[green]✓ {purged} evento(s) de auditoría purgados[/] (anteriores a {cutoff:O}).");
        return 0;
    }
}
