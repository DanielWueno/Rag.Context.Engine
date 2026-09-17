namespace RagEngine.Core.Domain;

public sealed record CollectionActor
{
    public bool IsAdministrator { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public string? Tenant { get; init; }
    public string? Module { get; init; }
}
