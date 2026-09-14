using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Cubre 5.f.4-harness-4-actores: cierra la sombrilla 5.f-revivir-collection-manifest
/// ejercitando por HTTP real (WebApplicationFactory&lt;Program&gt; contra
/// Api/Program.cs, no un stub de autorización) los cuatro actores de
/// 5.f.3-acl-autorizacion-y-modo-local — administrador, usuario autorizado, usuario
/// ajeno y actor sin autenticar — más el escenario de manifiesto migrado
/// (5.f.2-migracion-manifiestos-antiguos): RequiredScopes vacío tras migrar nunca
/// habilita a nadie salvo al administrador.
///
/// Integración real: requiere el mismo Qdrant local (localhost:6333/6334) que usan
/// los demás tests de este archivo hermano (CollectionManifestPersistenceTests) y el
/// modelo ONNX de embeddings ya descargado (infra/download-model.sh) — /api/search
/// solo devuelve 403 SIN tocar el embedder, pero cada caso "autorizado" completa el
/// roundtrip real: vectoriza la query, busca en Qdrant y arma la respuesta. Sin
/// infraestructura, estos tests fallan con el error real de conexión/arranque, no se
/// marcan skip.
///
/// Cada test crea sus PROPIAS colecciones aisladas (prefijo
/// "rag-engine-test-http-acl-"), nunca toca "innovapp-docs" ni ninguna otra colección
/// servida — ver AGENTS.md "No tocar colecciones servidas por una prueba".
///
/// Limitación conocida (comparte causa raíz con 1.9-doctor-exit-134/1.13, ya cerrados):
/// el host de pruebas que carga el runtime ONNX in-process (vía WebApplicationFactory)
/// a veces aborta el proceso al finalizar en vez de salir limpio, lo que puede cortar
/// <see cref="DisposeAsync"/> a mitad de la limpieza de colecciones. Observado en este
/// ítem: la corrida completa (234/234) cerró limpio, pero dos corridas aisladas
/// anteriores dejaron colecciones "rag-engine-test-http-acl-mig-*" residuales que se
/// borraron a mano tras verificar que no eran servidas por nada. No es una regresión de
/// este ítem — es el mismo abort de proceso que ya documenta Api/Program.cs — pero
/// conviene revisar `curl localhost:6333/collections` tras correr este archivo solo.
/// </summary>
public sealed class CollectionManifestHttpHarnessTests : IAsyncLifetime
{
    private const int EmbeddingDimension = 384; // debe igualar OnnxBrain:EmbeddingDimensions de appsettings.json
    private const string RequiredScope = "rag.read.harness";
    private const string AllowedTenant = "tenant-harness";

    private readonly string _publishedCollection = $"rag-engine-test-http-acl-pub-{Guid.NewGuid():N}";
    private readonly string _migratedCollection = $"rag-engine-test-http-acl-mig-{Guid.NewGuid():N}";

    private QdrantClient _client = null!;
    private QdrantVectorStore _store = null!;
    private HttpAclWebApplicationFactory _factory = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _client = new QdrantClient("localhost", 6334);
        _store = new QdrantVectorStore(_client, NullLogger<QdrantVectorStore>.Instance);

        await _store.EnsureCollectionAsync(_publishedCollection, EmbeddingDimension);
        await _store.UpsertManifestAsync(_publishedCollection, new CollectionManifest
        {
            CollectionName = _publishedCollection,
            ModelName = "paraphrase-multilingual-MiniLM-L12-v2",
            ModelOnnxSha256 = "hash-harness",
            EmbeddingDimension = EmbeddingDimension,
            RequiredScopes = [RequiredScope],
            Tenants = [AllowedTenant]
        });

        await _store.EnsureCollectionAsync(_migratedCollection, EmbeddingDimension);
        await UpsertLegacyManifestAsync(_migratedCollection);

        _factory = new HttpAclWebApplicationFactory();
        _http = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _factory.DisposeAsync();

        foreach (var collection in new[] { _publishedCollection, _migratedCollection })
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

    /// <summary>
    /// Escribe un manifiesto en el formato PREVIO a 5.f.1 (sin RequiredScopes/Tenants/
    /// Profile en el JSON), directamente vía el cliente de Qdrant — no vía
    /// <see cref="CollectionManifest.ToJson"/>, que siempre serializa el esquema
    /// extendido. Es el mismo fixture que valida
    /// 5.f.2-migracion-manifiestos-antiguos, pero servido por HTTP real.
    /// </summary>
    private async Task UpsertLegacyManifestAsync(string collectionName)
    {
        var legacyJson = JsonSerializer.Serialize(new
        {
            collectionName,
            modelName = "paraphrase-multilingual-MiniLM-L12-v2",
            modelOnnxSha256 = "hash-harness-legacy",
            embeddingDimension = EmbeddingDimension,
            createdAt = DateTimeOffset.UtcNow,
            lastIndexedAt = DateTimeOffset.UtcNow,
            totalChunks = 7
            // Sin requiredScopes/tenants/profile: el esquema previo a 5.f.1.
        });

        var point = new PointStruct { Id = new PointId { Uuid = QdrantVectorStore.ManifestPointId.ToString() } };
        var denseVec = new Vector();
        denseVec.Data.AddRange(new float[EmbeddingDimension]);
        var namedVectors = new NamedVectors();
        namedVectors.Vectors["dense"] = denseVec;
        point.Vectors = new Vectors { Vectors_ = namedVectors };
        point.Payload[QdrantVectorStore.IsManifestPayloadKey] = new Value { BoolValue = true };
        point.Payload[QdrantVectorStore.ManifestPayloadKey] = new Value { StringValue = legacyJson };

        await _client.UpsertAsync(collectionName, new[] { point }, wait: true);
    }

