using RagEngine.Core.Domain;

namespace RagEngine.Core.Abstractions;

/// <summary>
/// Discovers and streams source artifacts from a given root path using async enumeration.
/// IAsyncEnumerable guarantees streaming / deferred consumption — the full repository
/// is never loaded into memory at once (backpressure-friendly).
/// </summary>
public interface IIngestionScanner
{
    /// <summary>
    /// Streams discovered RawArtifacts from the given root directory.
    /// </summary>
    /// <param name="rootPath">The root directory to scan recursively.</param>
    /// <param name="profile">Scan profile defining extensions and exclusion patterns.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    IAsyncEnumerable<RawArtifact> ScanAsync(
        string rootPath,
        ScanProfile profile,
        CancellationToken cancellationToken = default);
}
