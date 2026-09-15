using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Cubre el criterio literal de 7.b-validar-coleccion-en-la-api: pedir una colección
/// AJENA (existe, publicada para otro tenant) y una colección INEXISTENTE debe devolver
/// el mismo ProblemDetails (mismo status, mismo título, mismo esquema de propiedades) en
/// /api/search, /api/ask y /api/ask/stream — sin filtrar si la colección existe. También
/// cubre que /api/collections, alimentado por el mismo <c>ICollectionAuthorizationService</c>
/// que ya usan los otros tres endpoints (ver AuthorizeCollectionAsync en Program.cs), NUNCA
/// lista una colección ajena o no publicada a un actor sin privilegio, aunque sí lista todo
/// para el administrador — con dos tenants, un actor administrador y una colección sin
/// publicar en el mismo fixture.
///
/// Integración real contra Qdrant local (localhost:6333/6334) y el modelo ONNX de
/// embeddings ya descargado — mismo requisito que <see cref="CollectionManifestHttpHarnessTests"/>,
/// que ya cubre los cuatro actores (admin/autorizado/ajeno/sin-autenticar) contra UNA sola
/// colección publicada. Este archivo no repite esa matriz: se centra en la parte que
/// faltaba del criterio de 7.b — comparar ajena vs inexistente, y la visibilidad del
/// listado — con dos colecciones publicadas para tenants distintos más una sin publicar.
///
/// Cada test crea sus PROPIAS colecciones aisladas (prefijo "rag-engine-test-http-vis-"),
/// nunca toca ninguna colección servida — ver AGENTS.md "No tocar colecciones servidas por
/// una prueba".
/// </summary>
public sealed class CollectionAuthorizationVisibilityHttpHarnessTests : IAsyncLifetime
{
    private const int EmbeddingDimension = 384; // debe igualar OnnxBrain:EmbeddingDimensions de appsettings.json
    private const string ScopeA = "rag.read.tenant-a";
    private const string ScopeB = "rag.read.tenant-b";
    private const string TenantA = "tenant-a-vis";
    private const string TenantB = "tenant-b-vis";

    private readonly string _collectionA = $"rag-engine-test-http-vis-a-{Guid.NewGuid():N}";
    private readonly string _collectionB = $"rag-engine-test-http-vis-b-{Guid.NewGuid():N}";
    private readonly string _collectionUnpublished = $"rag-engine-test-http-vis-unpub-{Guid.NewGuid():N}";
    private readonly string _collectionInexistente = $"rag-engine-test-http-vis-noexiste-{Guid.NewGuid():N}";

    private QdrantClient _client = null!;
    private QdrantVectorStore _store = null!;
    private VisibilityWebApplicationFactory _factory = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _client = new QdrantClient("localhost", 6334);
        _store = new QdrantVectorStore(_client, NullLogger<QdrantVectorStore>.Instance);

        await _store.EnsureCollectionAsync(_collectionA, EmbeddingDimension);
        await _store.UpsertManifestAsync(_collectionA, new CollectionManifest
        {
            CollectionName = _collectionA,
            ModelName = "paraphrase-multilingual-MiniLM-L12-v2",
            ModelOnnxSha256 = "hash-vis-a",
            EmbeddingDimension = EmbeddingDimension,
            RequiredScopes = [ScopeA],
            Tenants = [TenantA]
        });

        await _store.EnsureCollectionAsync(_collectionB, EmbeddingDimension);
        await _store.UpsertManifestAsync(_collectionB, new CollectionManifest
        {
            CollectionName = _collectionB,
            ModelName = "paraphrase-multilingual-MiniLM-L12-v2",
            ModelOnnxSha256 = "hash-vis-b",
            EmbeddingDimension = EmbeddingDimension,
            RequiredScopes = [ScopeB],
            Tenants = [TenantB]
        });

