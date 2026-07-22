using System.Diagnostics;
using System.Text;
using RagEngine.Api;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Extensions;
using Serilog;
using Serilog.Formatting.Compact;

// ──────────────────────────────────────────────────────────────────────────────
// RagEngine API — piloto de acceso en red al motor RAG ya existente.
//
// Reutiliza exactamente los mismos servicios que consume el CLI
// (AddRagEngineCore / AddRagEngineGeneration ya están diseñados para esto —
// ver el comentario en ServiceCollectionExtensions.AddRagEngineCore).
// No hay lógica de retrieval/generación nueva aquí: este proyecto es solo
// el "host" HTTP delgado sobre lo que ya corre en el CLI.
//
// Pensado para LAN/VPN interna, no para exponerse a internet público — el
// contenido ingestado puede incluir reglas de negocio internas.
// ──────────────────────────────────────────────────────────────────────────────

// Cada consulta (pregunta + respuesta + fuentes citadas) queda en logs/rag-api-*.json
// como una línea JSON — es lo que permite revisar después qué preguntó el equipo
// durante el piloto y si las respuestas fueron buenas, sin depender de `docker logs`
// (efímero y sin estructura). Mismo patrón que ya usa RagEngine.Cli.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning)
    .WriteTo.File(
        formatter: new CompactJsonFormatter(),
        path: "logs/rag-api-.json",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(dispose: true);

    builder.Services.AddRagEngineCore(builder.Configuration);
    builder.Services.AddRagEngineGeneration(builder.Configuration);

    // Escucha en todas las interfaces para que el equipo pueda alcanzarlo por LAN,
    // no solo localhost. Puerto configurable vía ASPNETCORE_URLS si 5080 choca.
    builder.WebHost.UseUrls("http://0.0.0.0:5080");

    var app = builder.Build();

    app.UseDefaultFiles();
    app.UseStaticFiles();

    var defaultCollection = builder.Configuration["Qdrant:DefaultCollection"] ?? "default";

    app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

    // Alimenta el selector de colección de la página — así el equipo no
    // depende de que quede fija a un proyecto (hoy innovapp-docs, mañana
    // podría ser cualquier otra colección ingestada).
    app.MapGet("/api/collections", async (
        Qdrant.Client.QdrantClient qdrant,
        CancellationToken cancellationToken) =>
    {
        var collections = await qdrant.ListCollectionsAsync(cancellationToken);
        return Results.Ok(new { collections, @default = defaultCollection });
    });

    app.MapPost("/api/search", async (
        RagQueryRequest request,
        ISemanticRetriever retriever,
        ILogger<Program> queryLogger,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "El campo 'query' es obligatorio." });

        var collection = request.Collection ?? defaultCollection;
        var topK = request.TopK ?? 10;
        var minScore = request.MinScore ?? 0.10f;
        var rerank = request.Rerank ?? true;

        var stopwatch = Stopwatch.StartNew();
        var results = await retriever.SearchAsync(
            request.Query,
            new RetrievalOptions
            {
                CollectionName = collection,
                TopK = topK,
                MinimumSimilarityScore = minScore,
                UseReRanking = rerank
            },
            cancellationToken);
        stopwatch.Stop();

        var sources = results.Select(SourceDto.From).ToList();

        queryLogger.LogInformation(
            "QueryEvent {@Entry}",
            new
            {
                Type = "search",
                Collection = collection,
                request.Query,
                TopK = topK,
                MinScore = minScore,
                Rerank = rerank,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ResultCount = sources.Count,
                Sources = sources.Select(s => new { s.File, s.Section, s.StartLine, s.EndLine, s.Score })
            });

        return Results.Ok(sources);
    });

    app.MapPost("/api/ask", async (
        RagQueryRequest request,
        ISemanticRetriever retriever,
        IRagGenerationService generation,
        ILogger<Program> queryLogger,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "El campo 'query' es obligatorio." });

        var collection = request.Collection ?? defaultCollection;
        var topK = request.TopK ?? 10;
        var minScore = request.MinScore ?? 0.10f;
        var rerank = request.Rerank ?? true;

        var stopwatch = Stopwatch.StartNew();

        // Dos llamadas de retrieval independientes (una aquí para citar fuentes, otra
        // dentro de AskStreamingAsync para armar el contexto del LLM): es la forma más
        // simple de exponer "sources" sin tocar el contrato público de
        // IRagGenerationService. El costo extra es insignificante en una colección de
        // documentación de este tamaño.
        var sourcesTask = retriever.SearchAsync(
            request.Query,
            new RetrievalOptions
            {
                CollectionName = collection,
                TopK = topK,
                MinimumSimilarityScore = minScore,
                UseReRanking = rerank
            },
            cancellationToken);

        var history = request.History?.Select(t => t.ToDomain()).ToList();

        var answer = new StringBuilder();
        await foreach (var fragment in generation.AskStreamingAsync(
            request.Query, collection, topK, minScore, rerank, history, cancellationToken))
        {
            answer.Append(fragment);
        }

        var sources = (await sourcesTask).Select(SourceDto.From).ToList();
        stopwatch.Stop();

        queryLogger.LogInformation(
            "QueryEvent {@Entry}",
            new
            {
                Type = "ask",
                Collection = collection,
                request.Query,
                TopK = topK,
                MinScore = minScore,
                Rerank = rerank,
                HistoryTurns = history?.Count ?? 0,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Answer = answer.ToString(),
                Sources = sources.Select(s => new { s.File, s.Section, s.StartLine, s.EndLine, s.Score })
            });

        return Results.Ok(new RagAskResponse(answer.ToString(), sources));
    });

    // Variante SSE de /api/ask para la página web: en vez de bloquear hasta
    // tener la respuesta completa, va emitiendo eventos según avanza el
    // pipeline real (retrieval → fuentes encontradas → tokens de la
    // generación) para que el front pueda mostrar progreso genuino en vez de
    // un mensaje fijo de "espera".
    app.MapPost("/api/ask/stream", async (
        RagQueryRequest request,
        HttpContext http,
        ISemanticRetriever retriever,
        IRagGenerationService generation,
        ILogger<Program> queryLogger,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "El campo 'query' es obligatorio." }, cancellationToken);
            return;
        }

        var collection = request.Collection ?? defaultCollection;
        var topK = request.TopK ?? 10;
        var minScore = request.MinScore ?? 0.10f;
        var rerank = request.Rerank ?? true;

        http.Response.Headers.CacheControl = "no-cache";
        http.Response.ContentType = "text/event-stream";

        async Task SendAsync(string eventName, object payload)
        {
            // JsonSerializerOptions.Web replica el camelCase que Results.Ok() ya
            // aplica en /api/search y /api/ask — sin esto, el front recibiría
            // "File"/"Section" en vez de "file"/"section" y no matchearía.
            var json = System.Text.Json.JsonSerializer.Serialize(
                payload, System.Text.Json.JsonSerializerOptions.Web);
            await http.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", cancellationToken);
            await http.Response.Body.FlushAsync(cancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();

        await SendAsync("status", new { message = "Buscando en la documentación..." });

        var results = await retriever.SearchAsync(
            request.Query,
            new RetrievalOptions
            {
                CollectionName = collection,
                TopK = topK,
                MinimumSimilarityScore = minScore,
                UseReRanking = rerank
            },
            cancellationToken);

        var sources = results.Select(SourceDto.From).ToList();

        // El front pinta estas tarjetas de inmediato (con su fragmento) y usa
        // los primeros títulos como el "extracto de contexto" del status —
        // esto ya es lo que se recuperó, no una simulación.
        await SendAsync("sources", new { sources });
        await SendAsync("status", new { message = $"Generando respuesta a partir de {sources.Count} fragmentos..." });

        // Sin estado de sesión en el servidor: el cliente reenvía la transcripción
        // completa en cada request. El retrieval de arriba solo usa `request.Query`
        // (el turno actual) — el historial se inyecta al LLM para dar continuidad
        // conversacional, no se vuelve a buscar en Qdrant.
        var history = request.History?.Select(t => t.ToDomain()).ToList();

        var answer = new StringBuilder();
        await foreach (var fragment in generation.AskStreamingAsync(
            request.Query, collection, topK, minScore, rerank, history, cancellationToken))
        {
            answer.Append(fragment);
            await SendAsync("token", new { text = fragment });
        }

        stopwatch.Stop();

        queryLogger.LogInformation(
            "QueryEvent {@Entry}",
            new
            {
                Type = "ask",
                Transport = "sse",
                Collection = collection,
                request.Query,
                TopK = topK,
                MinScore = minScore,
                Rerank = rerank,
                HistoryTurns = history?.Count ?? 0,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Answer = answer.ToString(),
                Sources = sources.Select(s => new { s.File, s.Section, s.StartLine, s.EndLine, s.Score })
            });

        await SendAsync("done", new { });
    });

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "El host terminó inesperadamente debido a una excepción fatal.");
}
finally
{
    Log.CloseAndFlush();
}
