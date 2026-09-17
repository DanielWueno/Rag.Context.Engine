using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Cubre el criterio literal de 7.a-perfil-por-coleccion vía HTTP real (no un stub):
/// "Dos colecciones con perfiles distintos dan resultados distintos para la MISMA
/// query con la misma llamada HTTP [...] y sin perfil declarado, el resultado es
/// idéntico al baseline actual".
///
/// Estrategia de fixture: en vez de vectores ficticios (que un umbral de similitud
/// &gt;0 filtraría por completo, ver <see cref="TenantPayloadFilterTests"/> para el
/// caso donde eso no importa), cada punto sembrado usa el embedding REAL de la
/// consulta que se va a lanzar — cosine similarity 1.0 garantizado contra
/// MinimumSimilarityScore, sin depender de que el modelo ordene nada por calidad
/// semántica: este test mide CUÁNTOS resultados vuelven (el corte de <c>topK</c>),
/// no la calidad del ranking.
///
/// Integración real contra Qdrant local (localhost:6333/6334) y el modelo ONNX de
/// embeddings ya descargado — mismo requisito que <see cref="CollectionManifestHttpHarnessTests"/>.
/// Cada test crea sus propias colecciones (prefijo "rag-engine-test-profile-"), nunca
/// toca ninguna colección servida.
/// </summary>
public sealed class RetrievalProfileHttpHarnessTests : IAsyncLifetime
{
    private const int EmbeddingDimension = 384; // debe igualar OnnxBrain:EmbeddingDimensions de appsettings.json
    private const string Query = "¿qué perfil de recuperación usa esta colección?";
    private const string ProfileName = "rag-engine-test-profile-topk3";
    private const int ProfileTopK = 3;
    private const int SeedPointCount = 12; // > 10 (default global) y > ProfileTopK: distingue los tres cortes posibles

    private readonly string _profiledCollection = $"rag-engine-test-profile-perfil-{Guid.NewGuid():N}";
    private readonly string _baselineCollection = $"rag-engine-test-profile-baseline-{Guid.NewGuid():N}";

    private QdrantClient _client = null!;
    private QdrantVectorStore _store = null!;
    private ProfileWebApplicationFactory _factory = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _client = new QdrantClient("localhost", 6334);
        _store = new QdrantVectorStore(_client, NullLogger<QdrantVectorStore>.Instance);
        _factory = new ProfileWebApplicationFactory();
        _http = _factory.CreateClient();

        // El embedding real de la consulta se reutiliza como vector de CADA punto
        // sembrado: garantiza similitud coseno 1.0 contra MinimumSimilarityScore,
        // sin importar qué valor use cada perfil — el test mide el CORTE (topK), no
        // el orden por calidad semántica.
        using var scope = _factory.Services.CreateScope();
        var brain = scope.ServiceProvider.GetRequiredService<IVectorizationBrain>();
        var queryVector = await brain.GenerateEmbeddingAsync(Query, CancellationToken.None);

        await _store.EnsureCollectionAsync(_profiledCollection, EmbeddingDimension);
        await _store.UpsertManifestAsync(_profiledCollection, new CollectionManifest
        {
            CollectionName = _profiledCollection,
            ModelName = "paraphrase-multilingual-MiniLM-L12-v2",
            ModelOnnxSha256 = "hash-profile-harness",
            EmbeddingDimension = EmbeddingDimension,
            Profile = ProfileName
        });
        await SeedPointsAsync(_profiledCollection, queryVector);

        await _store.EnsureCollectionAsync(_baselineCollection, EmbeddingDimension);
        // Deliberadamente SIN manifiesto: GetManifestAsync devuelve null, el resolver
        // de perfil resuelve null, y el endpoint debe caer en los literales 10/0.10f/
        // true que usaba antes de este ítem — el propio caso de control del ítem.
        await SeedPointsAsync(_baselineCollection, queryVector);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _factory.DisposeAsync();

        foreach (var collection in new[] { _profiledCollection, _baselineCollection })
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

