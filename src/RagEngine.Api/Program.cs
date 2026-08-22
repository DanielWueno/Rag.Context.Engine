using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using RagEngine.Api;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Extensions;
using RagEngine.Core.Services.Generation;
using RagEngine.Core.Services.Summary;
using RagEngine.Core.Utilities;
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

// La ruta se ancla a la raíz del repo (o a RAG_LOGS_DIR), no al directorio de
// trabajo: con la ruta relativa anterior, `dotnet run --project src/RagEngine.Api`
// escribía los logs DENTRO del árbol de código.
//
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
        path: Path.Combine(RagEnginePaths.ResolveLogsDirectory(), "rag-api-.json"),
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
    // no solo localhost.
    //
    // El UseUrls fijo que habia aqui ANULABA a ASPNETCORE_URLS, al contrario de lo
    // que decia su propio comentario: con el puerto ocupado, la unica salida era
    // editar el codigo. Eso bloqueaba levantar una segunda instancia con otra
    // configuracion, que es como se comparan dos variantes de generacion sin
    // apagar la que esta sirviendo. Ahora el default solo se aplica si nadie dijo
    // otra cosa.
    if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]) &&
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    {
        builder.WebHost.UseUrls("http://0.0.0.0:5080");
    }

    var app = builder.Build();

    // Fase 1 del modo Simple (docs/analisis-futuro/modo-respuesta-simple-codigo.md)
    // agrega un filtro determinístico + buffer para ResponseMode.Simple, con una
    // válvula de escape de rollback sin rebuild. Si alguien la apaga vía config/env
    // var, debe quedar bien visible en el arranque — no algo que se descubra
    // semanas después de un incidente puntual.
    if (!app.Services.GetRequiredService<IOptionsMonitor<RagGenerationOptions>>().CurrentValue.EnableSimpleModeSanitizer)
    {
        app.Services.GetRequiredService<ILogger<Program>>().LogWarning(
            "[RAG] EnableSimpleModeSanitizer=false — ResponseMode.Simple está transmitiendo sin " +
            "el filtro post-generación de Fase 1 (streaming crudo, sin buffer). Revisar " +
            "docs/analisis-futuro/modo-respuesta-simple-codigo.md antes de dejarlo así por mucho tiempo.");
    }

    app.UseDefaultFiles();
    app.UseStaticFiles();

    var defaultCollection = builder.Configuration["Qdrant:DefaultCollection"] ?? "default";

    // /api/ask and /api/ask/stream retrieve sources independently from the
    // RagGenerationService call that actually answers the question, so the two
    // can disagree: a meta-question skips retrieval entirely inside the service,
    // and a low-confidence top score makes it cut before generating — in both
    // cases the retrieved chunks played no role in the answer shown to the user,
    // so attaching them as "sources" would be misleading. Mirrors the same
    // decision RagGenerationService.AskStreamingAsync makes internally, using the
    // same IMetaIntentDetector and the same RagGenerationOptions thresholds.
    // isMetaIntent is passed in already computed — it requires an embedder call,
    // so callers compute it once per request rather than re-running it here.
    static bool ShouldSuppressSources(
        bool isMetaIntent,
        bool rerank,
        IReadOnlyList<RetrievalResult> results,
        RagGenerationOptions ragOptions) =>
        isMetaIntent ||
        (rerank && results.Count > 0 && results[0].SimilarityScore < ragOptions.LowConfidenceThreshold);

    // El resumen de negocio ya se generó y cacheó en ingesta (Fase 2, opt-in por colección
    // vía --con-resumen) para producir el vector dense-resumen — acá se reusa como campo de
    // fuente, sin generar nada nuevo. Miss de caché (colección sin resumen, o chunk que cayó
    // en el sentinel SIN_CONTENIDO_DE_NEGOCIO) simplemente deja Resumen en null.
    //
    // responseMode decide la forma del DTO, no solo su contenido: en Simple, un lector no
    // técnico no puede distinguir si un fragmento de código crudo ES la respuesta, una cita,
    // o un error — así que File/Section/StartLine/EndLine/Content se omiten por completo
    // (SourceDto.Redacted), dejando solo Score y, si existe, el Resumen ya en lenguaje de
    // negocio. /api/search (el botón "Buscar", una herramienta explícita de power-user) no
    // pasa por acá con Simple — solo /api/ask y /api/ask/stream respetan el toggle.
    static async Task<List<SourceDto>> BuildSourcesAsync(
        IReadOnlyList<RetrievalResult> results,
        SummaryCache summaryCache,
        ResponseMode responseMode,
        CancellationToken cancellationToken)
    {
        var resumenes = await Task.WhenAll(
            results.Select(r => summaryCache.TryGetAsync(r.ContentHash, cancellationToken)));
        return results.Zip(resumenes, (r, hit) =>
                responseMode != ResponseMode.Simple    ? SourceDto.From(r, hit.Summary)
                : r.Metadata.Language.IsProse()        ? SourceDto.ForProse(r, hit.Summary)
                :                                        SourceDto.Redacted(r, hit.Summary))
            .ToList();
    }

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
        SummaryCache summaryCache,
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

        // /api/search es la herramienta "Buscar" del power-user — siempre trae el fragmento
        // crudo con file/líneas, sin importar el toggle de respuesta simple/técnica del chat.
        var sources = await BuildSourcesAsync(results, summaryCache, ResponseMode.Technical, cancellationToken);

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
        IOptions<RagGenerationOptions> ragOptions,
        IMetaIntentDetector metaIntentDetector,
        SummaryCache summaryCache,
        ILogger<Program> queryLogger,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "El campo 'query' es obligatorio." });

        var collection = request.Collection ?? defaultCollection;
        var topK = request.TopK ?? 10;
        var minScore = request.MinScore ?? 0.10f;
        var rerank = request.Rerank ?? true;
        var responseMode = request.ResponseMode.ParseResponseMode();
        var isMetaIntent = await metaIntentDetector.IsMetaIntentAsync(request.Query, cancellationToken);

        var stopwatch = Stopwatch.StartNew();

        // Dos llamadas de retrieval independientes (una aquí para citar fuentes, otra
        // dentro de AskStreamingAsync para armar el contexto del LLM): es la forma más
        // simple de exponer "sources" sin tocar el contrato público de
        // IRagGenerationService. El costo extra es insignificante en una colección de
        // documentación de este tamaño. Se omite por completo para meta-preguntas: la
        // respuesta ni siquiera va a usar el contexto recuperado.
        var sourcesTask = isMetaIntent
            ? Task.FromResult<IReadOnlyList<RetrievalResult>>([])
            : retriever.SearchAsync(
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

        // Medido por separado del stopwatch total del request (que también cubre el
        // retrieval de "sources" en paralelo) para poder comparar la latencia real de
        // Simple (buferea toda la respuesta) contra Technical (streaming) — ver Fase 1
        // punto 4 de docs/analisis-futuro/modo-respuesta-simple-codigo.md.
        var generationStopwatch = Stopwatch.StartNew();
        var answer = new StringBuilder();
        await foreach (var fragment in generation.AskStreamingAsync(
            request.Query, collection, topK, minScore, rerank, responseMode, history,
            onStatus: null, cancellationToken: cancellationToken))
        {
            answer.Append(fragment);
        }
        generationStopwatch.Stop();

        var retrievedSources = await sourcesTask;
        var answerText = answer.ToString();
        var sources = ShouldSuppressSources(isMetaIntent, rerank, retrievedSources, ragOptions.Value)
                || answerText.Trim() == RagGenerationService.NoContextFallbackMessage
            ? []
            : await BuildSourcesAsync(retrievedSources, summaryCache, responseMode, cancellationToken);
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
                ResponseMode = responseMode,
                HistoryTurns = history?.Count ?? 0,
                DurationMs = stopwatch.ElapsedMilliseconds,
                GenerationDurationMs = generationStopwatch.ElapsedMilliseconds,
                Answer = answerText,
                Sources = sources.Select(s => new { s.File, s.Section, s.StartLine, s.EndLine, s.Score })
            });

        return Results.Ok(new RagAskResponse(answerText, sources));
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
        IOptions<RagGenerationOptions> ragOptions,
        IMetaIntentDetector metaIntentDetector,
        SummaryCache summaryCache,
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
        var responseMode = request.ResponseMode.ParseResponseMode();
        var isMetaIntent = await metaIntentDetector.IsMetaIntentAsync(request.Query, cancellationToken);

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

        // Meta-preguntas se saltan retrieval por completo: la respuesta de
        // AskStreamingAsync ni siquiera va a mirar el contexto recuperado.
        var results = isMetaIntent
            ? (IReadOnlyList<RetrievalResult>)[]
            : await retriever.SearchAsync(
                request.Query,
                new RetrievalOptions
                {
                    CollectionName = collection,
                    TopK = topK,
                    MinimumSimilarityScore = minScore,
                    UseReRanking = rerank
                },
                cancellationToken);

        // Si el score del top-1 va a hacer que AskStreamingAsync corte antes de
        // generar (banda baja) o responda con el bloque fijo de meta-pregunta, los
        // chunks recuperados no jugaron ningún papel en la respuesta — mostrarlos
        // como "fuentes" confundiría al usuario. Mismo criterio que usa el gate
        // interno del servicio de generación.
        var sources = ShouldSuppressSources(isMetaIntent, rerank, results, ragOptions.Value)
            ? []
            : await BuildSourcesAsync(results, summaryCache, responseMode, cancellationToken);

        // El front pinta estas tarjetas de inmediato (con su fragmento) y usa
        // los primeros títulos como el "extracto de contexto" del status —
        // esto ya es lo que se recuperó, no una simulación.
        await SendAsync("sources", new { sources });
        await SendAsync("status", new { message = sources.Count > 0
            ? $"Generando respuesta a partir de {sources.Count} fragmentos..."
            : "Generando respuesta..." });

        // Sin estado de sesión en el servidor: el cliente reenvía la transcripción
        // completa en cada request. El retrieval de arriba solo usa `request.Query`
        // (el turno actual) — el historial se inyecta al LLM para dar continuidad
        // conversacional, no se vuelve a buscar en Qdrant.
        var history = request.History?.Select(t => t.ToDomain()).ToList();

        // Medido por separado del stopwatch total del request, igual que en /api/ask,
        // para poder comparar la latencia real de Simple (buferea toda la respuesta,
        // un solo evento "token" al final) contra Technical (streaming token-a-token)
        // — ver Fase 1 punto 4 de docs/analisis-futuro/modo-respuesta-simple-codigo.md.
        var generationStopwatch = Stopwatch.StartNew();
        var answer = new StringBuilder();
        await foreach (var fragment in generation.AskStreamingAsync(
            request.Query, collection, topK, minScore, rerank, responseMode, history,
            onStatus: async (message, ct) => await SendAsync("status", new { message }),
            cancellationToken: cancellationToken))
        {
            answer.Append(fragment);
            await SendAsync("token", new { text = fragment });
        }
        generationStopwatch.Stop();

        // El gate no cortó (banda media/alta), pero el LLM decidió por su cuenta,
        // siguiendo la regla 2 del prompt, que ninguno de los chunks recuperados
        // servía — mismo criterio de "no mostrar fuentes que no se usaron", pero
        // solo se sabe hasta después de generar. El "sources" ya enviado antes
        // (para dar progreso temprano) se corrige con un segundo evento vacío.
        if (sources.Count > 0 && answer.ToString().Trim() == RagGenerationService.NoContextFallbackMessage)
        {
            sources = [];
            await SendAsync("sources", new { sources });
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
                ResponseMode = responseMode,
                HistoryTurns = history?.Count ?? 0,
                DurationMs = stopwatch.ElapsedMilliseconds,
                GenerationDurationMs = generationStopwatch.ElapsedMilliseconds,
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
    // Deliberadamente NO se llama a OnnxRuntimeLifetime.Shutdown() aquí: medido el
    // 2026-08-21, la API ya cierra con exit 0 y sin el aborto de SIGABRT que sí
    // sufría el CLI, así que no hay problema que arreglar. Hipótesis a confirmar
    // (ítem 1.13 del ledger): este host no destruye el contenedor de DI al apagarse,
    // de modo que la InferenceSession sobrevive al entorno global y nunca se da la
    // combinación que aborta. Si algún día se añade esa destrucción, habrá que
    // llamar a Shutdown() aquí también.
    Log.CloseAndFlush();
}
