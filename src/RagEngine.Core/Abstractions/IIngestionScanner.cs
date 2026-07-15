namespace RagEngine.Core.Abstractions;

/// <summary>
/// Discovers and streams source files from a given root path using async enumeration
/// to avoid loading all paths into memory at once (backpressure-friendly).
/// </summary>
public interface IIngestionScanner
{
    /// <summary>
    /// Streams discovered file paths from the given root directory.
    /// </summary>
    /// <param name="rootPath">The root directory to scan recursively.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    IAsyncEnumerable<string> ScanAsync(string rootPath, CancellationToken cancellationToken = default);
}
