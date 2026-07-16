namespace RagEngine.Core.Domain;

/// <summary>
/// Represents a discovered source file before processing.
/// Immutable record for thread-safety in the concurrent pipeline.
/// </summary>
public sealed record RawArtifact(
    string AbsolutePath,
    string RelativePath,
    SourceLanguage Language,
    DateTimeOffset LastModified,
    long SizeBytes
);
