using System.Text.Json;
using RagEngine.Core.Domain;

namespace RagEngine.Poc.FreeSearch;

/// <summary>
/// Set de evaluación etiquetado a MANO — es el trabajo humano que decide el go/no-go
/// del PoC, no un artefacto generado. Cada ítem es una pregunta libre (del tipo que
/// hoy falla) más la identificación del chunk que DEBERÍA recuperarse.
///
/// El chunk objetivo se identifica por ruta relativa + un substring de su contenido,
/// porque el Id real (UUID v5) no es conocible por un humano al etiquetar.
/// </summary>
public sealed record EvalItem
{
    /// <summary>La pregunta en lenguaje natural, tal como la haría un usuario no técnico.</summary>
    public required string Question { get; init; }

    /// <summary>Ruta(s) relativa(s) donde vive la respuesta correcta. Vacío = cualquier archivo.</summary>
    public string[] TargetRelativePaths { get; init; } = [];

    /// <summary>
    /// Substrings que el contenido del chunk objetivo debe contener (p. ej. el nombre de
    /// un atributo o campo). Vacío = basta con que la ruta coincida.
    /// </summary>
    public string[] TargetContentContains { get; init; } = [];

    /// <summary>Nota opcional del etiquetador (por qué es la respuesta correcta).</summary>
    public string? Note { get; init; }

    /// <summary>Determina si un chunk dado es un objetivo válido para esta pregunta.</summary>
    public bool Matches(CodeChunk chunk)
    {
        var pathOk = TargetRelativePaths.Length == 0
            || TargetRelativePaths.Any(p =>
                chunk.Metadata.RelativeFilePath.Replace('\\', '/')
                    .EndsWith(p.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        var contentOk = TargetContentContains.Length == 0
            || TargetContentContains.Any(s =>
                chunk.Content.Contains(s, StringComparison.OrdinalIgnoreCase));

        return pathOk && contentOk;
    }
}

public static class EvalSetLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static List<EvalItem> Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Set de evaluación no encontrado: {path}", path);

        var json = File.ReadAllText(path);
        var items = JsonSerializer.Deserialize<List<EvalItem>>(json, JsonOptions)
            ?? throw new InvalidOperationException($"No se pudo parsear el set de evaluación: {path}");

        if (items.Count == 0)
            throw new InvalidOperationException("El set de evaluación está vacío — etiqueta preguntas antes de correr el PoC.");

        return items;
    }
}
