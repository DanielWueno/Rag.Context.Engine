namespace RagEngine.Core.Domain;

/// <summary>
/// Identidad verificada por el host, no por el cliente de la consulta.
/// Sin autenticación, el resolver descarta todos los privilegios de <see cref="Actor"/>.
/// </summary>
public sealed record CollectionIdentity
{
    public bool IsAuthenticated { get; init; }
    public CollectionActor Actor { get; init; } = new();
}
