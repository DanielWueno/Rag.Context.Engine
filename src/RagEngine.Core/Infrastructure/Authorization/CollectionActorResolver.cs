using Microsoft.Extensions.Options;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.Authorization;

public sealed class CollectionActorResolver(IOptions<CollectionAuthorizationOptions> options)
    : ICollectionActorResolver
{
    public CollectionActor Resolve(Func<CollectionIdentity?> identitySource)
    {
        switch (options.Value.Mode)
        {
            case AuthorizationMode.Local:
                return new CollectionActor { IsAdministrator = true };
            case AuthorizationMode.Empresarial:
                var identity = identitySource();
                return identity is { IsAuthenticated: true, Actor: not null }
                    ? identity.Actor
                    : new CollectionActor();
            default:
                throw new InvalidOperationException("Authorization:Mode debe ser Local o Empresarial.");
        }
    }
}
