using RagEngine.Core.Abstractions;

namespace RagEngine.Core.Domain;

/// <summary>
/// Estado de resumen que ya tenía un chunk antes de un re-upsert.
/// </summary>
public sealed record ExistingResumenState(bool ResumenPending, float[]? SummaryVector);

/// <summary>
/// Un item de escritura de Fase 1: chunk + vectores densos/dispersos y, si existe,
/// el estado previo del resumen que debe preservarse.
/// </summary>
public sealed record VectorStoreBatchItem(
    CodeChunk Chunk,
    float[] DenseVector,
    IReadOnlyList<SparseEntry> SparseVector,
    ExistingResumenState? ExistingResumen);

/// <summary>
/// Punto pendiente de resumen reconstruido desde el payload ya persistido.
/// </summary>
public sealed record PendingResumenPoint(Guid PointId, CodeChunk Chunk);
