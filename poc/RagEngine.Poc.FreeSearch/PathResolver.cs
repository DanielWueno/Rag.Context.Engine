namespace RagEngine.Poc.FreeSearch;

/// <summary>
/// Resuelve rutas sin depender del working directory (que con `dotnet run` es ambiguo:
/// puede ser la raíz del repo, el dir del proyecto o el output). Prueba, en orden:
/// tal cual (CWD), y luego relativo al directorio del ejecutable (AppContext.BaseDirectory,
/// donde los Content del csproj se copian).
/// </summary>
public static class PathResolver
{
    /// <summary>Devuelve la ruta absoluta del archivo si existe en algún candidato; si no, null.</summary>
    public static string? FindFile(string path)
    {
        foreach (var candidate in Candidates(path))
            if (File.Exists(candidate))
                return candidate;
        return null;
    }

    /// <summary>Resuelve una ruta (posiblemente relativa) contra un directorio base explícito.</summary>
    public static string ResolveAgainst(string path, string baseDir)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));

    private static IEnumerable<string> Candidates(string path)
    {
        if (Path.IsPathRooted(path))
        {
            yield return path;
            yield break;
        }
        yield return Path.GetFullPath(path); // relativo al CWD
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path)); // junto al ejecutable
    }
}
