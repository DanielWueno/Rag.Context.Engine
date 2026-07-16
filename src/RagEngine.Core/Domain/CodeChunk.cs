namespace RagEngine.Core.Domain;

/// <summary>
/// A semantically coherent fragment of source code, ready for vectorization.
/// Contains the raw text AND the enriched text (raw + structural context header)
/// that gets embedded into the vector space.
/// The ID is a deterministic UUID v5 derived from file path + start line + content hash,
/// enabling idempotent re-indexing without duplicates.
/// </summary>
public sealed record CodeChunk
{
    /// <summary>
    /// Deterministic UUID v5 (filePath + startLine + contentHash).
    /// Same chunk content at the same location always produces the same ID.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>The raw source text of this chunk (stored in Qdrant payload).</summary>
    public required string Content { get; init; }

    /// <summary>
    /// Enriched text = context header + raw content.
    /// This is what gets embedded by the ONNX model for higher-quality retrieval.
    /// Example header:
    ///   // Repository: MyApp
    ///   // File: src/Services/OrderService.cs
    ///   // Namespace: MyApp.Services
    ///   // class: OrderService : IOrderService
    ///   // method: GetOrderAsync(int id) → Task&lt;Order&gt;
    /// </summary>
    public required string EnrichedContent { get; init; }

    /// <summary>Structural metadata: file path, language, class name, method name, etc.</summary>
    public required CodeChunkMetadata Metadata { get; init; }

    /// <summary>The structural type of this chunk (Method, Class, Interface, etc.).</summary>
    public required ChunkType Type { get; init; }

    /// <summary>SHA-256 hash of Content, used for incremental change detection.</summary>
    public required string ContentHash { get; init; }
}

/// <summary>
/// Structural classification of a code chunk.
/// </summary>
public enum ChunkType
{
    Method,             // A complete method body
    Class,              // Class header + fields (without methods)
    Interface,          // Full interface definition
    Property,           // A grouped block of properties
    Constructor,        // A class constructor
    XamlControl,        // A XAML UI control with its properties
    XamlDataTemplate,   // A XAML DataTemplate block
    SqlProcedure,       // A complete SQL stored procedure
    DocumentSection,    // A Markdown section (between headings)
    PlainTextWindow     // A sliding-window chunk for unstructured text
}
