using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Cubre el núcleo de 9.1 con Qdrant real: el contexto autorizado (tenant+módulo)
/// se aplica DENTRO del puerto de retrieval en búsqueda densa, sparse, resumen,
/// expansión two-hop y el camino HTTP que devuelve fuentes.
/// </summary>
public sealed class RetrievalAuthorizationContextHttpHarnessTests : IAsyncLifetime
{
    private const int Dimension = 4;
    private const string Scope = "rag.read.retrieval-auth";
    private const string TenantA = "tenant-a-auth";
    private const string TenantB = "tenant-b-auth";
    private const string ModuleAlpha = "alpha";
    private const string ModuleBeta = "beta";
    private const string SharedSymbol = "SharedDependency";

    private readonly string _collection = $"rag-engine-test-retrieval-auth-{Guid.NewGuid():N}";

    private QdrantClient _client = null!;
    private QdrantVectorStore _store = null!;
    private TestVectorizationBrain _brain = null!;
    private TestSparseTokenizer _sparseTokenizer = null!;
    private HttpHarnessFactory _factory = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _client = new QdrantClient("localhost", 6334);
        _store = new QdrantVectorStore(_client, NullLogger<QdrantVectorStore>.Instance);
        _brain = new TestVectorizationBrain(new Dictionary<string, float[]>
        {
            ["dense-query"] = V(1f, 0f, 0f, 0f),
            ["sparse-query"] = V(0f, 0f, 0f, 1f),
            ["summary-query"] = V(0f, 1f, 0f, 0f),
            ["twohop-query"] = V(0f, 0f, 1f, 0f)
        });
        _sparseTokenizer = new TestSparseTokenizer(new Dictionary<string, IReadOnlyList<SparseEntry>>
        {
            ["sparse-query"] = [new SparseEntry(17, 1f)]
        });

        await _store.EnsureCollectionAsync(_collection, Dimension, includeSummaryVector: true);
        await _store.UpsertManifestAsync(_collection, new CollectionManifest
        {
            CollectionName = _collection,
            ModelName = "fixture-model",
            ModelOnnxSha256 = "fixture-hash",
            EmbeddingDimension = Dimension,
            RequiredScopes = [Scope],
            Tenants = [TenantA, TenantB]
        });

        await SeedPointAsync(
            tenant: TenantA,
            relativePath: "alpha/Caller.cs",
            namespaceName: "Company.Alpha",
            content: "caller alpha",
            denseVector: V(1f, 0f, 3f, 0f),
            sparseVector: [new SparseEntry(17, 1f)],
            summaryVector: V(0f, 0.1f, 1f, 0f),
            definedSymbols: [],
            consumedSymbols: [SharedSymbol]);

        await SeedPointAsync(
            tenant: TenantA,
            relativePath: "alpha/Noise1.cs",
            namespaceName: "Company.Alpha",
            content: "alpha noise one",
            denseVector: V(0.95f, 0f, 0.8f, 0f),
            sparseVector: [],
            summaryVector: V(0f, 0.1f, 0.1f, 0f));

        await SeedPointAsync(
            tenant: TenantA,
            relativePath: "alpha/Noise2.cs",
            namespaceName: "Company.Alpha",
            content: "alpha noise two",
            denseVector: V(0.9f, 0f, 0.7f, 0f),
            sparseVector: [],
            summaryVector: V(0f, 0.1f, 0.1f, 0f));

        await SeedPointAsync(
            tenant: TenantA,
            relativePath: "alpha/SharedDependency.cs",
            namespaceName: "Company.Alpha",
            content: "allowed definition",
            denseVector: V(0.05f, 0f, 0.1f, 0f),
            sparseVector: [],
            summaryVector: V(0f, 0.92f, 0f, 0f),
            definedSymbols: [SharedSymbol],
            consumedSymbols: []);

        await SeedPointAsync(
            tenant: TenantA,
            relativePath: "beta/SharedDependency.cs",
            namespaceName: "Company.Beta",
            content: "same tenant foreign module",
            denseVector: V(0.95f, 0f, 1f, 0f),
            sparseVector: [],
            summaryVector: V(0f, 0.98f, 0f, 0f),
            definedSymbols: [SharedSymbol],
            consumedSymbols: []);

