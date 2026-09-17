using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

public interface IVectorStoreAdmin
{
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
