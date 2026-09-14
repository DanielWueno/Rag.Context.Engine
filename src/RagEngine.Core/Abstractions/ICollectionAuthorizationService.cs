using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

public interface ICollectionAuthorizationService
{
    /// <summary>
    /// Admin siempre lee; sin manifiesto o sin RequiredScopes, sólo admin.
    /// Para el resto basta un scope requerido, combinado con un tenant permitido.
    /// Tenants vacío significa sin restricción de tenant, nunca sin restricción de scope.
    /// Los identificadores se comparan de forma ordinal, sensible a mayúsculas.
    /// </summary>
    bool Authorize(CollectionManifest? manifest, CollectionActor actor);
}
