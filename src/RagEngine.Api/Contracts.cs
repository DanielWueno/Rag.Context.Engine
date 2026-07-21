using RagEngine.Core.Domain;

namespace RagEngine.Api;

/// <summary>Request body shared by /api/search and /api/ask.</summary>
public sealed record RagQueryRequest(
    string Query,
    string? Collection = null,
    int? TopK = null,
    float? MinScore = null,
    bool? Rerank = null);

/// <summary>A single retrieved chunk, flattened for JSON consumption.</summary>
public sealed record SourceDto(
    string File,
    string? Section,
    int StartLine,
    int EndLine,
    float Score,
    string Content)
{
    public static SourceDto From(RetrievalResult result) => new(
        result.Metadata.RelativeFilePath,
        result.Metadata.MethodName,
        result.Metadata.StartLine,
        result.Metadata.EndLine,
        result.SimilarityScore,
        result.Content);
}

/// <summary>Response body for /api/ask.</summary>
public sealed record RagAskResponse(string Answer, IReadOnlyList<SourceDto> Sources);
