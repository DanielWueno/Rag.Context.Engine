namespace RagEngine.Core.Domain;

/// <summary>
/// Contexto explícito con el que una consulta quedó autorizada antes de entrar al
/// puerto de retrieval. Nunca se infiere de null: incluso el uso local debe
/// declarar su modo de forma explícita.
/// </summary>
public sealed record RetrievalContext
{
    public required RetrievalContextMode Mode { get; init; }
    public string? Tenant { get; init; }
    public string? Module { get; init; }

    public static RetrievalContext Local { get; } = new()
    {
        Mode = RetrievalContextMode.Local
    };

    public static RetrievalContext ForAuthorized(string? tenant = null, string? module = null) => new()
    {
        Mode = RetrievalContextMode.Authorized,
        Tenant = tenant,
        Module = module
    };
}

public enum RetrievalContextMode
{
    Local,
    Authorized
}
