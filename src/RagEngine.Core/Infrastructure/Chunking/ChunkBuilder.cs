using RagEngine.Core.Domain;
using RagEngine.Core.Utilities;

namespace RagEngine.Core.Infrastructure.Chunking;

/// <summary>
/// Construcción de <see cref="CodeChunk"/> y geometría de la ventana deslizante,
/// compartidas por las 4 estrategias de chunking.
///
/// Existe porque el mismo bloque de 15 líneas estaba copiado en 9 sitios: calcular
/// el hash del contenido, derivar el Id determinista de (ruta, línea inicial, hash)
/// y rellenar los 10 campos de <see cref="CodeChunkMetadata"/>. Esas tres cosas son
/// invariantes del sistema —el Id determinista es lo que permite re-ingestar sin
/// duplicar puntos en Qdrant— y tenerlas repetidas nueve veces significa que
/// cualquier corrección hay que aplicarla nueve veces. El historial del proyecto ya
/// muestra el costo: el fix de metadata de líneas y el de chunk-imán hubo que
/// escribirlos en varios sitios.
/// </summary>
internal static class ChunkBuilder
{
    /// <summary>
    /// Estimación de ancho de línea usada para traducir un presupuesto de tokens a
    /// un número de líneas de ventana. Estaba como <c>80</c> suelto en dos sitios,
    /// con el mismo comentario "~80 chars/line" en uno solo de ellos.
    /// </summary>
    private const int ApproxCharsPerLine = 80;

    /// <summary>
    /// Geometría de la ventana deslizante a partir del presupuesto de tokens.
    /// <paramref name="minWindowLines"/> permite el piso que aplicaba la estrategia
    /// de fallback y que la de C# no aplicaba: se mantiene como parámetro en vez de
    /// unificarlo, porque igualar los dos comportamientos movería los cortes y
    /// obligaría a re-ingestar.
    /// </summary>
    public static (int WindowLines, int OverlapLines, int Step) WindowGeometry(
        ChunkingOptions options, int minWindowLines = 0)
    {
        int charsPerToken = (int)TokenEstimator.CharsPerToken;

        int windowLines = Math.Max(minWindowLines,
            options.MaxTokensPerChunk * charsPerToken / ApproxCharsPerLine);
        int overlapLines = Math.Max(0,
            options.OverlapTokens * charsPerToken / ApproxCharsPerLine);

        return (windowLines, overlapLines, Math.Max(1, windowLines - overlapLines));
    }

    /// <summary>
    /// Arma un chunk calculando su hash y su Id determinista. El Id se deriva de la
    /// línea inicial y del hash del contenido: mismo contenido en la misma posición
    /// produce siempre el mismo Id, que es lo que hace idempotente el re-upsert.
    /// </summary>
    public static CodeChunk Create(
        RawArtifact artifact,
        string content,
        string enrichedContent,
        ChunkType type,
        int startLine,
        int endLine,
        string repositoryName,
        SourceLanguage? language = null,
        string? namespaceName = null,
        string? className = null,
        string? methodName = null)
    {
        var hash = ContentHasher.Compute(content);

        return new CodeChunk
        {
            Id = DeterministicGuid.CreateForChunk(artifact.AbsolutePath, startLine, hash),
            Content = content,
            EnrichedContent = enrichedContent,
            Type = type,
            ContentHash = hash,
            Metadata = new CodeChunkMetadata(
                FilePath: artifact.AbsolutePath,
                RelativeFilePath: artifact.RelativePath,
                Language: language ?? artifact.Language,
                Namespace: namespaceName,
                ClassName: className,
                MethodName: methodName,
                StartLine: startLine,
                EndLine: endLine,
                LastModified: artifact.LastModified,
                RepositoryName: repositoryName)
        };
    }
}
