namespace RagEngine.Core.Domain;

/// <summary>
/// Rich structural metadata attached to every indexed chunk.
/// Allows the LLM to understand the context of a fragment
/// without needing to read the full file.
/// </summary>
public sealed record CodeChunkMetadata(
    string FilePath,
    string RelativeFilePath,
    SourceLanguage Language,
    string? Namespace,
    string? ClassName,
    string? MethodName,
    int StartLine,
    int EndLine,
    DateTimeOffset LastModified,
    string RepositoryName
);
