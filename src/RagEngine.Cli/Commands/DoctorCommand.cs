using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Infrastructure.VectorStore;
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
        // La dimensión del brain alimenta el chequeo de colecciones: un 'dense' de otro
        // tamaño es tan inconsultable como uno ausente, y sin este dato no se detecta.
        allClear &= CheckOnnxModel(out var embeddingDimensions);

        // 2b. Cross-Encoder re-ranker (optional — warning only)
        CheckCrossEncoderModel();

        // 3. Disk Space Check
        allClear &= CheckDiskSpace();

        // 4. Collections Check
        allClear &= await CheckCollectionsAsync(embeddingDimensions);

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

    private bool CheckOnnxModel(out int? embeddingDimensions)
    {
        embeddingDimensions = null;

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
            var (loaded, dims, detail) = TryDescribeBrain();
            embeddingDimensions = dims;

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
    private (bool Loaded, int? Dimensions, string Detail) TryDescribeBrain()
    {
        try
        {
            var brain = _services.GetRequiredService<IVectorizationBrain>();
            return (true, brain.EmbeddingDimensions, $"{brain.EmbeddingDimensions} dims");
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

            return (false, null, root.Message);
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

    /// <summary>
    /// Contar colecciones no diagnostica nada: una colección creada por una versión anterior
    /// del motor se lista como sana, con puntos y en verde, y sólo revienta cuando alguien la
    /// consulta ("Not existing vector name error: dense"). Este chequeo le mira el esquema a
    /// cada una y NOMBRA a la que está rota, que es lo que el ítem pide.
    /// </summary>
    private async Task<bool> CheckCollectionsAsync(int? embeddingDimensions)
    {
        IReadOnlyList<CollectionSchemaReport> reports;
        try
        {
            var store = _services.GetRequiredService<QdrantVectorStore>();
            reports = await store.InspectCollectionSchemasAsync(embeddingDimensions);
        }
        catch (Exception ex)
        {
            // Antes devolvía false sin imprimir nada: el doctor bajaba su veredicto
            // a "algo falló" sin decir qué, que es lo contrario de diagnosticar.
            AnsiConsole.MarkupLine($"[red]❌ Colecciones[/]    [red](No se pudieron listar: {Markup.Escape(ex.Message)})[/]");
            return false;
        }

        if (reports.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]⚠️ Colecciones[/]    0 colecciones encontradas → Ejecuta 'rag ingest'");
            return true;
        }

        var broken = reports.Where(r => r.Status == CollectionSchemaStatus.Incompatible).ToList();
        var legacy = reports.Where(r => r.Status == CollectionSchemaStatus.Legacy).ToList();

        if (broken.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[green]✅ Colecciones[/]    {reports.Count} colecciones, todas consultables" +
                (legacy.Count > 0 ? $"   [dim]({legacy.Count} sin vector de resumen)[/]" : ""));
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[red]❌ Colecciones[/]    {broken.Count} de {reports.Count} con esquema incompatible: " +
                $"[red]{Markup.Escape(string.Join(", ", broken.Select(b => b.Name)))}[/]");
        }

        // El detalle va siempre, no sólo cuando falla: saber que una colección busca en 2
        // bandas y no en 3 explica diferencias de recall que si no parecen ruido.
        if (embeddingDimensions is null)
        {
            AnsiConsole.MarkupLine(
                "[dim]   (el modelo no cargó: no se puede verificar que la dimensión de 'dense' coincida)[/]");
        }

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Colección");
        table.AddColumn(new TableColumn("Puntos").RightAligned());
        table.AddColumn("Esquema");
        table.AddColumn("Detalle");

        foreach (var r in reports)
        {
            var (icon, color) = r.Status switch
            {
                CollectionSchemaStatus.Current => ("✅", "green"),
                CollectionSchemaStatus.Legacy => ("⚠️", "yellow"),
                _ => ("❌", "red")
            };

            var detail = r.Problems.Concat(r.Notes).ToList();
            if (r.Remedy is not null)
            {
                detail.Add($"→ {r.Remedy}");
            }

            table.AddRow(
                new Markup($"[{color}]{icon} {Markup.Escape(r.Name)}[/]"),
                new Markup($"[dim]{r.PointsCount}[/]"),
                new Markup($"[{color}]{r.Status}[/]"),
                new Markup(Markup.Escape(detail.Count == 0 ? "—" : string.Join("\n", detail))));
        }

        AnsiConsole.Write(table);

        // Una colección rota hace fallar el doctor: es exactamente el fallo que este ítem
        // quiere adelantar a la consulta. Las 'Legacy' no, porque son consultables.
        return broken.Count == 0;
    }
}
