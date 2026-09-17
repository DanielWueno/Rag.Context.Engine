using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace RagEngine.Architecture.Tests;

/// <summary>
/// Ítem 9.5: RagEngine.Core es un único ensamblado con Qdrant/ONNX/SemanticKernel/
/// SQLite dentro. Nada validaba la DIRECCIÓN de esas dependencias — que las capas de
/// aplicación (Domain/Abstractions/Services/Pipeline) no las nombren directamente, y
/// que los hosts (Api/Cli) no puedan nombrarlas en absoluto, dejando esa
/// responsabilidad exclusivamente a Infrastructure/.
///
/// Mecanismo: se parsea el árbol de sintaxis (Roslyn) de cada archivo .cs de las
/// carpetas vigiladas y se listan sus directivas <c>using</c> (incluye <c>global
/// using</c>). Si alguna nombra uno de los cuatro paquetes prohibidos, el test falla
/// con el archivo y la línea exactos.
///
/// LÍMITE CONOCIDO (documentado también en el rollback de la ficha): una regla por
/// <c>using</c> no detecta una referencia totalmente calificada sin directiva (p.ej.
/// <c>Microsoft.Data.Sqlite.SqliteConnection</c> escrito así, sin
/// <c>using Microsoft.Data.Sqlite;</c>), ni una dependencia oculta detrás de una
/// clase estática pública (p.ej. una métrica) o una constante pública de una clase
/// concreta reexportada desde otra capa. La dirección de dependencias NO queda
/// "congelada" más allá de lo que esta regla efectivamente mira.
/// </summary>
public sealed class DependencyDirectionTests
{
    /// <summary>
    /// Ensamblados/paquetes que sólo Infrastructure/ (dentro de Core) puede nombrar.
    /// Ninguna capa de aplicación de Core ni ningún host puede referenciarlos
    /// directamente.
    /// </summary>
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Qdrant.Client",
        "Microsoft.ML.OnnxRuntime",
        "Microsoft.Data.Sqlite",
        "Microsoft.SemanticKernel"
    ];

    public static TheoryData<string> CoreApplicationLayerFolders => new()
    {
        "Domain",
        "Abstractions",
        "Services",
        "Pipeline"
    };

    [Theory]
    [MemberData(nameof(CoreApplicationLayerFolders))]
    public void La_capa_de_aplicacion_de_Core_no_referencia_adaptadores_concretos(string folder)
    {
        var repoRoot = RepoRootLocator.Find();
        var folderPath = Path.Combine(repoRoot, "src", "RagEngine.Core", folder);

        Assert.True(Directory.Exists(folderPath), $"No existe la carpeta vigilada: {folderPath}");

        AssertNoForbiddenUsings(folderPath);
    }

    [Fact]
    public void RagEngineApi_no_referencia_adaptadores_concretos()
    {
        var repoRoot = RepoRootLocator.Find();
        AssertNoForbiddenUsings(Path.Combine(repoRoot, "src", "RagEngine.Api"));
    }

    [Fact]
    public void RagEngineCli_no_referencia_adaptadores_concretos()
    {
        var repoRoot = RepoRootLocator.Find();
        AssertNoForbiddenUsings(Path.Combine(repoRoot, "src", "RagEngine.Cli"));
    }

    /// <summary>
    /// Control negativo del propio arnés: si esta lista queda vacía porque el
    /// escaneo de directorios se rompió silenciosamente (ruta mal escrita, filtro
    /// de bin/obj demasiado agresivo, etc.), el test de arriba "pasaría" sin haber
    /// mirado nada. Fija un mínimo de archivos reales inspeccionados.
    /// </summary>
    [Fact]
    public void El_escaneo_de_RagEngineApi_inspecciona_archivos_reales()
    {
        var repoRoot = RepoRootLocator.Find();
        var files = EnumerateSourceFiles(Path.Combine(repoRoot, "src", "RagEngine.Api")).ToList();
        Assert.True(files.Count >= 3, $"Se esperaban varios .cs reales en RagEngine.Api, se encontraron {files.Count}.");
    }

    private static void AssertNoForbiddenUsings(string rootPath)
    {
        var violations = ForbiddenUsingScanner.FindViolations(rootPath, ForbiddenNamespacePrefixes);

        Assert.True(
            violations.Count == 0,
            "Dirección de dependencias violada. La capa/host no puede nombrar un " +
            "adaptador concreto directamente (sólo Infrastructure/ puede):" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> EnumerateSourceFiles(string rootPath) =>
        ForbiddenUsingScanner.EnumerateSourceFiles(rootPath);
}

/// <summary>
/// Lógica de escaneo extraída de <see cref="DependencyDirectionTests"/> para que
/// <see cref="DependencyDirectionMutantControlTests"/> pueda ejercitarla
/// directamente sobre fixtures y demostrar que la regla SÍ detecta una violación
/// (control rojo), sin duplicar el árbol de sintaxis.
/// </summary>
internal static class ForbiddenUsingScanner
{
    public static List<string> FindViolations(string rootPath, IReadOnlyList<string> forbiddenNamespacePrefixes)
    {
        var violations = new List<string>();

        foreach (var file in EnumerateSourceFiles(rootPath))
        {
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text, path: file);
            var root = tree.GetCompilationUnitRoot();

            foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                var name = usingDirective.Name?.ToString();
                if (name is null)
                    continue;

                if (forbiddenNamespacePrefixes.Any(forbidden =>
                        name.Equals(forbidden, StringComparison.Ordinal) ||
                        name.StartsWith(forbidden + ".", StringComparison.Ordinal)))
                {
                    var line = usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    violations.Add($"{Path.GetRelativePath(rootPath, file)}:{line} -> using {name};");
                }
            }
        }

        return violations;
    }

    public static IEnumerable<string> EnumerateSourceFiles(string rootPath) =>
        Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
                !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