        await SeedPointAsync(
            tenant: TenantB,
            relativePath: "alpha/TenantB.cs",
            namespaceName: "Company.Alpha",
            content: "foreign tenant alpha",
            denseVector: V(1f, 0f, 0f, 0f),
            sparseVector: [new SparseEntry(17, 1f)],
            summaryVector: V(0f, 1f, 0f, 0f));

        await SeedPointAsync(
            tenant: TenantB,
            relativePath: "beta/TenantB.cs",
            namespaceName: "Company.Beta",
            content: "foreign tenant beta",
            denseVector: V(0.9f, 0f, 0f, 0f),
            sparseVector: [new SparseEntry(17, 1f)],
            summaryVector: V(0f, 1f, 0f, 0f),
            definedSymbols: [SharedSymbol],
            consumedSymbols: []);

        _factory = new HttpHarnessFactory(_brain, _sparseTokenizer);
        _http = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _factory.DisposeAsync();

        try
        {
            await _client.DeleteCollectionAsync(_collection);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task Busqueda_densa_aplica_tenant_y_modulo_desde_el_contexto()
    {
        var retriever = BuildRetriever(twoHopEnabled: false);

        var results = await retriever.SearchAsync("dense-query", AuthorizedOptions(ModuleAlpha));

        Assert.Contains(results, r => r.Metadata.RelativeFilePath == "alpha/Caller.cs");
        Assert.DoesNotContain(results, r => r.Metadata.RelativeFilePath == "beta/SharedDependency.cs");
        Assert.DoesNotContain(results, r => r.Metadata.RelativeFilePath == "alpha/TenantB.cs");
        Assert.All(results, AssertAuthorizedAlpha);
    }

    [Fact]
    public async Task Busqueda_sparse_aplica_tenant_y_modulo_desde_el_contexto()
    {
        var retriever = BuildRetriever(twoHopEnabled: false);

        var results = await retriever.SearchAsync("sparse-query", AuthorizedOptions(ModuleAlpha));

        Assert.Contains(results, r => r.Metadata.RelativeFilePath == "alpha/Caller.cs");
        Assert.DoesNotContain(results, r => r.Metadata.RelativeFilePath == "alpha/TenantB.cs");
        Assert.DoesNotContain(results, r => r.Metadata.RelativeFilePath == "beta/TenantB.cs");
        Assert.All(results, AssertAuthorizedAlpha);
    }

    [Fact]
    public async Task Busqueda_por_resumen_aplica_tenant_y_modulo_desde_el_contexto()
    {
        var retriever = BuildRetriever(twoHopEnabled: false);

        var results = await retriever.SearchAsync("summary-query", AuthorizedOptions(ModuleAlpha));

        Assert.Contains(results, r => r.Metadata.RelativeFilePath == "alpha/SharedDependency.cs");
        Assert.DoesNotContain(results, r => r.Metadata.RelativeFilePath == "beta/SharedDependency.cs");
        Assert.DoesNotContain(results, r => r.Metadata.RelativeFilePath == "alpha/TenantB.cs");
        Assert.All(results, AssertAuthorizedAlpha);
    }

    [Fact]
    public async Task Two_hop_no_reintroduce_puntos_no_autorizados()
    {
        var retriever = BuildRetriever(twoHopEnabled: true);

        var results = await retriever.SearchAsync("twohop-query", AuthorizedOptions(ModuleAlpha, topK: 2));

        Assert.Equal(
            ["alpha/Caller.cs", "alpha/SharedDependency.cs"],
            results.Select(r => r.Metadata.RelativeFilePath).ToArray());
        Assert.DoesNotContain(results, r => r.Metadata.RelativeFilePath == "beta/SharedDependency.cs");
        Assert.DoesNotContain(results, r => r.Metadata.RelativeFilePath == "beta/TenantB.cs");
    }

    [Fact]
    public async Task Api_search_devuelve_fuentes_filtradas_dentro_del_puerto()
    {
        using var response = await _http.SendAsync(TestActor.User(TenantA, ModuleAlpha).BuildSearchRequest(_collection, "dense-query"));
        response.EnsureSuccessStatusCode();

        var sources = await response.Content.ReadFromJsonAsync<List<SearchSource>>();

        Assert.NotNull(sources);
        Assert.NotEmpty(sources!);
        Assert.Contains(sources!, s => s.File == "alpha/Caller.cs");
        Assert.DoesNotContain(sources!, s => s.File == "beta/SharedDependency.cs");
        Assert.DoesNotContain(sources!, s => s.File == "alpha/TenantB.cs");
        Assert.All(sources!, s => Assert.StartsWith("alpha/", s.File));
    }

    private QdrantSemanticRetriever BuildRetriever(bool twoHopEnabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(_client);
        services.AddSingleton<IVectorizationBrain>(_brain);
        services.AddSingleton<ISparseTokenizer>(_sparseTokenizer);
        services.AddSingleton<IReRanker, PassthroughReRanker>();
        services.AddSingleton<QdrantVectorStore>(_store);
        services.AddSingleton<IVectorStoreAdmin>(_store);
        services.AddSingleton<IRetrievalProfileResolver, NullRetrievalProfileResolver>();
        services.AddSingleton<IOptions<RetrievalFusionOptions>>(Options.Create(new RetrievalFusionOptions()));
        services.AddSingleton<IOptions<TwoHopOptions>>(Options.Create(new TwoHopOptions
        {
            Enabled = twoHopEnabled,
            SeedResults = 1,
            MaxExpansionResults = 1,
            Weight = 0.8
        }));
        services.AddResiliencePipeline("qdrant", _ => { });

        var provider = services.BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<QdrantSemanticRetriever>(provider);
    }

    private RetrievalOptions AuthorizedOptions(string module, int topK = 5) => new()
    {
        Context = RetrievalContext.ForAuthorized(TenantA, module),
        CollectionName = _collection,
        TopK = topK,
        MinimumSimilarityScore = 0f,
        UseReRanking = false
    };

    private async Task SeedPointAsync(
        string tenant,
        string relativePath,
        string namespaceName,
        string content,
        float[] denseVector,
        IReadOnlyList<SparseEntry> sparseVector,
        float[] summaryVector,
        IReadOnlyList<string>? definedSymbols = null,
        IReadOnlyList<string>? consumedSymbols = null)
    {
        var chunk = new CodeChunk
        {
            Id = Guid.NewGuid(),
            Content = content,
            EnrichedContent = content,
            Metadata = new CodeChunkMetadata(
                FilePath: $"/repo/{relativePath}",
                RelativeFilePath: relativePath,
                Language: SourceLanguage.CSharp,
                Namespace: namespaceName,
                ClassName: Path.GetFileNameWithoutExtension(relativePath),
                MethodName: null,
                StartLine: 1,
                EndLine: 20,
                LastModified: DateTimeOffset.UtcNow,
                RepositoryName: "test-repo"),
            Type = ChunkType.Class,
            ContentHash = $"{tenant}:{relativePath}",
            DefinedSymbols = definedSymbols ?? Array.Empty<string>(),
            ConsumedSymbols = consumedSymbols ?? Array.Empty<string>()
        };

        var batch = new[]
        {
            new VectorStoreBatchItem(
                chunk,
                denseVector,
                sparseVector,
                new ExistingResumenState(ResumenPending: false, SummaryVector: summaryVector))
        };

        await _store.UpsertBatchAsync(
            _collection,
            batch,
            waitForCommit: true,
            markResumenPending: true,
            tenant: tenant);
    }

    private static void AssertAuthorizedAlpha(RetrievalResult result)
    {
        Assert.StartsWith("alpha/", result.Metadata.RelativeFilePath);
        Assert.Equal("Company.Alpha", result.Metadata.Namespace);
    }

    private static float[] V(params float[] values)
    {
        var length = MathF.Sqrt(values.Sum(v => v * v));
        return length == 0f ? values : values.Select(v => v / length).ToArray();
    }

    private sealed class TestVectorizationBrain(IReadOnlyDictionary<string, float[]> map) : IVectorizationBrain
    {
        public int EmbeddingDimensions => Dimension;
        public int MaxSequenceLength => 128;

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(map.TryGetValue(text, out var vector) ? vector : V(0f, 0f, 0f, 1f));

        public async Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
            IEnumerable<string> texts,
            CancellationToken cancellationToken = default) =>
            await Task.WhenAll(texts.Select(text => GenerateEmbeddingAsync(text, cancellationToken)));

        public async Task<VectorizationBatchResult> GenerateBatchEmbeddingsWithStatsAsync(
            IEnumerable<string> texts,
            CancellationToken cancellationToken = default)
        {
            var list = texts.ToList();
            var vectors = await GenerateBatchEmbeddingsAsync(list, cancellationToken);
            var stats = list.Select(_ => new TokenizationStats(1, MaxSequenceLength, 0, false)).ToArray();
            return new VectorizationBatchResult(vectors, stats);
        }
    }