    private HttpRequestMessage BuildSearchRequest(string collection, TestActor actor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/search")
        {
            Content = JsonContent.Create(new
            {
                query = "¿que es esto?",
                collection,
                topK = 1,
                minScore = 0.0f,
                rerank = false
            })
        };
        actor.Apply(request);
        return request;
    }

    [Fact]
    public async Task Administrador_lee_la_coleccion_publicada()
    {
        using var response = await _http.SendAsync(BuildSearchRequest(_publishedCollection, TestActor.Administrator));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Usuario_autorizado_por_scope_y_tenant_lee_la_coleccion_publicada()
    {
        var autorizado = TestActor.User(scopes: [RequiredScope], tenant: AllowedTenant);

        using var response = await _http.SendAsync(BuildSearchRequest(_publishedCollection, autorizado));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("otro-scope", AllowedTenant)]
    [InlineData(RequiredScope, "tenant-ajeno")]
    public async Task Usuario_ajeno_por_scope_o_tenant_es_rechazado(string scope, string tenant)
    {
        var ajeno = TestActor.User(scopes: [scope], tenant: tenant);

        using var response = await _http.SendAsync(BuildSearchRequest(_publishedCollection, ajeno));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Actor_sin_autenticar_es_rechazado_aunque_reclame_privilegios()
    {
        using var response = await _http.SendAsync(
            BuildSearchRequest(_publishedCollection, TestActor.Unauthenticated));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Coleccion_migrada_sin_requiredscopes_solo_admite_al_administrador()
    {
        var autorizadoEnOtraColeccion = TestActor.User(scopes: [RequiredScope], tenant: AllowedTenant);

        using var negado = await _http.SendAsync(
            BuildSearchRequest(_migratedCollection, autorizadoEnOtraColeccion));
        using var permitido = await _http.SendAsync(
            BuildSearchRequest(_migratedCollection, TestActor.Administrator));

        Assert.Equal(HttpStatusCode.Forbidden, negado.StatusCode);
        Assert.Equal(HttpStatusCode.OK, permitido.StatusCode);
    }

    /// <summary>Describe qué claims debe inyectar el middleware de prueba en HttpContext.User para este request.</summary>
    private sealed record TestActor(bool Authenticated, bool IsAdmin, IReadOnlyList<string> Scopes, string? Tenant)
    {
        public static readonly TestActor Administrator = new(true, true, [], null);
        public static readonly TestActor Unauthenticated = new(false, true, [RequiredScope], AllowedTenant);

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
    /// (para ejercitar el mapeo de identidad, no el atajo de administrador sintético de
    /// Local) y un middleware de encabezados que sustituye a un IDP real por las
    /// cabeceras de <see cref="TestActor"/> — el resto del pipeline (autorización,
    /// GetManifestAsync, retrieval) es el mismo código de producción.
    /// </summary>
    private sealed class HttpAclWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("HttpAclHarness");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authorization:Mode"] = "Empresarial"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new TestActorStartupFilter());
            });
        }
    }

    /// <summary>
    /// Inserta el middleware de identidad falsa antes que los endpoints del Program.cs
    /// real — mismo mecanismo estándar para inyectar middleware de prueba en apps de
    /// endpoints mínimos, donde no existe un método Configure separado que interceptar.
    /// </summary>
    private sealed class TestActorStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
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
                    foreach (var scope in scopesRaw.ToString()
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        claims.Add(new Claim("scope", scope));
                    }
                }

                if (context.Request.Headers.TryGetValue(TenantHeader, out var tenantRaw)
                    && !string.IsNullOrEmpty(tenantRaw))
                {
                    claims.Add(new Claim("tenant", tenantRaw!));
                }

                var identity = new ClaimsIdentity(claims, authenticationType: "TestActorHarness");
                context.User = new ClaimsPrincipal(identity);
            }
            // Sin la cabecera de autenticación, HttpContext.User conserva el principal
            // anónimo por defecto de ASP.NET Core — ninguna identidad autenticada, tal
            // como llegaría una request real sin IDP.

            await next();
        }
    }
}