        // Existe en Qdrant pero nunca se publicó manifiesto: GetManifestAsync devuelve
        // null exactamente igual que para una colección que nunca se creó — es el caso
        // de control que demuestra que "ajena/no publicada" y "no existe" son
        // indistinguibles para un actor sin privilegio.
        await _store.EnsureCollectionAsync(_collectionUnpublished, EmbeddingDimension);

        // _collectionInexistente deliberadamente NUNCA se crea en Qdrant.

        _factory = new VisibilityWebApplicationFactory();
        _http = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _factory.DisposeAsync();

        foreach (var collection in new[] { _collectionA, _collectionB, _collectionUnpublished })
        {
            try
            {
                await _client.DeleteCollectionAsync(collection);
            }
            catch
            {
                // best-effort cleanup; no ocultar el resultado del test por un fallo de limpieza
            }
        }
    }

    private static TestActor ActorTenantA() => TestActor.User(scopes: [ScopeA], tenant: TenantA);

    private static HttpRequestMessage BuildAskStreamRequest(string collection, TestActor actor) =>
        WithActor(new HttpRequestMessage(HttpMethod.Post, "/api/ask/stream")
        {
            Content = JsonContent.Create(new { query = "¿que es esto?", collection, topK = 1, minScore = 0.0f, rerank = false })
        }, actor);

    private static HttpRequestMessage WithActor(HttpRequestMessage request, TestActor actor)
    {
        actor.Apply(request);
        return request;
    }

    [Theory]
    [InlineData("/api/search")]
    [InlineData("/api/ask")]
    public async Task Coleccion_ajena_e_inexistente_dan_el_mismo_problemdetails(string endpoint)
    {
        var actor = ActorTenantA();

        HttpRequestMessage BuildFor(string collection) => WithActor(new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { query = "¿que es esto?", collection, topK = 1, minScore = 0.0f, rerank = false })
        }, actor);

        using var ajenaResponse = await _http.SendAsync(BuildFor(_collectionB));
        using var inexistenteResponse = await _http.SendAsync(BuildFor(_collectionInexistente));
        using var noPublicadaResponse = await _http.SendAsync(BuildFor(_collectionUnpublished));

        Assert.Equal(HttpStatusCode.Forbidden, ajenaResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, inexistenteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noPublicadaResponse.StatusCode);

        var ajenaBody = await ajenaResponse.Content.ReadAsStringAsync();
        var inexistenteBody = await inexistenteResponse.Content.ReadAsStringAsync();
        var noPublicadaBody = await noPublicadaResponse.Content.ReadAsStringAsync();

        using var ajenaDoc = JsonDocument.Parse(ajenaBody);
        using var inexistenteDoc = JsonDocument.Parse(inexistenteBody);
        using var noPublicadaDoc = JsonDocument.Parse(noPublicadaBody);

        // Mismo esquema (mismas propiedades) y mismo título/status: nada distingue
        // "no tiene permiso sobre una colección real" de "la colección no existe" ni de
        // "existe pero nunca se publicó". Se ignora deliberadamente cualquier campo tipo
        // traceId/correlation que difiera por request sin filtrar información de negocio.
        var ajenaPropiedades = ajenaDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        var inexistentePropiedades = inexistenteDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        var noPublicadaPropiedades = noPublicadaDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(ajenaPropiedades, inexistentePropiedades);
        Assert.Equal(ajenaPropiedades, noPublicadaPropiedades);

        Assert.Equal(ajenaDoc.RootElement.GetProperty("title").GetString(), inexistenteDoc.RootElement.GetProperty("title").GetString());
        Assert.Equal(ajenaDoc.RootElement.GetProperty("title").GetString(), noPublicadaDoc.RootElement.GetProperty("title").GetString());
        Assert.Equal(ajenaDoc.RootElement.GetProperty("status").GetInt32(), inexistenteDoc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(ajenaDoc.RootElement.GetProperty("status").GetInt32(), noPublicadaDoc.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Ask_stream_coleccion_ajena_e_inexistente_dan_el_mismo_problemdetails()
    {
        var actor = ActorTenantA();

        using var ajenaResponse = await _http.SendAsync(BuildAskStreamRequest(_collectionB, actor));
        using var inexistenteResponse = await _http.SendAsync(BuildAskStreamRequest(_collectionInexistente, actor));

        Assert.Equal(HttpStatusCode.Forbidden, ajenaResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, inexistenteResponse.StatusCode);
        // /api/ask/stream nunca llegó a fijar Content-Type text/event-stream: la
        // autorización corta ANTES del primer SendAsync SSE.
        Assert.NotEqual("text/event-stream", ajenaResponse.Content.Headers.ContentType?.MediaType);
        Assert.NotEqual("text/event-stream", inexistenteResponse.Content.Headers.ContentType?.MediaType);

        var ajenaBody = await ajenaResponse.Content.ReadAsStringAsync();
        var inexistenteBody = await inexistenteResponse.Content.ReadAsStringAsync();
        using var ajenaDoc = JsonDocument.Parse(ajenaBody);
        using var inexistenteDoc = JsonDocument.Parse(inexistenteBody);

        Assert.Equal(
            ajenaDoc.RootElement.GetProperty("title").GetString(),
            inexistenteDoc.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            ajenaDoc.RootElement.GetProperty("status").GetInt32(),
            inexistenteDoc.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Listado_de_colecciones_respeta_visibilidad_por_actor()
    {
        var actorTenantA = ActorTenantA();
        var actorTenantB = TestActor.User(scopes: [ScopeB], tenant: TenantB);

        using var responseTenantA = await _http.SendAsync(WithActor(new HttpRequestMessage(HttpMethod.Get, "/api/collections"), actorTenantA));
        using var responseTenantB = await _http.SendAsync(WithActor(new HttpRequestMessage(HttpMethod.Get, "/api/collections"), actorTenantB));
        using var responseAdmin = await _http.SendAsync(WithActor(new HttpRequestMessage(HttpMethod.Get, "/api/collections"), TestActor.Administrator));

        responseTenantA.EnsureSuccessStatusCode();
        responseTenantB.EnsureSuccessStatusCode();
        responseAdmin.EnsureSuccessStatusCode();

        var coleccionesTenantA = await ReadCollectionsAsync(responseTenantA);
        var coleccionesTenantB = await ReadCollectionsAsync(responseTenantB);
        var coleccionesAdmin = await ReadCollectionsAsync(responseAdmin);

        // El actor del tenant A ve su propia colección publicada, pero ni la ajena
        // (tenant B) ni la no publicada.
        Assert.Contains(_collectionA, coleccionesTenantA);
        Assert.DoesNotContain(_collectionB, coleccionesTenantA);
        Assert.DoesNotContain(_collectionUnpublished, coleccionesTenantA);

        // Simétrico para el tenant B.
        Assert.Contains(_collectionB, coleccionesTenantB);
        Assert.DoesNotContain(_collectionA, coleccionesTenantB);
        Assert.DoesNotContain(_collectionUnpublished, coleccionesTenantB);

        // El administrador ve las tres, incluida la no publicada.
        Assert.Contains(_collectionA, coleccionesAdmin);
        Assert.Contains(_collectionB, coleccionesAdmin);
        Assert.Contains(_collectionUnpublished, coleccionesAdmin);
    }

    private static async Task<List<string>> ReadCollectionsAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("collections").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    /// <summary>
    /// Contexto local explícito (Authorization:Mode=Local, el default de appsettings.json):
    /// CollectionActorResolver hace a TODO actor administrador implícito, sin necesidad de
    /// identidad — así que ni un request completamente anónimo debe perder visibilidad de
    /// la colección no publicada. Es el caso de control que separa "filtra por visibilidad"
    /// (lo que este ítem agrega) de "rompe el modo local de siempre".
    /// </summary>
    [Fact]
    public async Task En_modo_local_el_listado_no_filtra_nada_ni_siquiera_sin_identidad()
    {
        using var localFactory = new LocalModeWebApplicationFactory();
        using var localHttp = localFactory.CreateClient();

        using var response = await localHttp.GetAsync("/api/collections");
        response.EnsureSuccessStatusCode();

        var colecciones = await ReadCollectionsAsync(response);

        Assert.Contains(_collectionA, colecciones);
        Assert.Contains(_collectionB, colecciones);
        Assert.Contains(_collectionUnpublished, colecciones);
    }

    /// <summary>Host de prueba con Authorization:Mode=Local explícito, sin middleware de identidad falsa.</summary>
    private sealed class LocalModeWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("HttpVisibilityLocalHarness");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authorization:Mode"] = "Local"
                });
            });
        }
    }

    /// <summary>Describe qué claims debe inyectar el middleware de prueba en HttpContext.User para este request.</summary>
    private sealed record TestActor(bool Authenticated, bool IsAdmin, IReadOnlyList<string> Scopes, string? Tenant)
    {
        public static readonly TestActor Administrator = new(true, true, [], null);

        public static TestActor User(IReadOnlyList<string> scopes, string? tenant) =>
            new(true, false, scopes, tenant);

        public void Apply(HttpRequestMessage request)
        {
            request.Headers.Add(TestActorAuthHandler.AuthenticatedHeader, Authenticated ? "1" : "0");
            request.Headers.Add(TestActorAuthHandler.AdminHeader, IsAdmin ? "1" : "0");
            request.Headers.Add(TestActorAuthHandler.ScopesHeader, string.Join(' ', Scopes));
            if (Tenant is not null)
                request.Headers.Add(TestActorAuthHandler.TenantHeader, Tenant);
        }
    }

    /// <summary>
    /// Host de prueba sobre el Program.cs real de la API: Authorization:Mode=Empresarial
    /// (para ejercitar el mapeo de identidad real, no el atajo de administrador implícito
    /// de Local) y un middleware de encabezados que sustituye a un IDP real por las
    /// cabeceras de <see cref="TestActor"/> — mismo patrón que
    /// <see cref="CollectionManifestHttpHarnessTests"/>.
    /// </summary>
    private sealed class VisibilityWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("HttpVisibilityHarness");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authorization:Mode"] = "Empresarial"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new TestActorStartupFilter());
            });
        }
    }

    private sealed class TestActorStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(TestActorAuthHandler.Middleware);
            next(app);
        };
    }

    private static class TestActorAuthHandler
    {
        public const string AuthenticatedHeader = "X-Test-Authenticated";
        public const string AdminHeader = "X-Test-Admin";
        public const string ScopesHeader = "X-Test-Scopes";
        public const string TenantHeader = "X-Test-Tenant";

        public static async Task Middleware(HttpContext context, Func<Task> next)
        {
            if (context.Request.Headers.TryGetValue(AuthenticatedHeader, out var authenticatedRaw)
                && authenticatedRaw == "1")
            {
                var claims = new List<Claim>();
                if (context.Request.Headers.TryGetValue(AdminHeader, out var adminRaw) && adminRaw == "1")
                    claims.Add(new Claim(ClaimTypes.Role, "admin"));

                if (context.Request.Headers.TryGetValue(ScopesHeader, out var scopesRaw))
                {
                    foreach (var scope in scopesRaw.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        claims.Add(new Claim("scope", scope));
                }

                if (context.Request.Headers.TryGetValue(TenantHeader, out var tenantRaw)
                    && !string.IsNullOrEmpty(tenantRaw))
                {
                    claims.Add(new Claim("tenant", tenantRaw!));
                }

                var identity = new ClaimsIdentity(claims, authenticationType: "TestActorHarness");
                context.User = new ClaimsPrincipal(identity);
            }

            await next();
        }
    }
}
