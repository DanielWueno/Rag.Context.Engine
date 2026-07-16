using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

/// <summary>
/// Contract for language-specific code chunking strategies.
/// Implementations use Strategy Pattern — selected at runtime by ChunkingStrategyRouter
/// based on the SourceLanguage of the RawArtifact.
/// </summary>
public interface IChunkingStrategy
{
    /// <summary>The source language this strategy handles.</summary>
    SourceLanguage TargetLanguage { get; }

    /// <summary>
    /// Splits the content of a source artifact into semantically coherent chunks.
    /// Returns IAsyncEnumerable to maintain the streaming model of the pipeline.
    /// </summary>
    /// <param name="artifact">The source file metadata.</param>
    /// <param name="fileContent">The full text content of the file.</param>
    /// <param name="options">Chunking configuration (token limits, overlap, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<CodeChunk> ChunkAsync(
        RawArtifact artifact,
        string fileContent,
        ChunkingOptions options,
        CancellationToken cancellationToken = default);
}
