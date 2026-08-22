using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.VectorStore;

/// <summary>
/// Wraps QdrantClient to provide collection management and bulk upsert operations.
/// Uses the gRPC client for lower latency on bulk operations.
/// All upsert operations are idempotent (Upsert, not Insert).
/// </summary>
public sealed class QdrantVectorStore
{
    public const string DenseVectorName = "dense";
    public const string SparseVectorName = "sparse-code";

    /// <summary>Tercer vector, opt-in por colección: embedding del resumen de negocio generado por LLM.</summary>
    public const string SummaryVectorName = "dense-resumen";

    /// <summary>
    /// Payload booleano: true mientras el punto todavía no tiene <see cref="SummaryVectorName"/>
    /// poblado. Es la marca de trabajo pendiente que permite reanudar la Fase 2 de ingesta
    /// (resumen de negocio) sin reprocesar toda la colección — ver <c>DefaultIngestionPipeline</c>.
    /// </summary>
    public const string ResumenPendingPayloadKey = "resumen_pending";

    private static readonly TimeSpan SchemaCacheTtl = TimeSpan.FromMinutes(5);

    private readonly QdrantClient _client;
    private readonly ILogger<QdrantVectorStore> _logger;

    // Decisión 1/6/6a: el schema real de Qdrant es la única fuente de verdad de si una
    // colección tiene el tercer vector. Este caché en memoria evita pagar un
    // GetCollectionInfoAsync por cada búsqueda; se invalida explícitamente (no solo por
    // TTL) al recrear/asegurar una colección desde este mismo proceso.
    private readonly ConcurrentDictionary<string, (bool HasSummaryVector, DateTimeOffset CheckedAt)> _schemaCache = new();

    public QdrantVectorStore(QdrantClient client, ILogger<QdrantVectorStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Existencia simple de la colección — usado para decidir si hace falta reanudar (decisión 3a).</summary>
    public async Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default)
    {
        var collections = await _client.ListCollectionsAsync(ct);
        return collections.Contains(collectionName);
    }

    /// <summary>
    /// Creates the Qdrant collection if it does not already exist.
    /// Configures dense (cosine), sparse (TF), and — opt-in — un tercer vector denso
    /// de resumen de negocio.
    /// </summary>
    public async Task EnsureCollectionAsync(
        string collectionName,
        int dimension,
        bool includeSummaryVector = false,
        CancellationToken ct = default)
    {
        var collections = await _client.ListCollectionsAsync(ct);
        if (collections.Contains(collectionName))
        {
            _logger.LogInformation("Collection '{Name}' already exists.", collectionName);
            InvalidateSchemaCache(collectionName);
            return;
        }

        await CreateHybridCollectionAsync(collectionName, dimension, includeSummaryVector, ct);
        InvalidateSchemaCache(collectionName);
    }

    private async Task CreateHybridCollectionAsync(
        string collectionName,
        int dimension,
        bool includeSummaryVector,
        CancellationToken ct)
    {
        var vectorsConfig = new VectorParamsMap();
        vectorsConfig.Map[DenseVectorName] = new VectorParams
        {
            Size = (ulong)dimension,
            Distance = Distance.Cosine,
            OnDisk = false
        };
        if (includeSummaryVector)
        {
            vectorsConfig.Map[SummaryVectorName] = new VectorParams
            {
                Size = (ulong)dimension,
                Distance = Distance.Cosine,
                OnDisk = false
            };
        }

        var sparseConfig = new SparseVectorConfig();
        sparseConfig.Map[SparseVectorName] = new SparseVectorParams
        {
            Index = new SparseIndexConfig { FullScanThreshold = 5000 }
        };

        await _client.CreateCollectionAsync(
            collectionName,
            vectorsConfig: vectorsConfig,
            sparseVectorsConfig: sparseConfig,
            cancellationToken: ct);

        _logger.LogInformation(
            "Created Qdrant hybrid collection '{Name}' with {Dim}D dense{Summary} + sparse vectors.",
            collectionName, dimension, includeSummaryVector ? " + dense-resumen" : "");
    }

    /// <summary>
    /// Drops and recreates the collection (used with ForceReindex flag).
    /// </summary>
    public async Task RecreateCollectionAsync(
        string collectionName,
        int dimension,
        bool includeSummaryVector = false,
        CancellationToken ct = default)
    {
        var collections = await _client.ListCollectionsAsync(ct);
        if (collections.Contains(collectionName))
        {
            await _client.DeleteCollectionAsync(collectionName, cancellationToken: ct);
            _logger.LogInformation("Deleted existing collection '{Name}'.", collectionName);
        }

        await CreateHybridCollectionAsync(collectionName, dimension, includeSummaryVector, ct);
        InvalidateSchemaCache(collectionName);
    }

