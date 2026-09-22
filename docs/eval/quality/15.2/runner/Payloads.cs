using System.Text.Json;
using System.Text.Json.Serialization;

namespace RagEngine.Experiment152;

/// <summary>Payload indexado de un chunk, tal como lo volcó fingerprint.py.</summary>
public sealed record ChunkPayload
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("relative_path")] public string? RelativePath { get; init; }
    [JsonPropertyName("class_name")] public string? ClassName { get; init; }
    [JsonPropertyName("method_name")] public string? MethodName { get; init; }
    [JsonPropertyName("namespace")] public string? Namespace { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("content_hash")] public string? ContentHash { get; init; }
    [JsonPropertyName("defined_symbols")] public List<string>? DefinedSymbols { get; init; }
    [JsonPropertyName("consumed_symbols")] public List<string>? ConsumedSymbols { get; init; }
    [JsonPropertyName("chunk_type")] public string? ChunkType { get; init; }
}

/// <summary>
/// Localiza un chunk dentro de su archivo fuente. Replica EXACTAMENTE el criterio de
/// <c>prepare.py:source_evidence</c> — comparación sin espacios en blanco y ocurrencia
/// única — porque los metadatos de línea del payload tienen desfases conocidos
/// (<c>payload_line_drift</c> de preparation.json lista 36 casos). Confiar en start_line
/// habría desplazado los nodos de invocación de los brazos sintáctico y semántico.
/// </summary>
public static class SourceSpanLocator
{
    public readonly record struct Span(int Start, int End);

    /// <summary>
    /// Devuelve el intervalo de caracteres del chunk en <paramref name="text"/>, o null si
    /// el contenido no aparece o aparece más de una vez. Un chunk no localizable NO se
    /// adivina: el brazo se abstiene sobre él y así queda contado.
    /// </summary>
    public static Span? Locate(string text, string content)
    {
        var positions = new List<int>(text.Length);
        var normalized = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            positions.Add(i);
            normalized.Append(text[i]);
        }

        var needle = string.Concat(content.Where(c => !char.IsWhiteSpace(c)));
        if (needle.Length == 0) return null;

        var haystack = normalized.ToString();
        var start = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (start < 0) return null;
        if (haystack.IndexOf(needle, start + 1, StringComparison.Ordinal) >= 0) return null;

        return new Span(positions[start], positions[start + needle.Length - 1]);
    }
}

public static class PayloadStore
{
    public static IReadOnlyList<ChunkPayload> Load(string gzPath)
    {
        using var file = File.OpenRead(gzPath);
        using var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<ChunkPayload>>(gz)
               ?? throw new InvalidOperationException("Payload dump vacío");
    }
}
