using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RagEngine.Cli.Commands;

/// <summary>
/// Checks system dependencies for the RAG Engine.
///
/// Usage:
///   rag doctor
/// </summary>
public sealed class DoctorCommand : AsyncCommand
{
    private readonly QdrantClient _qdrant;
    private readonly IConfiguration _config;
    private readonly IVectorizationBrain _brain; // We can check if it resolves/loads
    
    public DoctorCommand(QdrantClient qdrant, IConfiguration config, IVectorizationBrain brain)
    {
        _qdrant = qdrant;
        _config = config;
        _brain = brain;
    }

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        AnsiConsole.MarkupLine("[bold cyan]Checking RagEngine dependencies...[/]\n");

        var allClear = true;

        // 1. Qdrant Check
        allClear &= await CheckQdrantAsync();

        // 2. ONNX & Tokenizer Check
        allClear &= CheckOnnxModel();
        
        // 3. Disk Space Check
        allClear &= CheckDiskSpace();

        // 4. Collections Check
        allClear &= await CheckCollectionsAsync();

        AnsiConsole.WriteLine();
        
        if (allClear)
        {
            AnsiConsole.MarkupLine("[bold green]All systems go! Ready for ingestion and search.[/]");
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine("[bold yellow]Some checks failed or raised warnings. Please review the output above.[/]");
            return 1;
        }
    }

    private async Task<bool> CheckQdrantAsync()
    {
        var host = _config["Qdrant:Host"] ?? "localhost";
        var port = _config["Qdrant:Port"] ?? "6334";
        
        try
        {
            // Simple ping to verify connection
            await _qdrant.ListCollectionsAsync(); 
            AnsiConsole.MarkupLine($"[green]✅ Qdrant[/]         {host}:{port}   [dim](Connected)[/]");
            return true;
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine($"[red]❌ Qdrant[/]         {host}:{port}   [red](Connection Failed)[/]");
            return false;
        }
    }

    private bool CheckOnnxModel()
    {
        var modelPath = _config["OnnxBrain:ModelPath"] ?? "models/all-MiniLM-L6-v2/model.onnx";
        var tokenizerPath = _config["OnnxBrain:TokenizerPath"] ?? "models/all-MiniLM-L6-v2/tokenizer.json";
        
        var modelOk = File.Exists(modelPath);
        var tokenOk = File.Exists(tokenizerPath);

        if (modelOk)
            AnsiConsole.MarkupLine($"[green]✅ ONNX Model[/]     {modelPath}   [dim](Loaded | {_brain.EmbeddingDimensions} dims)[/]");
        else
            AnsiConsole.MarkupLine($"[red]❌ ONNX Model[/]     {modelPath}   [red](Not Found)[/]");

        if (tokenOk)
            AnsiConsole.MarkupLine($"[green]✅ Tokenizer[/]      {tokenizerPath}   [dim](OK)[/]");
        else
            AnsiConsole.MarkupLine($"[red]❌ Tokenizer[/]      {tokenizerPath}   [red](Not Found)[/]");

        return modelOk && tokenOk;
    }

    private bool CheckDiskSpace()
    {
        try
        {
            var drive = new DriveInfo(Directory.GetCurrentDirectory());
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            var totalGb = drive.TotalSize / (1024.0 * 1024 * 1024);
            var usedGb = totalGb - freeGb;

            if (freeGb > 5)
            {
                AnsiConsole.MarkupLine($"[green]✅ Disk Space[/]     {drive.Name}   [dim]({usedGb:F1} GB used | {freeGb:F1} GB free)[/]");
                return true;
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]⚠️ Disk Space[/]     {drive.Name}   [yellow](Low Space | {freeGb:F1} GB free)[/]");
                return true; // Still passing but with a warning
            }
        }
        catch
        {
            AnsiConsole.MarkupLine("[dim]   Disk Space     (Unknown)[/]");
            return true;
        }
    }

    private async Task<bool> CheckCollectionsAsync()
    {
        try
        {
            var collections = await _qdrant.ListCollectionsAsync();
            if (collections.Count > 0)
            {
                AnsiConsole.MarkupLine($"[green]✅ Colecciones[/]    {collections.Count} colecciones encontradas.");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]⚠️ Colecciones[/]    0 colecciones encontradas → Ejecuta 'rag ingest'");
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
