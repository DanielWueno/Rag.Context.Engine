using Xunit;

namespace RagEngine.Architecture.Tests;

/// <summary>
/// Control rojo del arnés de 9.5: demuestra que <see cref="ForbiddenUsingScanner"/>
/// SÍ detecta una violación real (mutante en <c>Mutants/ViolatesDependencyDirection.cs</c>,
/// nunca compilado) y que no dispara falsos positivos sobre código legítimo
/// (<c>Mutants/RespectsDependencyDirection.cs</c>). Sin este control, un cambio que
/// vaciara accidentalmente <see cref="DependencyDirectionTests"/> (por ejemplo, un
/// filtro de exclusión demasiado amplio) podría dejar la guardia "verde" sin que
/// esté mirando nada.
/// </summary>
public sealed class DependencyDirectionMutantControlTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "Qdrant.Client",
        "Microsoft.ML.OnnxRuntime",
        "Microsoft.Data.Sqlite",
        "Microsoft.SemanticKernel"
    ];

    [Fact]
    public void El_escaner_detecta_el_mutante_con_adaptadores_concretos()
    {
        var mutantsPath = Path.Combine(RepoRootLocator.Find(),
            "tests", "RagEngine.Architecture.Tests", "Mutants");

        var violations = ForbiddenUsingScanner.FindViolations(mutantsPath, ForbiddenNamespacePrefixes);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.Contains("ViolatesDependencyDirection.cs") && v.Contains("Qdrant.Client"));
        Assert.Contains(violations, v => v.Contains("ViolatesDependencyDirection.cs") && v.Contains("Microsoft.SemanticKernel"));
        Assert.DoesNotContain(violations, v => v.Contains("RespectsDependencyDirection.cs"));
    }
}
