using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RagEngine.Api;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Diagnostics;
using RagEngine.Core.Domain;
using RagEngine.Core.Extensions;
using RagEngine.Core.Infrastructure.Vectorization;
using RagEngine.Core.Infrastructure.VectorStore;
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

    // Listener de métricas para detectar los instrumentos del Meter Rag.Context.Engine
    builder.Services.AddHostedService<MetricsListener>();

    // Exportador simple de métricas para verificar que los instrumentos funcionan
    builder.Services.AddSingleton(sp =>
        new SimpleMetricsExporter(sp.GetRequiredService<ILogger<SimpleMetricsExporter>>()));

    // AddProblemDetails() habilita el relleno automático de ProblemDetails que ya
    // hace el propio binding de minimal API cuando el body no parsea como JSON
    // (RequestDelegateFactory captura el JsonException y, si encuentra
    // IProblemDetailsService registrado, lo usa en vez de devolver un 400 vacío).
    // El exception handler de abajo lo reusa para las excepciones no capturadas.
    builder.Services.AddProblemDetails();

    // Cliente aparte del que arma GenerationServiceExtensions para el Kernel de SK:
    // ese HttpClient apunta a Ollama con el timeout largo de generación
    // (Ollama:TimeoutSeconds, hasta 120s) y no se expone como servicio. El chequeo de
    // salud necesita un timeout corto propio — no tiene sentido que /api/health cuelgue
    // 120s solo porque Ollama está caído.
    builder.Services.AddHttpClient();

    // "Cors:AllowedOrigins" con default vacío: sin nadie configurado, ningún origen
    // cross-site recibe los headers Access-Control-Allow-*. Este host es para LAN/VPN
    // interna (ver cabecera del archivo) — no hay cliente en producción hoy que lo
    // necesite (ver ledger, ítem de rollback de Fase 2), así que "restrictivo por
    // defecto" no le quita nada a nadie todavía.
    const string ApiCorsPolicy = "ApiCorsPolicy";
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(options => options.AddPolicy(ApiCorsPolicy, policy =>
    {
        if (allowedOrigins.Length > 0)
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
    }));

    // Límite conservador de tamaño de body: este host solo recibe queries de texto
    // (RagQueryRequest) — 1 MB deja margen de sobra para historial de chat largo sin
    // dejar que un request arbitrariamente grande consuma memoria sin límite.
    const long MaxRequestBodyBytes = 1 * 1024 * 1024;
    builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxRequestBodyBytes);

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

    // Traduce cualquier excepción no capturada por un endpoint a ProblemDetails
    // (application/problem+json, status 500) en vez del 500 con detalle de
    // desarrollador (HTML o texto plano) que da UseDeveloperExceptionPage o el
    // handler por defecto sin esto. IProblemDetailsService ya sabe rellenar
    // type/title/status — solo hace falta apuntarle el código de estado.
    app.UseExceptionHandler(exceptionHandlerApp => exceptionHandlerApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        context.RequestServices.GetRequiredService<ILogger<Program>>()
            .LogError(feature?.Error, "Excepción no manejada en {Path}", feature?.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Ocurrió un error interno procesando la solicitud."
            }
        });
    }));

    // El binding automático de minimal API (JSON malformado, o un campo que no
    // castea al tipo esperado) ya deja el response en 400 sin cuerpo — no lanza una
    // excepción que UseExceptionHandler pueda interceptar. UseStatusCodePages() es lo
    // que, con AddProblemDetails() registrado arriba, rellena ese response vacío con
    // el cuerpo application/problem+json en vez de dejarlo en Content-Length: 0.
    app.UseStatusCodePages();

    app.UseCors(ApiCorsPolicy);

    app.UseDefaultFiles();
    app.UseStaticFiles();

    var defaultCollection = builder.Configuration["Qdrant:DefaultCollection"] ?? "default";

    static CollectionIdentity? ReadCollectionIdentity(HttpContext http)
    {
        // Sólo claims de una identidad autenticada por el host. No se interpretan
        // headers/token crudos como identidad ni se mezclan privilegios de identidades.
        var identities = http.User.Identities.Where(identity => identity.IsAuthenticated).ToArray();
        if (identities.Length != 1)
            return null;

        var identity = identities[0];
        var tenants = identity.FindAll("tenant").Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (tenants.Length > 1)
            return null;

        return new CollectionIdentity
        {
            IsAuthenticated = true,
            Actor = new CollectionActor
            {
                // RoleClaimType permite el mapeo del host; "admin" es sensible a mayúsculas.
                IsAdministrator = identity.HasClaim(ClaimTypes.Role, "admin") ||
                    identity.HasClaim(identity.RoleClaimType, "admin"),
                // "scope" admite claims repetidos (arrays mapeados por el host) y valores
                // separados por espacios; "tenant" es un identificador único opcional.
                Scopes = identity.FindAll("scope")
                    .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Distinct(StringComparer.Ordinal).ToArray(),
                Tenant = tenants.SingleOrDefault()
            }
        };
    }

    /// <summary>
    /// Autoriza la colección y devuelve el manifiesto ya leído (o null si nunca se
    /// publicó uno) para que el llamador pueda resolver el perfil de recuperación
    /// (ítem 7.a) sin repetir el round-trip a Qdrant que ya hizo esta función.
    /// </summary>
    static async Task<(IResult? Denied, CollectionManifest? Manifest)> AuthorizeCollectionAsync(
        string collection,
        HttpContext http,
        QdrantVectorStore store,
        ICollectionActorResolver actorResolver,
        ICollectionAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        // Manifiesto ausente equivale a no publicado: sólo administrador, no acceso público.
        var manifest = await store.GetManifestAsync(collection, cancellationToken);
        // La lectura de HttpContext.User es perezosa: Local nunca toca la fuente de identidad.
        var actor = actorResolver.Resolve(() => ReadCollectionIdentity(http));
        var denied = authorization.Authorize(manifest, actor)
            ? null
            : Results.Problem(
                title: "No tiene autorización para leer esta colección.",
                statusCode: StatusCodes.Status403Forbidden);
        return (denied, manifest);
    }

    /// <summary>
    /// Ítem 7.a: resuelve los defaults efectivos de topK/minScore para una request.
    /// El valor explícito del cliente SIEMPRE gana; en su ausencia, el perfil de la
    /// colección (si hay uno declarado y existe en el catálogo) decide; sin perfil
    /// resuelto, topK/minScore caen exactamente en los literales 10/0.10f que ya
    /// usaba este endpoint antes de ese ítem — ese baseline queda intacto.
    ///
    /// Ítem 7.c: rerank YA NO tiene un default silencioso a <c>true</c>. Sin valor
    /// explícito del cliente ni perfil que lo declare, el resultado es <c>false</c>
    /// — quien quiere pagar el costo del cross-encoder lo pide por request o lo
    /// publica en el perfil de la colección; no vuelve a ser un efecto lateral
    /// invisible del endpoint.
    /// </summary>
    static (int TopK, float MinScore, bool Rerank, PromptFamily? PromptFamily) ResolveEffectiveRetrievalDefaults(
        RagQueryRequest request, RetrievalProfile? profile) => (
        request.TopK ?? profile?.TopK ?? 10,
        request.MinScore ?? profile?.MinScore ?? 0.10f,
        request.Rerank ?? profile?.UseReRanking ?? false,
        profile?.PromptFamily);

    // /api/ask and /api/ask/stream retrieve sources independently from the
    // RagGenerationService call that actually answers the question, so the two
    // can disagree: a meta-question skips retrieval entirely inside the service,
    // and a low-confidence top score makes it cut before generating — in both
    // cases the retrieved chunks played no role in the answer shown to the user,
    // so attaching them as "sources" would be misleading.
    //
    // Ítem 4.7: esto solía RECALCULAR la banda a mano
    // (`results[0].SimilarityScore < ragOptions.LowConfidenceThreshold`), una copia
    // que se quedaba atrás en silencio cada vez que 4.2/4.3 recalibraban esos
    // umbrales o encendían CrossEncoder:StableGateScore. Ahora delega en el mismo
    // ConfidenceGate que usa RagGenerationService.AskStreamingAsync internamente
    // (ver el comentario de esa clase) — un solo sitio decide la banda, la API
    // sólo lee el veredicto. isMetaIntent se pasa ya calculado: requiere una
    // llamada al embedder, así que los llamadores lo calculan una vez por request
    // en vez de repetirlo aquí; el gate no cubre meta-intención, así que esa parte
    // se queda en la API (fuera de alcance del ítem).
    //
    // Ítem 4.9: el parámetro `rerank` desapareció de esta firma. Era el proxy con el que
    // el gate adivinaba en qué escala venía el score; ahora eso lo declara el propio
    // resultado (RetrievalResult.ScoreScale), así que la API ya no tiene que reenviar una
    // bandera de la petición para que la banda se calcule bien.
    //
    // Nota sobre lista vacía (asimetría que señala el ítem 4.7): antes, con
    // `results.Count == 0` la expresión `rerank && results.Count > 0 && ...`
    // daba false siempre — NUNCA suprimía, sin importar rerank.
    // ConfidenceGate.Assess trata 0 chunks como sin-grounding incondicionalmente
    // (ver su comentario: "zero chunks retrieved at all" es uno de los dos casos
    // que unifica), así que delegar en el gate SÍ cambia el booleano de
    // ShouldSuppressSources para lista vacía (pasa a ser "suprimir"). Se decide
    // seguir al gate (single source of truth) en vez de preservar la asimetría,
    // porque no hay JSON observable que cambie: con `results` vacío,
    // BuildSourcesAsync(results, ...) ya devuelve una lista vacía sin importar el
    // valor de este booleano — Zip no tiene elementos que producir. Es decir, el
    // único caso con diferencia observable real es results.Count > 0, y ahí la
    // fórmula del gate es idéntica a la que había aquí.
    static bool ShouldSuppressSources(
        bool isMetaIntent,
        ConfidenceGate confidenceGate,
        IReadOnlyList<RetrievalResult> results,
        float minScore,
        string query) =>
        isMetaIntent || !confidenceGate.Assess(results, minScore, query).HasGrounding;

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

    // Chequeo real de los tres componentes de los que depende el motor: Qdrant, Ollama
    // y el brain de vectorización ONNX. IVectorizationBrain se resuelve desde el
    // IServiceProvider (no por parámetro del delegate) por el mismo motivo que
    // DoctorCommand lo resuelve perezosamente desde _services: es singleton y su
    // InferenceSession ya se construyó en el primer uso real (embeddings/reranker),
    // así que esto NO abre una sesión ONNX nueva por llamada — solo lee
    // EmbeddingDimensions de la que ya vive en el contenedor. Si el modelo faltara,
    // ese GetRequiredService fallaría aquí (dentro del try), no en el arranque del host.
    app.MapGet("/api/health", async (
        Qdrant.Client.QdrantClient qdrant,
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaOptions> ollamaOptions,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
    {
        var checks = new Dictionary<string, string>();
        var healthy = true;

        try
        {
            await qdrant.ListCollectionsAsync(cancellationToken);
            checks["qdrant"] = "ok";
        }
        catch (Exception ex)
        {
            healthy = false;
            checks["qdrant"] = $"down: {ex.Message}";
        }

        try
        {
            // GET a /v1/models (OpenAI-compatible, lo que Ollama expone en el
            // Endpoint configurado): barato, no carga ningún modelo en memoria, solo
            // confirma que el proceso de Ollama responde. Timeout corto y propio —
            // Ollama:TimeoutSeconds (hasta 120s) es para generación, no para esto.
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(3);
            var response = await httpClient.GetAsync(
                $"{ollamaOptions.Value.Endpoint.TrimEnd('/')}/models", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                checks["ollama"] = "ok";
            }
            else
            {
                healthy = false;
                checks["ollama"] = $"down: status {(int)response.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            healthy = false;
            checks["ollama"] = $"down: {ex.Message}";
        }

        try
        {
            var brain = services.GetRequiredService<IVectorizationBrain>();
            if (brain.EmbeddingDimensions > 0)
            {
                checks["onnx"] = $"ok ({brain.EmbeddingDimensions} dims)";
            }
            else
            {
                healthy = false;
                checks["onnx"] = "down: EmbeddingDimensions <= 0";
            }
        }
        catch (Exception ex)
        {
            healthy = false;
            checks["onnx"] = $"down: {ex.Message}";
        }

        return healthy
            ? Results.Ok(new { status = "ok", checks })
            : Results.Problem(
                title: "Uno o más componentes del motor RAG no están disponibles.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?> { ["checks"] = checks });
    });

    // Alimenta el selector de colección de la página — así el equipo no
    // depende de que quede fija a un proyecto (hoy innovapp-docs, mañana
    // podría ser cualquier otra colección ingestada).
    //
    // Ítem 7.b: este listado usaba ListCollectionsAsync sin filtrar, así que en modo
    // Empresarial cualquier actor autenticado (o incluso sin autenticar) veía los
    // nombres de TODAS las colecciones, incluidas las ajenas o no publicadas —
    // exactamente la filtración que /api/search, /api/ask y /api/ask/stream ya
    // evitan al negar por AuthorizeCollectionAsync. Ahora aplica la misma
    // ICollectionAuthorizationService por colección: solo entran al listado las que
    // el actor puede leer. En modo Local (todo actor es administrador implícito),
    // el comportamiento no cambia — sigue siendo el listado completo de siempre.
    app.MapGet("/api/collections", async (
        Qdrant.Client.QdrantClient qdrant,
        QdrantVectorStore store,
        HttpContext http,
        ICollectionActorResolver actorResolver,
        ICollectionAuthorizationService authorization,
        CancellationToken cancellationToken) =>
    {
        var allCollections = await qdrant.ListCollectionsAsync(cancellationToken);
        var actor = actorResolver.Resolve(() => ReadCollectionIdentity(http));

        var visibleCollections = new List<string>();
        foreach (var collection in allCollections)
        {
            var manifest = await store.GetManifestAsync(collection, cancellationToken);
            if (authorization.Authorize(manifest, actor))
                visibleCollections.Add(collection);
        }

        return Results.Ok(new { collections = visibleCollections, @default = defaultCollection });
    });

    // Endpoint de prueba para verificar que los instrumentos de métricas están activos
    // Registra valores en los 4 contadores del Meter: ChunksIndexedTotal, IngestionErrorsTotal,
    // SearchLatencyMs, SearchErrorsTotal
    app.MapGet("/api/test-metrics", () =>
    {
        RagEngineMetrics.ChunksIndexedTotal.Add(10, new KeyValuePair<string, object?>("collection", "test"));
        RagEngineMetrics.IngestionErrorsTotal.Add(1, new KeyValuePair<string, object?>("stage", "test"));
        RagEngineMetrics.SearchLatencyMs.Record(42.5, new KeyValuePair<string, object?>("collection", "test"));
        RagEngineMetrics.SearchErrorsTotal.Add(1, new KeyValuePair<string, object?>("collection", "test"));

        return Results.Ok(new { message = "Metrics recorded. Check with: dotnet-counters monitor -p <PID> --counters 'Rag.Context.Engine'" });
    });

    // Retrieval/generación/caché se resuelven después de autorizar: el binding de
    // parámetros DI los construiría antes del handler, incluso para devolver 403.
    app.MapPost("/api/search", async (
        RagQueryRequest request,
        HttpContext http,
        QdrantVectorStore store,
        ICollectionActorResolver actorResolver,
        ICollectionAuthorizationService authorization,
        IRetrievalProfileResolver profileResolver,
        ILogger<Program> queryLogger,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "El campo 'query' es obligatorio." });

        var collection = request.Collection ?? defaultCollection;
        var (denied, manifest) = await AuthorizeCollectionAsync(
            collection, http, store, actorResolver, authorization, cancellationToken);
        if (denied is not null)
            return denied;

        var retriever = http.RequestServices.GetRequiredService<ISemanticRetriever>();
        var summaryCache = http.RequestServices.GetRequiredService<SummaryCache>();
        var (topK, minScore, rerank, _) = ResolveEffectiveRetrievalDefaults(
            request, profileResolver.Resolve(manifest));

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
        HttpContext http,
        QdrantVectorStore store,
        ICollectionActorResolver actorResolver,
        ICollectionAuthorizationService authorization,
        IRetrievalProfileResolver profileResolver,
        ILogger<Program> queryLogger,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "El campo 'query' es obligatorio." });

        var collection = request.Collection ?? defaultCollection;
        var (denied, manifest) = await AuthorizeCollectionAsync(
            collection, http, store, actorResolver, authorization, cancellationToken);
        if (denied is not null)
            return denied;

        var retriever = http.RequestServices.GetRequiredService<ISemanticRetriever>();
        var generation = http.RequestServices.GetRequiredService<IRagGenerationService>();
        var confidenceGate = http.RequestServices.GetRequiredService<ConfidenceGate>();
        var metaIntentDetector = http.RequestServices.GetRequiredService<IMetaIntentDetector>();
        var summaryCache = http.RequestServices.GetRequiredService<SummaryCache>();
        var (topK, minScore, rerank, promptFamily) = ResolveEffectiveRetrievalDefaults(
            request, profileResolver.Resolve(manifest));
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
            promptFamily, onStatus: null, cancellationToken: cancellationToken))
        {
            answer.Append(fragment);
        }
        generationStopwatch.Stop();

        var retrievedSources = await sourcesTask;
        var answerText = answer.ToString();
        var sources = ShouldSuppressSources(isMetaIntent, confidenceGate, retrievedSources, minScore, request.Query)
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
        QdrantVectorStore store,
        ICollectionActorResolver actorResolver,
        ICollectionAuthorizationService authorization,
        IRetrievalProfileResolver profileResolver,
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
        var (denied, manifest) = await AuthorizeCollectionAsync(
            collection, http, store, actorResolver, authorization, cancellationToken);
        if (denied is not null)
        {
            await denied.ExecuteAsync(http);
            return;
        }

        var retriever = http.RequestServices.GetRequiredService<ISemanticRetriever>();
        var generation = http.RequestServices.GetRequiredService<IRagGenerationService>();
        var confidenceGate = http.RequestServices.GetRequiredService<ConfidenceGate>();
        var metaIntentDetector = http.RequestServices.GetRequiredService<IMetaIntentDetector>();
        var summaryCache = http.RequestServices.GetRequiredService<SummaryCache>();
        var (topK, minScore, rerank, promptFamily) = ResolveEffectiveRetrievalDefaults(
            request, profileResolver.Resolve(manifest));
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
        var sources = ShouldSuppressSources(isMetaIntent, confidenceGate, results, minScore, request.Query)
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
        try
        {
            await foreach (var fragment in generation.AskStreamingAsync(
                request.Query, collection, topK, minScore, rerank, responseMode, history,
                promptFamily,
                onStatus: async (message, ct) => await SendAsync("status", new { message }),
                cancellationToken: cancellationToken))
            {
                answer.Append(fragment);
                await SendAsync("token", new { text = fragment });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // El cliente cortó la conexión — no hay a quién mandarle un evento de error.
            throw;
        }
        catch (Exception ex)
        {
            // Ítem 8.e: para acá la respuesta SSE ya se abrió (los eventos "status"/
            // "sources" de arriba ya salieron), así que los headers ya se escribieron —
            // no se puede convertir esto en un ProblemDetails 500 como hace
            // UseExceptionHandler para /api/ask, que sí puede porque nada se había
            // escrito todavía. Tampoco se reintenta: si ya se emitieron fragmentos de
            // "token", reabrir la generación duplicaría lo que el cliente ya recibió
            // (ChatAnswerStreamer ya no reintenta una vez abierto el stream, por la
            // misma razón). Se cierra limpio con un evento "error" y se corta acá, sin
            // "done" ni el log de QueryEvent de abajo, que asume una respuesta completa.
            queryLogger.LogError(ex, "Fallo generando respuesta en /api/ask/stream para '{Query}'", request.Query);
            await SendAsync("error", new { message = "Ocurrió un error generando la respuesta." });
            return;
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
    // Libera el entorno global de ONNX, igual que hace el CLI.
    //
    // Aquí hubo una nota que decía lo contrario ("la API ya cierra con exit 0, no hay
    // problema que arreglar"). Estaba equivocada, y el ítem 1.13 del ledger midió por
    // qué: aquella medición apagó la API sin haberle hecho ninguna consulta, y la
    // InferenceSession se construye perezosamente en el primer request. Un host que
    // nunca vectorizó nada no toca el runtime nativo y, efectivamente, sale 0.
    //
    // Con una sola consulta servida, la API aborta exactamente igual que abortaba el
    // CLI. Medido el 2026-08-24 sobre este host: 3 de 3 apagados con SIGINT tras un
    // POST /api/search terminaron en exit 134 con 'libc++abi: mutex lock failed';
    // 2 de 2 apagados sin consulta previa terminaron en 0.
    //
    // La otra mitad de la hipótesis vieja —que este host no destruye el contenedor de
    // DI y por eso la sesión sobrevive— también es falsa: WebApplication.Run() destruye
    // el host en su propio finally, y una traza en OnnxVectorizationBrain.Dispose
    // confirmó que corre al recibir SIGINT. La sesión se liberaba bien; lo que quedaba
    // sin liberar era el OrtEnv global, que es justo lo que arregla Shutdown().
    //
    // Va aquí, después de app.Run(), porque para entonces el contenedor ya se destruyó
    // y no quedan sesiones vivas — la misma precondición que documenta Shutdown().
    OnnxRuntimeLifetime.Shutdown();

    Log.CloseAndFlush();
}

// Marcador necesario para WebApplicationFactory<Program> (5.f.4-harness-4-actores):
// los top-level statements generan una clase Program interna por defecto, invisible
// desde el ensamblado de tests. Esta declaración parcial y pública no cambia ningún
// comportamiento de arranque — solo expone el tipo de entrada para el harness HTTP.
public partial class Program;
