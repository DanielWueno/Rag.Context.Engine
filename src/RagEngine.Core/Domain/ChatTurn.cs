namespace RagEngine.Core.Domain;

/// <summary>Who authored a given turn in a chat conversation.</summary>
public enum ChatRole
{
    User,
    Assistant
}

/// <summary>
/// A single prior turn in a multi-turn conversation, supplied by the caller so the
/// pipeline stays stateless — no server-side session storage. The caller (API/CLI)
/// is responsible for resending the full transcript on every request.
/// </summary>
public sealed record ChatTurn(ChatRole Role, string Content);
