namespace RagEngine.Core.Domain;

/// <summary>
/// Configuration profile for the ingestion scanner.
/// Defines which files to include and which to exclude,
/// similar to a .gitignore but for indexing.
/// </summary>
public sealed record ScanProfile
{
    /// <summary>Allowed file extensions (e.g. ".cs", ".ts").</summary>
    public required IReadOnlySet<string> AllowedExtensions { get; init; }

    /// <summary>Directory name patterns to exclude.</summary>
    public required IReadOnlyList<string> ExcludePatterns { get; init; }

    /// <summary>Maximum file size in bytes. Files larger than this are skipped. Default: 512 KB.</summary>
    public long MaxFileSizeBytes { get; init; } = 524_288;

    /// <summary>
    /// Pre-configured profile for enterprise .NET solutions.
    /// Covers C#, TypeScript, XAML, SQL, Markdown, and project files.
    /// </summary>
    public static ScanProfile DotNetEnterprise => new()
    {
        AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".ts", ".tsx", ".js", ".jsx", ".xaml", ".sql",
            ".md", ".txt", ".json", ".xml", ".csproj"
        },
        ExcludePatterns =
        [
            "bin", "obj", "node_modules", ".git", ".vs", "__pycache__",
            ".idea", "packages", "migrations", "Migrations"
        ]
    };

    /// <summary>Minimal profile for C# only.</summary>
    public static ScanProfile CSharpOnly => new()
    {
        AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
        ExcludePatterns = ["bin", "obj", ".git", "migrations", "Migrations"]
    };
}
