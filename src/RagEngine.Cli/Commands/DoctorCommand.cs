using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Utilities;
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

    // El brain se resuelve de forma perezosa, NO por constructor. Inyectarlo hacía
    // que el contenedor construyera la InferenceSession antes de ejecutar el
    // comando: si faltaba el modelo, el diagnóstico moría con
    // "Could not resolve type 'DoctorCommand'" y un stack trace de 20 líneas,
    // justo en el caso que el doctor existe para diagnosticar.
    private readonly IServiceProvider _services;

    public DoctorCommand(QdrantClient qdrant, IConfiguration config, IServiceProvider services)
    {
        _qdrant = qdrant;
        _config = config;
        _services = services;
    }

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        AnsiConsole.MarkupLine("[bold cyan]Checking RagEngine dependencies...[/]\n");

        var allClear = true;

        // 1. Qdrant Check
        allClear &= await CheckQdrantAsync();

        // 2. ONNX & Tokenizer Check
        allClear &= CheckOnnxModel();

        // 2b. Cross-Encoder re-ranker (optional — warning only)
        CheckCrossEncoderModel();

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
        // La sección define GrpcPort/HttpPort: "Qdrant:Port" no existe, así que
        // esto siempre reportaba el literal 6334 sin importar la configuración.
        var port = _config["Qdrant:GrpcPort"] ?? "6334";
        
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
        // Resuelto con el mismo criterio que el pipeline de opciones, para que el
        // doctor reporte la ruta que de verdad se va a abrir.
        var modelPath = RagEnginePaths.ResolveModelPath(
            _config["OnnxBrain:ModelPath"] ?? "models/paraphrase-multilingual-MiniLM-L12-v2/model.onnx");
        // Antes leía "OnnxBrain:TokenizerPath", clave que no existe en ningún
        // appsettings: el chequeo caía siempre al fallback de otro modelo
        // (all-MiniLM-L6-v2) en vez de mirar el tokenizador que se abre de verdad.
        var tokenizerPath = RagEnginePaths.ResolveModelPath(
            _config["OnnxBrain:VocabPath"] ?? "models/paraphrase-multilingual-MiniLM-L12-v2/sentencepiece.bpe.model");
        
        var modelOk = File.Exists(modelPath);
        var tokenOk = File.Exists(tokenizerPath);

        if (modelOk)
        {
            // Que el archivo exista no garantiza que ONNX pueda abrirlo: un binario
            // de otra arquitectura o corrupto falla aquí. Se reporta la diferencia
            // en vez de dejar que la excepción tumbe el comando.
            var (loaded, detail) = TryDescribeBrain();

            if (loaded)
                AnsiConsole.MarkupLine($"[green]✅ ONNX Model[/]     {modelPath}   [dim](Loaded | {detail})[/]");
            else
                AnsiConsole.MarkupLine($"[red]❌ ONNX Model[/]     {modelPath}   [red](El archivo existe pero no se pudo cargar: {Markup.Escape(detail)})[/]");

            modelOk = loaded;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]❌ ONNX Model[/]     {modelPath}   [red](Not Found — ejecuta 'bash infra/download-model.sh')[/]");
        }

        if (tokenOk)
            AnsiConsole.MarkupLine($"[green]✅ Tokenizer[/]      {tokenizerPath}   [dim](OK)[/]");
        else
            AnsiConsole.MarkupLine($"[red]❌ Tokenizer[/]      {tokenizerPath}   [red](Not Found)[/]");

        return modelOk && tokenOk;
    }

    /// <summary>
    /// Resuelve el brain y describe sus dimensiones, sin dejar escapar la
    /// excepción: el doctor tiene que terminar de imprimir el resto de los
    /// chequeos aunque este falle.
    /// </summary>
    private (bool Loaded, string Detail) TryDescribeBrain()
    {
        try
        {
            var brain = _services.GetRequiredService<IVectorizationBrain>();
            return (true, $"{brain.EmbeddingDimensions} dims");
        }
        catch (Exception ex)
        {
            // Solo el mensaje, no el stack: el doctor es para diagnosticar, no
            // para depurar. La excepción completa ya queda en el log de Serilog.
            var root = ex;
            while (root.InnerException is not null)
            {
                root = root.InnerException;
            }

            return (false, root.Message);
        }
    }

    /// <summary>
    /// El cross-encoder es opcional (solo lo exige --rerank), por lo que su
    /// ausencia se reporta como advertencia y nunca hace fallar el doctor.
    /// </summary>
    private void CheckCrossEncoderModel()
    {
        var modelPath = RagEnginePaths.ResolveModelPath(
            _config["CrossEncoder:ModelPath"] ?? "models/mmarco-mMiniLMv2-L12-H384-v1/model.onnx");
        var vocabPath = RagEnginePaths.ResolveModelPath(
            _config["CrossEncoder:VocabPath"] ?? "models/mmarco-mMiniLMv2-L12-H384-v1/sentencepiece.bpe.model");

        if (File.Exists(modelPath) && File.Exists(vocabPath))
            AnsiConsole.MarkupLine($"[green]✅ Re-Ranker[/]      {modelPath}   [dim](OK — disponible para --rerank)[/]");
        else
            AnsiConsole.MarkupLine($"[yellow]⚠️ Re-Ranker[/]      {modelPath}   " +
                                   "[yellow](Not Found — '--rerank' fallará; ejecuta 'bash infra/download-model.sh reranker')[/]");
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
        catch (Exception ex)
        {
            // Antes devolvía false sin imprimir nada: el doctor bajaba su veredicto
            // a "algo falló" sin decir qué, que es lo contrario de diagnosticar.
            AnsiConsole.MarkupLine($"[red]❌ Colecciones[/]    [red](No se pudieron listar: {Markup.Escape(ex.Message)})[/]");
            return false;
        }
    }
}
