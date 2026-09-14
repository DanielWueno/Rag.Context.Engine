using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Cubre el ítem 5.f.1-manifest-schema-y-persistencia-basica: persistencia/roundtrip del
/// <see cref="CollectionManifest"/> extendido (Profile, RequiredScopes, Tenants) como point
/// reservado en Qdrant, y el error accionable ante un hash ONNX incompatible.
///
/// Integración real contra el Qdrant local del entorno de desarrollo (localhost:6334, el
/// mismo contenedor "qdrant-local" que usa `rag-api`) — no hay fake de Qdrant en este
/// repositorio y montar uno solo para esto sería más frágil que usar el servidor real.
/// Cada test crea y borra su PROPIA colección aislada (nombre único por ejecución,
/// prefijo "rag-engine-test-manifest-"), nunca toca "rag-engine" ni ninguna colección
/// servida por rag-api — ver AGENTS.md "No tocar colecciones servidas por una prueba".
///
/// Si Qdrant no está accesible en localhost:6334, los tests fallan con el error de
/// conexión real (no se marcan skip): la ausencia de infraestructura no debe disfrazarse
/// de éxito.
/// </summary>
public sealed class CollectionManifestPersistenceTests : IAsyncLifetime
{
    private const int TestDimension = 8;
    private readonly string _collectionName = $"rag-engine-test-manifest-{Guid.NewGuid():N}";
    private QdrantClient _client = null!;
    private QdrantVectorStore _store = null!;

    public async Task InitializeAsync()
    {
        _client = new QdrantClient("localhost", 6334);
        _store = new QdrantVectorStore(_client, NullLogger<QdrantVectorStore>.Instance);
        await _store.EnsureCollectionAsync(_collectionName, TestDimension);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _client.DeleteCollectionAsync(_collectionName);
        }
        catch
        {
            // best-effort cleanup; no ocultar el resultado del test por un fallo de limpieza
        }
    }

    [Fact]
    public async Task Roundtrip_conserva_profile_scopes_y_multiples_tenants()
    {
        var manifest = new CollectionManifest
        {
            CollectionName = _collectionName,
            ModelName = "paraphrase-multilingual-MiniLM-L12-v2",
            ModelOnnxSha256 = "abc123def456",
            EmbeddingDimension = TestDimension,
            TotalChunks = 42,
            Profile = "codigo-estricto",
            RequiredScopes = new[] { "rag.read.sistema" },
            Tenants = new[] { "tenant-a", "tenant-b", "tenant-c" }
        };

        await _store.UpsertManifestAsync(_collectionName, manifest);
        var read = await _store.GetManifestAsync(_collectionName);

        Assert.NotNull(read);
        Assert.Equal(manifest.CollectionName, read!.CollectionName);
        Assert.Equal(manifest.ModelName, read.ModelName);
        Assert.Equal(manifest.ModelOnnxSha256, read.ModelOnnxSha256);
        Assert.Equal(manifest.EmbeddingDimension, read.EmbeddingDimension);
        Assert.Equal(manifest.TotalChunks, read.TotalChunks);
        Assert.Equal(manifest.Profile, read.Profile);
        Assert.Equal(manifest.RequiredScopes, read.RequiredScopes);
        Assert.Equal(manifest.Tenants, read.Tenants);
    }

    [Fact]
    public async Task GetManifestAsync_devuelve_null_cuando_no_hay_manifiesto()
    {
        var read = await _store.GetManifestAsync(_collectionName);
        Assert.Null(read);
    }

    [Fact]
    public async Task EnsureModelCompatibleAsync_no_lanza_cuando_no_hay_manifiesto_previo()
    {
        // Primera ingesta: nada contra qué validar todavía.
        await _store.EnsureModelCompatibleAsync(_collectionName, "hash-del-modelo-actual");
    }

    [Fact]
    public async Task EnsureModelCompatibleAsync_no_lanza_cuando_el_hash_coincide()
    {
        await _store.UpsertManifestAsync(_collectionName, new CollectionManifest
        {
            CollectionName = _collectionName,
            ModelName = "modelo-x",
            ModelOnnxSha256 = "hash-identico",
            EmbeddingDimension = TestDimension
        });

        await _store.EnsureModelCompatibleAsync(_collectionName, "hash-identico");
    }

    [Fact]
    public async Task EnsureModelCompatibleAsync_lanza_error_accionable_con_hash_esperado_y_encontrado_ante_mismatch()
    {
        await _store.UpsertManifestAsync(_collectionName, new CollectionManifest
        {
            CollectionName = _collectionName,
            ModelName = "modelo-viejo",
            ModelOnnxSha256 = "hash-encontrado-guardado",
            EmbeddingDimension = TestDimension
        });

        var ex = await Assert.ThrowsAsync<ManifestModelMismatchException>(() =>
            _store.EnsureModelCompatibleAsync(_collectionName, "hash-esperado-actual"));

        Assert.Equal("hash-esperado-actual", ex.ExpectedModelOnnxSha256);
        Assert.Equal("hash-encontrado-guardado", ex.FoundModelOnnxSha256);
        Assert.Contains("hash-esperado-actual", ex.Message);
        Assert.Contains("hash-encontrado-guardado", ex.Message);
    }

    [Fact]
    public async Task Manifiesto_antiguo_sin_scopes_ni_tenants_deserializa_como_no_publicado()
    {
        // Simula un manifiesto escrito antes de este desglose (sin Profile/RequiredScopes/Tenants
        // en el JSON), verificando que FromJson no falla y que el resultado nunca publica por
        // defecto — vacío significa "solo administrador".
        var legacyJson = """
            {
              "CollectionName": "coleccion-vieja",
              "ModelName": "modelo-viejo",
              "ModelOnnxSha256": "hash-viejo",
              "EmbeddingDimension": 384,
              "CreatedAt": "2026-01-01T00:00:00Z",
              "LastIndexedAt": "2026-01-01T00:00:00Z",
              "TotalChunks": 10
            }
            """;

        var manifest = CollectionManifest.FromJson(legacyJson);

        Assert.Empty(manifest.RequiredScopes);
        Assert.Empty(manifest.Tenants);
        Assert.Null(manifest.Profile);
    }
}
