using System.Security.Cryptography;
using System.Text;

namespace RagEngine.Core.Utilities;

/// <summary>
/// Generates deterministic UUID v5 (RFC 4122, SHA-1 namespace-based).
/// Used to produce stable chunk IDs from file path + content hash,
/// enabling idempotent re-indexing without duplicates in Qdrant.
/// </summary>
public static class DeterministicGuid
{
    // DNS namespace GUID as defined in RFC 4122 Appendix C
    private static readonly Guid DnsNamespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    /// <summary>
    /// Creates a UUID v5 from the given name string using the DNS namespace.
    /// </summary>
    public static Guid Create(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return CreateFromNamespace(DnsNamespace, name);
    }

    /// <summary>
    /// Creates a UUID v5 from a chunk identity key and content hash.
    /// <paramref name="identityKey"/> debe ser estable entre entornos (ítem 8.f):
    /// se construye con <see cref="Chunking.ChunkBuilder.BuildIdentityKey"/> a
    /// partir de RepositoryName + RelativeFilePath, nunca de la ruta absoluta —
    /// esta última cambia según el host o el bind mount del contenedor que
    /// procesó el archivo, y dos rutas distintas para el mismo contenido lógico
    /// duplicarían el chunk en vez de reemplazarlo.
    /// </summary>
    public static Guid CreateForChunk(string identityKey, int startLine, string contentHash)
        => Create($"{identityKey}:{startLine}:{contentHash}");

    private static Guid CreateFromNamespace(Guid namespaceId, string name)
    {
        // Convert namespace UUID to big-endian byte array
        byte[] namespaceBytes = namespaceId.ToByteArray();
        SwapByteOrder(namespaceBytes);

        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] combined = [..namespaceBytes, ..nameBytes];

        byte[] hash = SHA1.HashData(combined);

        // Set version 5 (0101 in the most significant bits of byte 6)
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        // Set variant (10xx in the most significant bits of byte 8)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        // Convert back to little-endian for .NET Guid
        SwapByteOrder(hash);

        return new Guid(hash[..16]);
    }

    private static void SwapByteOrder(byte[] b)
    {
        // Swap bytes for the first three fields (little-endian in .NET Guid)
        (b[0], b[3]) = (b[3], b[0]);
        (b[1], b[2]) = (b[2], b[1]);
        (b[4], b[5]) = (b[5], b[4]);
        (b[6], b[7]) = (b[7], b[6]);
    }
}
