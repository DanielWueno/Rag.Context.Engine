using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.Authorization;

public sealed class CollectionAuthorizationService : ICollectionAuthorizationService
{
    public bool Authorize(CollectionManifest? manifest, CollectionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (actor.IsAdministrator)
            return true;

        if (manifest is not { IsPublished: true })
            return false;

        var hasScope = actor.Scopes.Any(scope =>
            !string.IsNullOrWhiteSpace(scope) &&
            manifest.RequiredScopes.Contains(scope, StringComparer.Ordinal));

        return hasScope && (manifest.Tenants.Count == 0 ||
            (!string.IsNullOrWhiteSpace(actor.Tenant) &&
             manifest.Tenants.Contains(actor.Tenant, StringComparer.Ordinal)));
    }
}
