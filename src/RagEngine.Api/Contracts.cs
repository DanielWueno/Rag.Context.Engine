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
/// <param name="ResponseMode">
///   "technical" or "simple" (case-insensitive), only used by /api/ask and
///   /api/ask/stream. Anything else — including null/omitted — defaults to
///   "simple". Kept as a raw string over the wire, parsed with
///   <see cref="ResponseModeExtensions.ParseResponseMode"/>, the same
///   string-over-the-wire/enum-in-code pattern <see cref="ChatTurnDto.ToDomain"/>
///   uses for <c>Role</c>.
/// </param>
public sealed record RagQueryRequest(
    string Query,
    string? Collection = null,
    int? TopK = null,
    float? MinScore = null,
    bool? Rerank = null,
    string? ResponseMode = null,
    IReadOnlyList<ChatTurnDto>? History = null);

/// <summary>Parsing helper for <see cref="RagQueryRequest.ResponseMode"/>.</summary>
public static class ResponseModeExtensions
{
    /// <summary>
    /// Defaults to <see cref="Core.Domain.ResponseMode.Simple"/> for null, empty, or
    /// unrecognized values — the toggle is opt-in to technical detail, not opt-out.
    /// </summary>
    public static Core.Domain.ResponseMode ParseResponseMode(this string? raw) =>
        string.Equals(raw, "technical", StringComparison.OrdinalIgnoreCase)
            ? Core.Domain.ResponseMode.Technical
            : Core.Domain.ResponseMode.Simple;
}

/// <summary>
/// A single retrieved chunk, flattened for JSON consumption. File/Section/StartLine/
/// EndLine/Content are nullable because <see cref="ResponseMode.Simple"/> answers
/// (see <see cref="Redacted"/>) omit them entirely — Score is the only field every
/// mode always populates.
/// </summary>
public sealed record SourceDto(
    string? File,
    string? Section,
    int? StartLine,
    int? EndLine,
    float Score,
    string? Content,
    string? Resumen)
{
    /// <summary>
    /// Full technical citation — file path, line range, and the raw chunk content —
    /// used for <see cref="ResponseMode.Technical"/>.
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

    /// <summary>
    /// Used for <see cref="ResponseMode.Simple"/> — a non-technical reader cannot tell
    /// whether a file path/line range/raw code fragment IS the answer, a citation, or
    /// an error, so none of that is sent. Only the score, plus <paramref name="resumen"/>
    /// when the collection has one, is included: the pre-generated business summary is
    /// itself already the "citation explained in plain language" for this chunk, not raw
    /// code, so it is the one exception allowed alongside the score.
    /// </summary>
    public static SourceDto Redacted(RetrievalResult result, string? resumen = null) => new(
        File: null,
        Section: null,
        StartLine: null,
        EndLine: null,
        Score: result.SimilarityScore,
        Content: null,
        Resumen: resumen);
}

/// <summary>Response body for /api/ask.</summary>
public sealed record RagAskResponse(string Answer, IReadOnlyList<SourceDto> Sources);
