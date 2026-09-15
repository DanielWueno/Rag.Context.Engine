using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Cubre el criterio literal de 7.c-rerank-no-por-defecto: sin <c>rerank</c> explícito
/// en la request NI perfil de colección que lo declare, <c>/api/search</c> ya NO debe
/// comportarse como si <c>rerank=true</c> — antes de este ítem
/// (<c>request.Rerank ?? profile?.UseReRanking ?? true</c>) ese era el default
/// silencioso; ahora cae en <c>false</c>.
///
/// Estrategia de fixture (sin depender de que el cross-encoder ordene "bien" nada,
/// como <see cref="RetrievalProfileHttpHarnessTests"/>): la señal que se mide no es el
/// ranking, es la ESCALA del score devuelto (ítem 4.9,
/// <see cref="RetrievalScoreScale"/>). Sin rerank, <c>QdrantSemanticRetriever</c> deja
/// el score de fusión RRF nativo de Qdrant — con un solo prefetch denso que matchea (la
/// colección no tiene vector disperso indexado, así que la rama dispersa no aporta
/// candidatos) el score exacto y determinista es <c>1/(rank+1)</c> con rank 0-based
/// (verificado empíricamente contra Qdrant real, ver <see cref="ExpectedRrfScores"/>).
/// Con rerank, el score pasa a ser el sigmoide del cross-encoder — un número que NO
/// coincide con esa fórmula — sin importar si el contenido es o no relevante a la query.
///
/// Integración real contra Qdrant local (localhost:6333/6334), el modelo ONNX de
/// embeddings y el cross-encoder ya descargados — mismo requisito que
/// <see cref="RetrievalProfileHttpHarnessTests"/>. La colección nunca declara
/// manifiesto ni perfil: es exactamente el camino "sin perfil" que decide este ítem.
/// </summary>
public sealed class RerankDefaultHttpHarnessTests : IAsyncLifetime
{
    private const int EmbeddingDimension = 384; // debe igualar OnnxBrain:EmbeddingDimensions de appsettings.json
    private const string Query = "¿qué política aplica el rerank por defecto de esta colección?";

    // Los 3 puntos sembrados comparten el mismo vector denso (el embedding real de la
    // query) y NO tienen vector disperso indexado (SparseEntry vacío al upsert), así
    // que el prefetch disperso no aporta candidatos: la fusión RRF nativa de Qdrant
    // queda reducida a una sola rama. Verificado empíricamente (no documentado por
    // Qdrant): esa rama puntúa <c>1/(rank+1)</c> con rank 0-based — NO usa
    // <see cref="RetrievalFusionOptions.RrfK"/> (ese k sólo aplica a la fusión
    // ponderada manual de colecciones con vector dense-resumen, ver
    // QdrantSemanticRetriever.cs:360). Con 3 puntos, el conjunto exacto y determinista
    // de scores sin rerank es {0.5, 1/3, 0.25} — cero dependencia del modelo de
    // embeddings o del cross-encoder.
    private static readonly float[] ExpectedRrfScores = [0.5f, 1f / 3f, 0.25f];
    private const float Tolerance = 0.001f;

    private readonly string _collection = $"rag-engine-test-rerank-default-{Guid.NewGuid():N}";

    private QdrantClient _client = null!;
    private QdrantVectorStore _store = null!;
    private RerankDefaultWebApplicationFactory _factory = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _client = new QdrantClient("localhost", 6334);
        _store = new QdrantVectorStore(_client, NullLogger<QdrantVectorStore>.Instance);
        _factory = new RerankDefaultWebApplicationFactory();
        _http = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var brain = scope.ServiceProvider.GetRequiredService<IVectorizationBrain>();
        var queryVector = await brain.GenerateEmbeddingAsync(Query, CancellationToken.None);

