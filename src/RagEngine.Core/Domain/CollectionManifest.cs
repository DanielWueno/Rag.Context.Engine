using System.Text.Json;
using System.Text.Json.Serialization;

namespace RagEngine.Core.Domain;

/// <summary>
/// Tracks model identity, ACL publication contract and collection metadata to detect
/// semantic drift. Stored as a single vector-store point (see
/// <see cref="Infrastructure.VectorStore.QdrantVectorStore.UpsertManifestAsync"/>) whose
/// payload carries the JSON-serialized manifest under the
/// <see cref="Infrastructure.VectorStore.QdrantVectorStore.ManifestPayloadKey"/> key.
/// The SHA-256 hash of model.onnx identifies the embedding model used;
/// a hash mismatch signals that the collection must be re-indexed.
/// </summary>
/// <remarks>
/// <see cref="RequiredScopes"/> es el contrato de publicación ACL (lo aplica
/// 5.f.3-acl-autorizacion-y-modo-local): vacío significa "sin publicar, solo
/// administrador". Este ítem (5.f.1) sólo persiste y hace roundtrip del campo — no
/// aplica autorización. <see cref="Profile"/> es el perfil de recuperación opcional
/// por colección, compartido con 7.a-perfil-por-coleccion.
/// </remarks>
public sealed record CollectionManifest
{
    public required string CollectionName { get; init; }
    public required string ModelName { get; init; }
    public required string ModelOnnxSha256 { get; init; }
    public required int EmbeddingDimension { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastIndexedAt { get; set; } = DateTimeOffset.UtcNow;
    public int TotalChunks { get; set; }

    /// <summary>Perfil de recuperación opcional por colección (7.a-perfil-por-coleccion). Null = perfil por defecto.</summary>
    public string? Profile { get; init; }

    /// <summary>
    /// Contrato de publicación ACL: vacío/ausente significa "sin publicar — solo
    /// administrador". Una lista no vacía significa que al menos uno de estos scopes
    /// (combinado con un tenant permitido en <see cref="Tenants"/>) habilita lectura.
    /// La aplicación de esta regla vive en 5.f.3-acl-autorizacion-y-modo-local, no en
    /// este record.
    /// </summary>
    public IReadOnlyList<string> RequiredScopes { get; init; } = Array.Empty<string>();

    /// <summary>Tenants permitidos a leer esta colección cuando <see cref="RequiredScopes"/> no está vacío.</summary>
    public IReadOnlyList<string> Tenants { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Invariante de publicación: true solo cuando hay al menos un scope requerido.
    /// Un manifiesto migrado desde el esquema antiguo (5.f.2-migracion-manifiestos-antiguos)
    /// llega con <see cref="RequiredScopes"/> vacío y por lo tanto siempre es false aquí —
    /// "vacío" nunca implica "publicado". La aplicación completa de autorización (mapeo de
    /// actor a scopes/tenants) vive en 5.f.3-acl-autorizacion-y-modo-local; esta propiedad
    /// solo expone la invariante de datos para que sea verificable sin duplicar lógica.
    /// </summary>
    public bool IsPublished => RequiredScopes.Count > 0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Case-insensitive: manifiestos antiguos o escritos a mano pueden usar PascalCase;
        // no queremos que un cambio de convención de nombres rompa la deserialización.
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Serializa este manifiesto al JSON que se guarda en el payload del backend vectorial.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Deserializa un manifiesto desde su payload JSON guardado. Los campos ausentes
    /// (manifiestos escritos antes de este desglose) toman sus valores por defecto —
    /// RequiredScopes y Tenants vacíos, Profile null — nunca publicando una colección
    /// por defecto. La migración explícita de manifiestos antiguos es responsabilidad
    /// de 5.f.2-migracion-manifiestos-antiguos; esto solo evita que la deserialización
    /// falle.
    /// </summary>
    public static CollectionManifest FromJson(string json) =>
        JsonSerializer.Deserialize<CollectionManifest>(json, JsonOptions)
        ?? throw new JsonException("El manifiesto deserializado resultó null.");
}
