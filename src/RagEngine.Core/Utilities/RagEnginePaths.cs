using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RagEngine.Core.Utilities;

/// <summary>
/// Resuelve las rutas de disco que el motor necesita (modelos ONNX, logs) sin
/// depender del directorio de trabajo ni de la máquina en la que se escribió el
/// appsettings.
///
/// El problema que resuelve: <c>appsettings.json</c> llevaba rutas absolutas de la
/// máquina del autor (<c>/Users/&lt;usuario&gt;/models/...</c>), así que el archivo
/// versionado no servía en ninguna otra máquina; y los sinks de Serilog usaban la
/// ruta relativa <c>logs/</c>, que al correr <c>dotnet run --project src/X</c>
/// terminaba escribiendo dentro del árbol de código.
///
/// Contrato: las rutas de configuración admiten <c>~</c> y tokens
/// <c>${VARIABLE}</c>. Un token sin definir se deja intacto a propósito, para que
/// el mensaje de error de quien abre el archivo lo muestre tal cual en vez de
/// fallar con una ruta a medio construir.
/// </summary>
public static class RagEnginePaths
{
    /// <summary>Variable que define dónde viven los modelos ONNX descargados.</summary>
    public const string ModelsDirVariable = "RAG_MODELS_DIR";

    /// <summary>Variable que define dónde se escriben los logs.</summary>
    public const string LogsDirVariable = "RAG_LOGS_DIR";

    /// <summary>
    /// Archivo que marca la raíz del repositorio. Se busca hacia arriba desde el
    /// directorio del binario para anclar los logs a un sitio estable.
    /// </summary>
    private const string SolutionMarker = "RagEngine.slnx";

    private static readonly Regex VariableToken = new(
        @"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.Compiled);

    /// <summary>Sufijo que marca un binario ONNX optimizado (int8) para Apple Silicon.</summary>
    private const string Arm64QuantizedSuffix = "_qint8_arm64";

    /// <summary>
    /// Directorio de modelos por defecto: <c>~/models</c>. Es donde
    /// <c>infra/download-model.sh</c> los deja cuando se corre desde el home.
    /// </summary>
    public static string DefaultModelsDirectory =>
        Path.Combine(HomeDirectory, "models");

    private static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Expande <c>~</c> inicial y tokens <c>${VARIABLE}</c>. <c>${RAG_MODELS_DIR}</c>
    /// cae al valor por defecto si no está definida; cualquier otro token sin
    /// definir se deja literal para que aparezca en el mensaje de error.
    /// </summary>
    public static string Expand(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string expanded = VariableToken.Replace(path, match =>
        {
            string name = match.Groups["name"].Value;
            string? value = Environment.GetEnvironmentVariable(name);

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return name == ModelsDirVariable
                ? DefaultModelsDirectory
                : match.Value;
        });

        if (expanded == "~")
        {
            return HomeDirectory;
        }

        if (expanded.StartsWith("~/", StringComparison.Ordinal) ||
            expanded.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return Path.Combine(HomeDirectory, expanded[2..]);
        }

        return expanded;
    }

    /// <summary>
    /// Resuelve la ruta de un archivo de modelo. Tras expandir, una ruta relativa
    /// se ancla al directorio de modelos — nunca al directorio de trabajo, que es
    /// lo que hacía que el mismo comando funcionara o no según desde dónde se
    /// invocara.
    /// </summary>
    public static string ResolveModelPath(string configuredPath)
    {
        string expanded = Expand(configuredPath);

        if (string.IsNullOrWhiteSpace(expanded))
        {
            return expanded;
        }

        // Un token sin resolver ya es informativo: no lo enterremos bajo el
        // directorio de modelos, porque el mensaje de error sería confuso. La
        // selección por arquitectura tampoco aplica: no hay archivo real que
        // elegir todavía.
        if (VariableToken.IsMatch(expanded))
        {
            return expanded;
        }

        if (Path.IsPathRooted(expanded))
        {
            return SelectArchitectureBinary(expanded);
        }

        string modelsDir = Environment.GetEnvironmentVariable(ModelsDirVariable) is { Length: > 0 } fromEnv
            ? Expand(fromEnv)
            : DefaultModelsDirectory;

        // Las rutas relativas de los defaults ya vienen con el prefijo "models/",
        // que sería redundante bajo el directorio de modelos.
        string relative = expanded.StartsWith("models/", StringComparison.Ordinal) ||
                          expanded.StartsWith(@"models\", StringComparison.Ordinal)
            ? expanded["models/".Length..]
            : expanded;

        return SelectArchitectureBinary(Path.Combine(modelsDir, relative));
    }

    /// <summary>
    /// Si <paramref name="path"/> apunta a un binario ONNX con el sufijo
    /// <c>_qint8_arm64</c> (optimizado para Apple Silicon), lo deja tal cual en
    /// arm64 y cae al binario genérico (sin el sufijo, p. ej. <c>model.onnx</c>)
    /// en cualquier otra arquitectura. Una ruta sin ese sufijo — configuración
    /// explícita del usuario — nunca se toca.
    /// </summary>
    public static string SelectArchitectureBinary(string path) =>
        SelectArchitectureBinary(path, RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// Variante pura de <see cref="SelectArchitectureBinary(string)"/> que recibe
    /// la arquitectura como parámetro, para poder testear ambas ramas sin
    /// depender del proceso real.
    /// </summary>
    public static string SelectArchitectureBinary(string path, Architecture architecture)
    {
        if (string.IsNullOrEmpty(path) || !path.Contains(Arm64QuantizedSuffix, StringComparison.Ordinal))
        {
            return path;
        }

        if (architecture == Architecture.Arm64)
        {
            return path;
        }

        return path.Replace(Arm64QuantizedSuffix, string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Directorio donde escribir los logs, en orden: <c>RAG_LOGS_DIR</c>, luego
    /// <c>&lt;raíz del repo&gt;/logs</c> si se encuentra el marcador de solución, y
    /// como último recurso <c>logs/</c> junto al binario. Nunca el directorio de
    /// trabajo: eso es lo que metía los logs dentro de <c>src/</c>.
    /// </summary>
    public static string ResolveLogsDirectory()
    {
        if (Environment.GetEnvironmentVariable(LogsDirVariable) is { Length: > 0 } configured)
        {
            return Expand(configured);
        }

        string? repoRoot = FindRepositoryRoot(AppContext.BaseDirectory);

        return repoRoot is not null
            ? Path.Combine(repoRoot, "logs")
            : Path.Combine(AppContext.BaseDirectory, "logs");
    }

    /// <summary>
    /// Busca hacia arriba el directorio que contiene el marcador de solución.
    /// Devuelve null si no aparece (caso de un publish fuera del repo).
    /// </summary>
    public static string? FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