    private async Task SeedPointsAsync(string collectionName, float[] queryVector)
    {
        var batch = new List<VectorStoreBatchItem>();
        for (int i = 0; i < SeedPointCount; i++)
        {
            var chunk = new CodeChunk
            {
                Id = Guid.NewGuid(),
                Content = $"contenido-{i}",
                EnrichedContent = $"contenido-{i}",
                Metadata = new CodeChunkMetadata(
                    FilePath: $"/repo/chunk-{i}.cs",
                    RelativeFilePath: $"chunk-{i}.cs",
                    Language: SourceLanguage.CSharp,
                    Namespace: null,
                    ClassName: null,
                    MethodName: null,
                    StartLine: 1,
                    EndLine: 1,
                    LastModified: DateTimeOffset.UtcNow,
                    RepositoryName: "test-repo"),
                Type = ChunkType.Method,
                ContentHash = $"hash-{i}"
            };
            batch.Add(new VectorStoreBatchItem(chunk, queryVector, Array.Empty<SparseEntry>(), null));
        }
        await _store.UpsertBatchAsync(collectionName, batch, waitForCommit: true);
    }

    private static HttpRequestMessage BuildSearchRequest(string collection) =>
        new(HttpMethod.Post, "/api/search")
        {
            // topK/minScore/rerank deliberadamente OMITIDOS: es exactamente la
            // ausencia que decide entre el default global y el perfil declarado.
            Content = JsonContent.Create(new { query = Query, collection })
        };

    [Fact]
    public async Task Coleccion_con_perfil_declarado_corta_al_topK_del_perfil()
    {
        using var response = await _http.SendAsync(BuildSearchRequest(_profiledCollection));
        response.EnsureSuccessStatusCode();

        var sources = await response.Content.ReadFromJsonAsync<List<SearchSource>>();

        Assert.NotNull(sources);
        Assert.Equal(ProfileTopK, sources!.Count);
    }

    [Fact]
    public async Task Coleccion_sin_perfil_declarado_corta_al_default_global_de_siempre()
    {
        using var response = await _http.SendAsync(BuildSearchRequest(_baselineCollection));
        response.EnsureSuccessStatusCode();

        var sources = await response.Content.ReadFromJsonAsync<List<SearchSource>>();

        // 10 es el literal que ya usaba /api/search antes de 7.a (request.TopK ?? 10):
        // sin perfil, debe seguir siendo EXACTAMENTE ese valor, no otro.
        Assert.NotNull(sources);
        Assert.Equal(10, sources!.Count);
    }

    [Fact]
    public async Task Misma_query_misma_llamada_http_perfiles_distintos_dan_resultados_distintos()
    {
        using var perfilResponse = await _http.SendAsync(BuildSearchRequest(_profiledCollection));
        using var baselineResponse = await _http.SendAsync(BuildSearchRequest(_baselineCollection));

        var perfilSources = await perfilResponse.Content.ReadFromJsonAsync<List<SearchSource>>();
        var baselineSources = await baselineResponse.Content.ReadFromJsonAsync<List<SearchSource>>();

        Assert.NotEqual(perfilSources!.Count, baselineSources!.Count);
    }

    /// <summary>Subconjunto mínimo del payload de <c>/api/search</c> necesario para contar resultados.</summary>
    private sealed record SearchSource(string File, int StartLine, int EndLine, float Score);

    /// <summary>
    /// Host de prueba sobre el Program.cs real de la API: Authorization:Mode se deja en
    /// el default (Local) de appsettings.json — todo actor es administrador implícito,
    /// así que no hace falta simular identidad (ítem 7.a no toca ACL). Lo único que se
    /// sobrescribe es el catálogo de perfiles, con el perfil que este archivo declara.
    /// </summary>
    private sealed class ProfileWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("RetrievalProfileHarness");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"RetrievalProfiles:Profiles:{ProfileName}:TopK"] = ProfileTopK.ToString(),
                    [$"RetrievalProfiles:Profiles:{ProfileName}:MinScore"] = "0.0",
                    [$"RetrievalProfiles:Profiles:{ProfileName}:UseReRanking"] = "false"
                });
            });
        }
    }
}
