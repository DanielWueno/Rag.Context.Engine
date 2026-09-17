using RagEngine.Core.Abstractions;
using Microsoft.Extensions.Options;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.VectorStore;

/// <summary>
/// Resuelve el <see cref="RetrievalProfile"/> efectivo de una colección (ítem 7.a):
/// lee <see cref="CollectionManifest.Profile"/> (el nombre) y lo busca en el catálogo
/// nombrado de <see cref="RetrievalProfileCatalogOptions"/>. Nunca lanza por un nombre
/// desconocido ni por ausencia de manifiesto — ambos casos resuelven a null, que es la
/// señal de "usa el comportamiento global de hoy" para todo consumidor.
/// </summary>
public interface IRetrievalProfileResolver
{
    /// <summary>
    /// Variante pura (sin I/O): resuelve a partir de un manifiesto ya leído. Útil para
    /// no repetir el round-trip a Qdrant cuando el llamador (p.ej. la autorización de
    /// la API) ya obtuvo el manifiesto por otra razón.
    /// </summary>
    RetrievalProfile? Resolve(CollectionManifest? manifest);

    /// <summary>Lee el manifiesto de <paramref name="collectionName"/> y resuelve su perfil.</summary>
    Task<RetrievalProfile?> ResolveAsync(string collectionName, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRetrievalProfileResolver"/>
public sealed class RetrievalProfileResolver : IRetrievalProfileResolver
{
    private readonly IVectorStoreAdmin _store;
    private readonly RetrievalProfileCatalogOptions _catalog;

    public RetrievalProfileResolver(IVectorStoreAdmin store, IOptions<RetrievalProfileCatalogOptions> catalog)
    {
        _store = store;
        _catalog = catalog.Value;
    }

    /// <inheritdoc />
    public RetrievalProfile? Resolve(CollectionManifest? manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest?.Profile)) return null;
        return _catalog.Profiles.TryGetValue(manifest.Profile, out var profile) ? profile : null;
    }

    /// <inheritdoc />
    public async Task<RetrievalProfile?> ResolveAsync(string collectionName, CancellationToken cancellationToken = default)
    {
        var manifest = await _store.GetManifestAsync(collectionName, cancellationToken);
        return Resolve(manifest);
    }
}
