namespace RagEngine.Core.Infrastructure.Authorization;

public enum AuthorizationMode
{
    Local,
    Empresarial
}

public sealed record CollectionAuthorizationOptions
{
    public const string SectionName = "Authorization";

    public AuthorizationMode Mode { get; init; } = AuthorizationMode.Local;
}