    /// <summary>
    /// Decisión 1: única fuente de verdad de si una colección tiene el tercer vector de
    /// resumen — se decide inspeccionando el schema real de Qdrant, no un flag externo.
    /// Cacheado en memoria con TTL corto (decisión 6a); la ventana de staleness
    /// cruzada entre procesos queda acotada a ese TTL y es comportamiento aceptado.
    /// </summary>
    public async Task<bool> HasSummaryVectorAsync(string collectionName, CancellationToken ct = default)
    {
        if (_schemaCache.TryGetValue(collectionName, out var cached) &&
            DateTimeOffset.UtcNow - cached.CheckedAt < SchemaCacheTtl)
        {
            return cached.HasSummaryVector;
        }

        var info = await _client.GetCollectionInfoAsync(collectionName, ct);
        var vectorsConfig = info.Config?.Params?.VectorsConfig;
        var has = vectorsConfig?.ConfigCase == VectorsConfig.ConfigOneofCase.ParamsMap
                   && vectorsConfig.ParamsMap.Map.ContainsKey(SummaryVectorName);

        _schemaCache[collectionName] = (has, DateTimeOffset.UtcNow);
        return has;
    }

    private void InvalidateSchemaCache(string collectionName) => _schemaCache.TryRemove(collectionName, out _);

    /// <summary>
    /// Estado de resumen que YA existía para un chunk antes de este upsert — null significa
    /// "punto nunca visto" (necesita resumen). Se usa para que re-ingestar un repo (archivos
    /// nuevos o cambiados, algo que SIEMPRE va a pasar en un repo de código real) nunca
    /// destruya el trabajo de resumen ya hecho: Qdrant hace upsert por REEMPLAZO COMPLETO de
    /// vectores y payload (verificado empíricamente), así que un re-upsert que solo incluya
    /// dense+sparse borraría el vector dense-resumen si no se reincluye explícitamente aquí.
    /// </summary>
    public sealed record ExistingResumenState(bool ResumenPending, float[]? SummaryVector);

