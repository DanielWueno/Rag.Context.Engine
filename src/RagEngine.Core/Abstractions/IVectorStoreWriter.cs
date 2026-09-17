using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

public interface IVectorStoreWriter
{
    Task<IReadOnlyDictionary<Guid, ExistingResumenState>> GetExistingResumenStateAsync(
        string collectionName,
        IReadOnlyList<Guid> chunkIds,
        CancellationToken ct = default);

    Task<int> UpsertBatchAsync(
        string collectionName,
        IReadOnlyList<VectorStoreBatchItem> batch,
        bool waitForCommit = true,
        bool markResumenPending = false,
        string? tenant = null,
        CancellationToken ct = default);

    Task<int> DeleteSupersededPointsAsync(
        string collectionName,
        IReadOnlySet<Guid> currentChunkIds,
        IReadOnlySet<string> processedFilePaths,
        CancellationToken ct = default);

    IAsyncEnumerable<PendingResumenPoint> StreamPendingResumenAsync(
        string collectionName,
        uint pageSize = 100,
        CancellationToken ct = default);

    Task UpdateSummaryVectorAsync(
        string collectionName,
        Guid pointId,
        float[] summaryVector,
        CancellationToken ct = default);

    Task MarkResumenCompleteAsync(
        string collectionName,
        IReadOnlyList<Guid> pointIds,
        CancellationToken ct = default);

    Task<ulong> CountResumenPendingAsync(string collectionName, CancellationToken ct = default);
}
