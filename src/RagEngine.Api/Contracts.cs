using RagEngine.Core.Domain;

namespace RagEngine.Api;

/// <summary>
/// A single prior turn sent by the client for /api/ask and /api/ask/stream. The API
/// keeps no session state — the client resends the full transcript on every request.
/// </summary>
public sealed record ChatTurnDto(string Role, string Content)
{
    public ChatTurn ToDomain() => new(
        string.Equals(Role, "assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant : ChatRole.User,
        Content);
}

/// <summary>Request body shared by /api/search and /api/ask.</summary>
public sealed record RagQueryRequest(
    string Query,
    string? Collection = null,
    int? TopK = null,
    float? MinScore = null,
    bool? Rerank = null,
    IReadOnlyList<ChatTurnDto>? History = null);

/// <summary>A single retrieved chunk, flattened for JSON consumption.</summary>
public sealed record SourceDto(
    string File,
    string? Section,
    int StartLine,
    int EndLine,
    float Score,
    string Content,
    string? Resumen)
{
    /// <summary>
    /// <paramref name="resumen"/> viene de una consulta aparte a <c>SummaryCache</c> por
    /// <see cref="RetrievalResult.ContentHash"/> — null si la colección no generó resumen para
    /// este chunk (sin <c>--con-resumen</c>, o cayó en el sentinel SIN_CONTENIDO_DE_NEGOCIO).
    /// </summary>
    public static SourceDto From(RetrievalResult result, string? resumen = null) => new(
        result.Metadata.RelativeFilePath,
        result.Metadata.MethodName,
        result.Metadata.StartLine,
        result.Metadata.EndLine,
        result.SimilarityScore,
        result.Content,
        resumen);
}

/// <summary>Response body for /api/ask.</summary>
public sealed record RagAskResponse(string Answer, IReadOnlyList<SourceDto> Sources);