    /// <summary>
    /// Busca, para un lote de IDs, el estado de resumen que ya tenían antes de este upsert
    /// (si existían). Un solo round-trip por lote — se llama antes de <see cref="UpsertBatchAsync"/>
    /// cuando la colección tiene resumen habilitado.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, ExistingResumenState>> GetExistingResumenStateAsync(
        string collectionName,
        IReadOnlyList<Guid> chunkIds,
        CancellationToken ct = default)
    {
        if (chunkIds.Count == 0) return new Dictionary<Guid, ExistingResumenState>();

        var ids = chunkIds.Select(id => new PointId { Uuid = id.ToString() }).ToList();
        var points = await _client.RetrieveAsync(collectionName, ids, withPayload: true, withVectors: true, cancellationToken: ct);

        var result = new Dictionary<Guid, ExistingResumenState>(points.Count);
        foreach (var point in points)
        {
            var pending = point.Payload.TryGetValue(ResumenPendingPayloadKey, out var v) && v.BoolValue;
            float[]? summaryVector = null;
            if (point.Vectors.VectorsOptionsCase == VectorsOutput.VectorsOptionsOneofCase.Vectors &&
                point.Vectors.Vectors.Vectors.TryGetValue(SummaryVectorName, out var vecOutput))
            {
                // Qdrant >= 1.14 entrega el denso en el oneof `dense`; el campo plano
                // `Data` quedó vacío por compatibilidad. Leerlo directo devolvía
                // float[0] (no null), y ese vector vacío se reenviaba en el upsert:
                // Qdrant rechazaba el punto ("dense vector must not be empty") y,
                // como el upsert es atómico, se perdía el lote entero.
                var data = vecOutput.VectorCase == VectorOutput.VectorOneofCase.Dense
                    ? vecOutput.Dense.Data
                    : vecOutput.Data;
                summaryVector = data.Count > 0 ? data.ToArray() : null;
            }

            result[Guid.Parse(point.Id.Uuid)] = new ExistingResumenState(pending, summaryVector);
        }
        return result;
    }

    /// <summary>
    /// Upserts a batch of points containing dense and sparse vectors (Fase 1 de ingesta).
    /// </summary>
    /// <param name="waitForCommit">
    /// true (default): bloquea hasta que Qdrant aplica la operación a los índices.
    /// false: retorna al persistirse en el WAL (status "acknowledged") — la
    /// durabilidad se conserva, solo se difiere la aplicación al segmento.
    /// Recomendado para ingesta masiva, donde la latencia de aplicación de los
    /// índices dual (HNSW + invertido disperso) saldría de la ruta crítica.
    /// </param>
    /// <param name="markResumenPending">
    /// true cuando la colección tiene el tercer vector habilitado. Por cada punto: si
    /// <c>ExistingResumen</c> viene poblado (el chunk ya existía), se preserva su estado
    /// (vector de resumen incluido, si lo tenía) en vez de resetearlo — así una re-ingesta
    /// (archivos nuevos o modificados) nunca pierde resúmenes ya generados. Si es null
    /// (punto nunca visto), se marca <see cref="ResumenPendingPayloadKey"/>=true.
    /// </param>
    public async Task<int> UpsertBatchAsync(
        string collectionName,
        IReadOnlyList<(CodeChunk Chunk, float[] DenseVector, IReadOnlyList<SparseEntry> SparseVector, ExistingResumenState? ExistingResumen)> batch,
        bool waitForCommit = true,
        bool markResumenPending = false,
        CancellationToken ct = default)
    {
        if (batch.Count == 0) return 0;

        var points = batch.Select(item =>
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = item.Chunk.Id.ToString() }
            };

            var denseVec = new Vector();
            denseVec.Data.AddRange(item.DenseVector);

            float[] sparseValues = new float[item.SparseVector.Count];
            uint[] sparseIndices = new uint[item.SparseVector.Count];
            for (int i = 0; i < item.SparseVector.Count; i++)
            {
                sparseIndices[i] = item.SparseVector[i].TermIndex;
                sparseValues[i] = item.SparseVector[i].Weight;
            }

            Vector sparseVec = (sparseValues, sparseIndices);

            var namedVectors = new NamedVectors();
            namedVectors.Vectors[DenseVectorName] = denseVec;
            namedVectors.Vectors[SparseVectorName] = sparseVec;

            if (markResumenPending && item.ExistingResumen?.SummaryVector is { Length: > 0 } existingVec)
                namedVectors.Vectors[SummaryVectorName] = existingVec; // preservar: nunca perder un resumen ya generado

            point.Vectors = new Vectors { Vectors_ = namedVectors };

            point.Payload["content"]          = new Value { StringValue = item.Chunk.Content };
            point.Payload["enriched_content"]  = new Value { StringValue = item.Chunk.EnrichedContent };
            point.Payload["file_path"]         = new Value { StringValue = item.Chunk.Metadata.FilePath };
            point.Payload["relative_path"]     = new Value { StringValue = item.Chunk.Metadata.RelativeFilePath };
            point.Payload["language"]          = new Value { StringValue = item.Chunk.Metadata.Language.ToString() };
            point.Payload["start_line"]        = new Value { IntegerValue = item.Chunk.Metadata.StartLine };
            point.Payload["end_line"]          = new Value { IntegerValue = item.Chunk.Metadata.EndLine };
            point.Payload["chunk_type"]        = new Value { StringValue = item.Chunk.Type.ToString() };
            point.Payload["content_hash"]      = new Value { StringValue = item.Chunk.ContentHash };
            point.Payload["last_modified"]     = new Value { StringValue = item.Chunk.Metadata.LastModified.ToString("O") };
            point.Payload["repository_name"]   = new Value { StringValue = item.Chunk.Metadata.RepositoryName };

            if (item.Chunk.Metadata.Namespace is not null)
                point.Payload["namespace"]  = new Value { StringValue = item.Chunk.Metadata.Namespace };
            if (item.Chunk.Metadata.ClassName is not null)
                point.Payload["class_name"] = new Value { StringValue = item.Chunk.Metadata.ClassName };
            if (item.Chunk.Metadata.MethodName is not null)
                point.Payload["method_name"] = new Value { StringValue = item.Chunk.Metadata.MethodName };

            if (markResumenPending)
            {
                // Punto ya existente: conserva su estado (pending true/false tal cual estaba).
                // Punto nunca visto: nace pendiente.
                point.Payload[ResumenPendingPayloadKey] = new Value
                {
                    BoolValue = item.ExistingResumen?.ResumenPending ?? true
                };
            }

            return point;
        }).ToList();

        await _client.UpsertAsync(collectionName, points, wait: waitForCommit, cancellationToken: ct);

        _logger.LogDebug("Upserted {Count} points to collection '{Collection}'.",
            batch.Count, collectionName);

        return batch.Count;
    }

    /// <summary>
    /// Fase 2: adjunta el vector de resumen a un punto YA existente (subido en Fase 1),
    /// sin reenviar payload ni los otros vectores — gRPC UpdatePointVectors.
    /// </summary>
    public async Task UpdateSummaryVectorAsync(
        string collectionName,
        Guid pointId,
        float[] summaryVector,
        CancellationToken ct = default)
    {
        var namedVectors = new NamedVectors();
        namedVectors.Vectors[SummaryVectorName] = summaryVector;

        var pointVectors = new PointVectors
        {
            Id = new PointId { Uuid = pointId.ToString() },
            Vectors = new Vectors { Vectors_ = namedVectors }
        };

        await _client.UpdateVectorsAsync(collectionName, new[] { pointVectors }, wait: false, cancellationToken: ct);
    }

    /// <summary>
    /// Marca puntos como completados (resumen_pending=false) tras un
    /// <see cref="UpdateSummaryVectorAsync"/> exitoso, o tras determinar que un
    /// chunk cayó en el sentinel SIN_CONTENIDO_DE_NEGOCIO (no va a recibir vector,
    /// pero tampoco debe seguir apareciendo como pendiente).
    /// </summary>
    public async Task MarkResumenCompleteAsync(
        string collectionName,
        IReadOnlyList<Guid> pointIds,
        CancellationToken ct = default)
    {
        if (pointIds.Count == 0) return;

        var payload = new Dictionary<string, Value> { [ResumenPendingPayloadKey] = new Value { BoolValue = false } };
        await _client.SetPayloadAsync(collectionName, payload, ids: pointIds, wait: false, cancellationToken: ct);
    }

    /// <summary>Cuenta puntos con <see cref="ResumenPendingPayloadKey"/>=true — usado por `rag status` (decisión 7).</summary>
    public async Task<ulong> CountResumenPendingAsync(string collectionName, CancellationToken ct = default)
    {
        var filter = new Filter
        {
            Must = { new Condition { Field = new FieldCondition { Key = ResumenPendingPayloadKey, Match = new Match { Boolean = true } } } }
        };
        return await _client.CountAsync(collectionName, filter, exact: true, cancellationToken: ct);
    }

    /// <summary>Un punto reconstruido desde el payload de Qdrant, listo para volver a pasar por el generador de resumen.</summary>
    public sealed record PendingResumenPoint(Guid PointId, CodeChunk Chunk);

    /// <summary>
    /// Decisión 3a: reanudación de la Fase 2 sin reprocesar Fase 1. Escanea (scroll)
    /// los puntos con resumen_pending=true, reconstruyendo el <see cref="CodeChunk"/>
    /// necesario para volver a llamar al generador de resumen a partir del payload ya
    /// guardado en Fase 1 (no hace falta releer el archivo fuente).
    /// </summary>
    public async Task<(IReadOnlyList<PendingResumenPoint> Points, PointId? NextOffset)> ScrollPendingResumenAsync(
        string collectionName,
        PointId? offset = null,
        uint limit = 100,
        CancellationToken ct = default)
    {
        var filter = new Filter
        {
            Must = { new Condition { Field = new FieldCondition { Key = ResumenPendingPayloadKey, Match = new Match { Boolean = true } } } }
        };

        var response = await _client.ScrollAsync(
            collectionName,
            filter: filter,
            limit: limit,
            offset: offset,
            payloadSelector: new WithPayloadSelector { Enable = true },
            cancellationToken: ct);

        var points = response.Result.Select(MapToPendingResumenPoint).ToList();
        var next = response.NextPageOffset;
        var hasNext = next is not null && next.PointIdOptionsCase != PointId.PointIdOptionsOneofCase.None;

        return (points, hasNext ? next : null);
    }

    private static PendingResumenPoint MapToPendingResumenPoint(RetrievedPoint point)
    {
        var p = point.Payload;
        var pointId = Guid.Parse(point.Id.Uuid);

        var chunk = new CodeChunk
        {
            Id = pointId,
            Content = p["content"].StringValue,
            EnrichedContent = p["enriched_content"].StringValue,
            Type = Enum.Parse<ChunkType>(p["chunk_type"].StringValue),
            ContentHash = p["content_hash"].StringValue,
            Metadata = new CodeChunkMetadata(
                FilePath: p["file_path"].StringValue,
                RelativeFilePath: p.GetValueOrDefault("relative_path")?.StringValue ?? string.Empty,
                Language: Enum.Parse<SourceLanguage>(p["language"].StringValue),
                Namespace: p.GetValueOrDefault("namespace")?.StringValue,
                ClassName: p.GetValueOrDefault("class_name")?.StringValue,
                MethodName: p.GetValueOrDefault("method_name")?.StringValue,
                StartLine: (int)p["start_line"].IntegerValue,
                EndLine: (int)p["end_line"].IntegerValue,
                LastModified: DateTimeOffset.Parse(p["last_modified"].StringValue),
                RepositoryName: p["repository_name"].StringValue
            )
        };

        return new PendingResumenPoint(pointId, chunk);
    }
}