    private sealed class TestSparseTokenizer(IReadOnlyDictionary<string, IReadOnlyList<SparseEntry>> map) : ISparseTokenizer
    {
        public IReadOnlyList<SparseEntry> Tokenize(string text) =>
            map.TryGetValue(text, out var vector) ? vector : Array.Empty<SparseEntry>();
    }

    private sealed class PassthroughReRanker : IReRanker
    {
        public Task<IReadOnlyList<RetrievalResult>> ReRankAsync(
            string query,
            IReadOnlyList<RetrievalResult> candidates,
            int topK,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RetrievalResult>>(candidates.Take(topK).ToList());
    }

    private sealed class NullRetrievalProfileResolver : IRetrievalProfileResolver
    {
        public RetrievalProfile? Resolve(CollectionManifest? manifest) => null;

        public Task<RetrievalProfile?> ResolveAsync(string collectionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<RetrievalProfile?>(null);
    }

    private sealed class HttpHarnessFactory(TestVectorizationBrain brain, TestSparseTokenizer tokenizer)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("RetrievalAuthorizationHarness");
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
                services.AddSingleton<IVectorizationBrain>(brain);
                services.AddSingleton<ISparseTokenizer>(tokenizer);
                services.AddSingleton<IReRanker, PassthroughReRanker>();
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

