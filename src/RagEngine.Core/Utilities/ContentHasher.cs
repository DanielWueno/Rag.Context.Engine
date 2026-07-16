using System.Security.Cryptography;
using System.Text;

namespace RagEngine.Core.Utilities;

/// <summary>
/// Computes SHA-256 content hashes for change detection and deduplication.
/// Used to generate stable chunk IDs and detect file modifications
/// during incremental re-indexing.
/// </summary>
public static class ContentHasher
{
    /// <summary>
    /// Computes the hex-encoded SHA-256 hash of the given text.
    /// </summary>
    public static string Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the hex-encoded SHA-256 hash of a file at the given path.
    /// </summary>
    public static async Task<string> ComputeFileAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
