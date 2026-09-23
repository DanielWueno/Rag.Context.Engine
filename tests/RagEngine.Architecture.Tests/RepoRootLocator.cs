namespace RagEngine.Architecture.Tests;

/// <summary>
/// Localiza la raíz del repositorio subiendo desde el directorio de ejecución del
/// test hasta encontrar <c>RagEngine.slnx</c>. Copia deliberada de
/// <c>RagEngine.Core.Tests.Calibration.RepoRootLocator</c>: este proyecto no
/// referencia RagEngine.Core.Tests (evitaría el punto de 9.5 de ser un proyecto
/// nuevo e independiente) y la clase es demasiado pequeña para justificar una
/// tercera dependencia compartida.
/// </summary>
internal static class RepoRootLocator
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RagEngine.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "No se encontró RagEngine.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
