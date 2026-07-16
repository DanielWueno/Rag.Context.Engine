using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;

namespace RagEngine.Core.Infrastructure.Scanning;

/// <summary>
/// Discovers source files from the local filesystem using IAsyncEnumerable streaming.
/// Files are yielded one at a time — the entire repository is never loaded into memory.
/// Respects ScanProfile for extension filtering and directory exclusion.
/// </summary>
public sealed class FileSystemIngestionScanner : IIngestionScanner
{
    private readonly ILogger<FileSystemIngestionScanner> _logger;

    private static readonly Dictionary<string, SourceLanguage> ExtensionMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"]    = SourceLanguage.CSharp,
        [".ts"]    = SourceLanguage.TypeScript,
        [".tsx"]   = SourceLanguage.TypeScript,
        [".js"]    = SourceLanguage.JavaScript,
        [".jsx"]   = SourceLanguage.JavaScript,
        [".xaml"]  = SourceLanguage.Xaml,
        [".sql"]   = SourceLanguage.Sql,
        [".md"]    = SourceLanguage.Markdown,
        [".txt"]   = SourceLanguage.PlainText,
        [".json"]  = SourceLanguage.PlainText,
        [".xml"]   = SourceLanguage.PlainText,
        [".csproj"]= SourceLanguage.PlainText,
    };

    public FileSystemIngestionScanner(ILogger<FileSystemIngestionScanner> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RawArtifact> ScanAsync(
        string rootPath,
        ScanProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var root = new DirectoryInfo(rootPath);
        if (!root.Exists)
            throw new DirectoryNotFoundException($"Repository root not found: {rootPath}");

        _logger.LogInformation("Starting scan of {RootPath}", rootPath);

        int count = 0;
        await foreach (var artifact in EnumerateRecursiveAsync(root, rootPath, profile, cancellationToken))
        {
            count++;
            yield return artifact;
        }

        _logger.LogInformation("Scan complete. Discovered {Count} files in {RootPath}", count, rootPath);
    }

    private async IAsyncEnumerable<RawArtifact> EnumerateRecursiveAsync(
        DirectoryInfo dir,
        string rootPath,
        ScanProfile profile,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Enumerate files in current directory
        IEnumerable<FileInfo> files;
        try
        {
            files = dir.EnumerateFiles();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("Access denied to {Dir}: {Msg}", dir.FullName, ex.Message);
            yield break;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // Extension filter
            if (!profile.AllowedExtensions.Contains(file.Extension))
                continue;

            // Size filter
            if (file.Length > profile.MaxFileSizeBytes)
            {
                _logger.LogDebug("Skipping oversized file {File} ({Size} bytes)", file.Name, file.Length);
                continue;
            }

            var language = ExtensionMap.GetValueOrDefault(file.Extension, SourceLanguage.Unknown);
            var relativePath = Path.GetRelativePath(rootPath, file.FullName);

            yield return new RawArtifact(
                AbsolutePath: file.FullName,
                RelativePath: relativePath,
                Language: language,
                LastModified: file.LastWriteTimeUtc,
                SizeBytes: file.Length
            );
        }

        // Recurse into subdirectories
        IEnumerable<DirectoryInfo> subdirs;
        try
        {
            subdirs = dir.EnumerateDirectories();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("Access denied to subdirs of {Dir}: {Msg}", dir.FullName, ex.Message);
            yield break;
        }

        foreach (var subdir in subdirs)
        {
            ct.ThrowIfCancellationRequested();

            // Exclude configured directory names
            if (profile.ExcludePatterns.Any(p =>
                    string.Equals(subdir.Name, p, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogDebug("Excluding directory {Dir}", subdir.Name);
                continue;
            }

            await foreach (var artifact in EnumerateRecursiveAsync(subdir, rootPath, profile, ct))
                yield return artifact;
        }
    }
}
