using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Cubre el ítem 5.e-salto-de-contrato-y-rebaseline (absorbe 12.7-tenant-en-el-payload):
/// tenant explícito por punto en el payload y su filtro en la búsqueda, sin exigir IDP
/// ni autenticación corporativa — identidad local declarada por el operador al ingestar.
///
/// Integración real contra el Qdrant local del entorno de desarrollo (localhost:6334, el
/// mismo contenedor "qdrant-local" que usa `rag-api`) — ver AGENTS.md "No tocar
/// colecciones servidas por una prueba". Cada test crea y borra su PROPIA colección
/// aislada (prefijo "rag-engine-test-tenant-"), y usa vectores ficticios de baja
/// dimensión: el objetivo es verificar el payload y el filtro, no la calidad semántica.
///
/// Escenarios exigidos por la ficha:
///   - Puntos con tenant explícito: el filtro por ese tenant devuelve solo esos puntos.
///   - Colección compartida/mixta: tenants distintos conviven en la misma colección y el
///     filtro aísla cada uno sin fuga cruzada.
///   - Fuente sin mapeo (tenant null): el punto se indexa igual, sin la clave de payload,
///     visible sin filtro pero excluido de cualquier filtro con tenant explícito — igual
///     invariante que CollectionManifest.Tenants vacío ("sin restricción" nunca "sin
///     acceso", pero un valor concreto exige coincidencia exacta).
/// </summary>
public sealed class TenantPayloadFilterTests : IAsyncLifetime
{
    private const int TestDimension = 8;
    private readonly string _collectionName = $"rag-engine-test-tenant-{Guid.NewGuid():N}";
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

    private static CodeChunk MakeChunk(string relativePath, string content) => new()
    {
        Id = Guid.NewGuid(),
        Content = content,
        EnrichedContent = content,
        Metadata = new CodeChunkMetadata(
            FilePath: $"/repo/{relativePath}",
            RelativeFilePath: relativePath,
            Language: SourceLanguage.CSharp,
            Namespace: null,
            ClassName: null,
            MethodName: null,
            StartLine: 1,
            EndLine: 1,
            LastModified: DateTimeOffset.UtcNow,
            RepositoryName: "test-repo"),
        Type = ChunkType.Method,
        ContentHash = content.GetHashCode().ToString()
    };

    private static (float[] Dense, IReadOnlyList<SparseEntry> Sparse) DummyVectors()
        => (new float[TestDimension], Array.Empty<SparseEntry>());

    private async Task UpsertWithTenantAsync(string relativePath, string content, string? tenant)
    {
        var chunk = MakeChunk(relativePath, content);
        var (dense, sparse) = DummyVectors();
        var batch = new List<(CodeChunk, float[], IReadOnlyList<SparseEntry>, QdrantVectorStore.ExistingResumenState?)>
        {
            (chunk, dense, sparse, null)
        };
        await _store.UpsertBatchAsync(_collectionName, batch, waitForCommit: true, tenant: tenant);
    }

    private async Task<HashSet<string>> ScrollRelativePathsAsync(Filter? filter)
    {
        var response = await _client.ScrollAsync(
            _collectionName,
            filter: filter,
            limit: 100,
            payloadSelector: new WithPayloadSelector { Enable = true },
            vectorsSelector: new WithVectorsSelector { Enable = false });

        return response.Result
            .Select(p => p.Payload["relative_path"].StringValue)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Filter TenantFilter(string tenant) => new()
    {
        Must =
        {
            new Condition
            {
                Field = new FieldCondition
                {
                    Key = QdrantVectorStore.TenantPayloadKey,
                    Match = new Match { Keyword = tenant }
                }
            }
        }
    };

    [Fact]
    public async Task Punto_con_tenant_explicito_solo_aparece_en_filtro_de_ese_tenant()
    {
        await UpsertWithTenantAsync("a.cs", "contenido-a", tenant: "tenant-a");
        await UpsertWithTenantAsync("b.cs", "contenido-b", tenant: "tenant-b");

        var resultadoA = await ScrollRelativePathsAsync(TenantFilter("tenant-a"));
        var resultadoB = await ScrollRelativePathsAsync(TenantFilter("tenant-b"));

        Assert.Equal(new HashSet<string> { "a.cs" }, resultadoA);
        Assert.Equal(new HashSet<string> { "b.cs" }, resultadoB);
    }

    [Fact]
    public async Task Coleccion_compartida_mixta_no_filtra_por_defecto_pero_aisla_por_tenant()
    {
        await UpsertWithTenantAsync("a.cs", "contenido-a", tenant: "tenant-a");
        await UpsertWithTenantAsync("b.cs", "contenido-b", tenant: "tenant-b");
        await UpsertWithTenantAsync("c.cs", "contenido-c", tenant: null);

        var sinFiltro = await ScrollRelativePathsAsync(filter: null);
        var soloA = await ScrollRelativePathsAsync(TenantFilter("tenant-a"));

        Assert.Equal(new HashSet<string> { "a.cs", "b.cs", "c.cs" }, sinFiltro);
        Assert.Equal(new HashSet<string> { "a.cs" }, soloA);
    }

    [Fact]
    public async Task Fuente_sin_mapeo_no_escribe_clave_de_payload_y_queda_fuera_de_cualquier_filtro_de_tenant()
    {
        await UpsertWithTenantAsync("sin-mapeo.cs", "contenido-libre", tenant: null);

        var response = await _client.ScrollAsync(
            _collectionName,
            limit: 10,
            payloadSelector: new WithPayloadSelector { Enable = true },
            vectorsSelector: new WithVectorsSelector { Enable = false });

        var punto = Assert.Single(response.Result);
        Assert.False(punto.Payload.ContainsKey(QdrantVectorStore.TenantPayloadKey));

        var filtradoPorCualquierTenant = await ScrollRelativePathsAsync(TenantFilter("tenant-cualquiera"));
        Assert.Empty(filtradoPorCualquierTenant);
    }
}
