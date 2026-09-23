using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

public interface IVectorStoreAdmin
{
    /// <summary>
    /// Ítem 9.2: nombres de todas las colecciones existentes en el backend. Sirve tanto
    /// como listado real (p. ej. `/api/collections`) como ping de conectividad barato
    /// (p. ej. `/api/health`, `rag doctor`) — quien solo necesita confirmar que el
    /// backend responde puede descartar el resultado.
    /// </summary>
    Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Ítem 9.2: salud y tamaño de una colección (equivalente a `rag status`). Lanza si
    /// la colección no existe o no se pudo consultar — igual que antes, cuando los
    /// hosts llamaban directamente al cliente de infraestructura.
    /// </summary>
    Task<CollectionHealthReport> GetCollectionHealthAsync(string collectionName, CancellationToken ct = default);

    Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default);

    Task EnsureCollectionAsync(
        string collectionName,
        int dimension,
        bool includeSummaryVector = false,
        CancellationToken ct = default);

    Task RecreateCollectionAsync(
        string collectionName,
        int dimension,
        bool includeSummaryVector = false,
        CancellationToken ct = default);

    Task<bool> HasSummaryVectorAsync(string collectionName, CancellationToken ct = default);

    Task<bool> HasDefinedSymbolsIndexAsync(string collectionName, CancellationToken ct = default);

    Task<IReadOnlyList<CollectionSchemaReport>> InspectCollectionSchemasAsync(
        int? expectedDimension,
        CancellationToken ct = default);

    Task UpsertManifestAsync(
        string collectionName,
        CollectionManifest manifest,
        CancellationToken ct = default);

    Task<CollectionManifest?> GetManifestAsync(string collectionName, CancellationToken ct = default);

    Task EnsureModelCompatibleAsync(
        string collectionName,
        string expectedModelOnnxSha256,
        CancellationToken ct = default);
}
