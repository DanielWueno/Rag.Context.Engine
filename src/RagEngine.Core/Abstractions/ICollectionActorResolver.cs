using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

public interface ICollectionActorResolver
{
    /// <summary>
    /// Local produce el administrador sintético sin invocar la fuente.
    /// Empresarial sólo conserva privilegios de una identidad autenticada por el host;
    /// identidad ausente o no autenticada produce un actor sin privilegios, nunca un fallback local.
    /// </summary>
    CollectionActor Resolve(Func<CollectionIdentity?> identitySource);
}