    private sealed record TestActor(string Tenant, string Module)
    {
        public static TestActor User(string tenant, string module) => new(tenant, module);

        public HttpRequestMessage BuildSearchRequest(string collection, string query)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/search")
            {
                Content = JsonContent.Create(new { query, collection, topK = 5, minScore = 0.0f, rerank = false })
            };

            request.Headers.Add(TestActorAuthHandler.AuthenticatedHeader, "1");
            request.Headers.Add(TestActorAuthHandler.ScopesHeader, Scope);
            request.Headers.Add(TestActorAuthHandler.TenantHeader, Tenant);
            request.Headers.Add(TestActorAuthHandler.ModuleHeader, Module);
            return request;
        }
    }

    private static class TestActorAuthHandler
    {
        public const string AuthenticatedHeader = "X-Test-Authenticated";
        public const string ScopesHeader = "X-Test-Scopes";
        public const string TenantHeader = "X-Test-Tenant";
        public const string ModuleHeader = "X-Test-Module";

        public static async Task Middleware(HttpContext context, Func<Task> next)
        {
            if (context.Request.Headers.TryGetValue(AuthenticatedHeader, out var authenticatedRaw)
                && authenticatedRaw == "1")
            {
                var claims = new List<Claim>();

                if (context.Request.Headers.TryGetValue(ScopesHeader, out var scopesRaw))
                {
                    foreach (var scope in scopesRaw.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        claims.Add(new Claim("scope", scope));
                }

                if (context.Request.Headers.TryGetValue(TenantHeader, out var tenantRaw)
                    && !string.IsNullOrWhiteSpace(tenantRaw))
                {
                    claims.Add(new Claim("tenant", tenantRaw!));
                }

                if (context.Request.Headers.TryGetValue(ModuleHeader, out var moduleRaw)
                    && !string.IsNullOrWhiteSpace(moduleRaw))
                {
                    claims.Add(new Claim("module", moduleRaw!));
                }

                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "RetrievalAuthorizationHarness"));
            }

            await next();
        }
    }

    private sealed record SearchSource(string File, int StartLine, int EndLine, float Score);
}