        await _store.EnsureCollectionAsync(_collection, EmbeddingDimension);
        // Deliberadamente SIN manifiesto: GetManifestAsync devuelve null, el resolver de
        // perfil resuelve null, y el endpoint debe caer en el default de rerank vigente.
        await SeedPointsAsync(_collection, queryVector);
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
            // best-effort cleanup; no ocultar el resultado del test por un fallo de limpieza
        }
    }

    private async Task SeedPointsAsync(string collectionName, float[] queryVector)
    {
        var batch = new List<(CodeChunk, float[], IReadOnlyList<SparseEntry>, QdrantVectorStore.ExistingResumenState?)>();
        for (int i = 0; i < 3; i++)
        {
            var chunk = new CodeChunk
            {
                Id = Guid.NewGuid(),
                Content = $"El rerank por defecto de la colección {i}.",
                EnrichedContent = $"El rerank por defecto de la colección {i}.",
                Metadata = new CodeChunkMetadata(
                    FilePath: $"/repo/chunk-{i}.md",
                    RelativeFilePath: $"chunk-{i}.md",
                    Language: SourceLanguage.Markdown,
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
            batch.Add((chunk, queryVector, Array.Empty<SparseEntry>(), null));
        }
        await _store.UpsertBatchAsync(collectionName, batch, waitForCommit: true);
    }

    private HttpRequestMessage BuildSearchRequest(bool? rerank) =>
        new(HttpMethod.Post, "/api/search")
        {
            Content = JsonContent.Create(new { query = Query, collection = _collection, rerank })
        };

    [Fact]
    public async Task Sin_rerank_explicito_ni_perfil_el_score_es_de_escala_rrf_no_cross_encoder()
    {
        using var response = await _http.SendAsync(BuildSearchRequest(rerank: null));
        response.EnsureSuccessStatusCode();

        var sources = await response.Content.ReadFromJsonAsync<List<SearchSource>>();

        Assert.NotNull(sources);
        // Antes de 7.c este score hubiera sido el sigmoide del cross-encoder (default
        // silencioso rerank=true). Ahora, sin rerank explícito ni perfil, debe seguir
        // siendo EXACTAMENTE el RRF nativo de Qdrant de una sola rama: {0.5, 1/3, 0.25}.
        var scores = sources!.Select(s => s.Score).OrderByDescending(s => s).ToArray();
        Assert.Equal(ExpectedRrfScores.Length, scores.Length);
        for (int i = 0; i < ExpectedRrfScores.Length; i++)
        {
            Assert.True(Math.Abs(scores[i] - ExpectedRrfScores[i]) < Tolerance,
                $"Score #{i} esperado {ExpectedRrfScores[i]} (RRF nativo), llegó {scores[i]} — " +
                "indicio de que el rerank se sigue aplicando por default sin pedirlo.");
        }
    }

    [Fact]
    public async Task Con_rerank_explicito_true_el_score_es_del_cross_encoder_no_rrf()
    {
        using var response = await _http.SendAsync(BuildSearchRequest(rerank: true));
        response.EnsureSuccessStatusCode();

        var sources = await response.Content.ReadFromJsonAsync<List<SearchSource>>();

        Assert.NotNull(sources);
        Assert.NotEmpty(sources!);
        // Pedido explícitamente, el rerank SIGUE disponible — 7.c sólo quita el default
        // silencioso, no la capacidad. El score cross-encoder es un sigmoide que NO
        // coincide con ninguno de los valores exactos y deterministas de la fusión RRF.
        var topScore = sources![0].Score;
        Assert.All(ExpectedRrfScores, rrfScore =>
            Assert.True(Math.Abs(topScore - rrfScore) >= Tolerance,
                $"El score del ganador ({topScore}) coincide con un valor RRF ({rrfScore}) " +
                "al pedir rerank=true explícitamente — el rerank no se está aplicando."));
    }

    /// <summary>Subconjunto mínimo del payload de <c>/api/search</c> necesario para leer el score.</summary>
    private sealed record SearchSource(string File, int StartLine, int EndLine, float Score);

    /// <summary>
    /// Host de prueba sobre el Program.cs real de la API: Authorization:Mode se deja en
    /// el default (Local) de appsettings.json — todo actor es administrador implícito.
    /// No se registra ningún perfil: el catálogo queda vacío a propósito.
    /// </summary>
    private sealed class RerankDefaultWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("RerankDefaultHarness");
    }
}
