namespace RagEngine.Core.Tests.Calibration;

/// <summary>
/// Localiza la raíz del repositorio subiendo desde el directorio de ejecución del
/// test hasta encontrar <c>RagEngine.slnx</c>. Existe porque la calibración del ítem
/// 11.5 necesita leer el código fuente y la documentación REALES del repositorio
/// (censo versionado en git, no una copia fijada en Fixtures/) para que la muestra
/// se pueda reproducir con <c>git log</c> sobre los mismos archivos.
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
