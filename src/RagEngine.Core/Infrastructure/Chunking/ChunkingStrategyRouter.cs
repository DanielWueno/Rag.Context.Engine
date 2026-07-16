using Microsoft.Extensions.Logging;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.Chunking;

/// <summary>
/// Routes incoming RawArtifacts to the appropriate IChunkingStrategy
/// based on their SourceLanguage at runtime.
/// New strategies can be added in Sprint 3 without modifying the pipeline.
/// </summary>
public sealed class ChunkingStrategyRouter
{
    private readonly Dictionary<SourceLanguage, IChunkingStrategy> _strategies;
    private readonly FallbackChunkingStrategy _fallback;
    private readonly ILogger<ChunkingStrategyRouter> _logger;

    public ChunkingStrategyRouter(
        IEnumerable<IChunkingStrategy> strategies,
        FallbackChunkingStrategy fallback,
        ILogger<ChunkingStrategyRouter> logger)
    {
        _fallback = fallback;
        _logger = logger;

        // Index strategies by target language for O(1) lookup
        _strategies = strategies
            .Where(s => s.TargetLanguage != SourceLanguage.Unknown)
            .ToDictionary(s => s.TargetLanguage);

        _logger.LogInformation(
            "ChunkingStrategyRouter initialized with {Count} language-specific strategies: [{Languages}]",
            _strategies.Count,
            string.Join(", ", _strategies.Keys));
    }

    /// <summary>
    /// Returns the best-matching IChunkingStrategy for the given artifact.
    /// Falls back to the sliding-window strategy for unknown or unregistered languages.
    /// </summary>
    public IChunkingStrategy GetStrategy(RawArtifact artifact)
    {
        if (_strategies.TryGetValue(artifact.Language, out var strategy))
        {
            _logger.LogDebug("Using {Strategy} for {File}",
                strategy.GetType().Name, artifact.RelativePath);
            return strategy;
        }

        _logger.LogDebug("No specific strategy for {Lang}, using FallbackChunkingStrategy",
            artifact.Language);
        return _fallback;
    }
}
