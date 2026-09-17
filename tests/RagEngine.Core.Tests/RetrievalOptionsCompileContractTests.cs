using System.Diagnostics;
using RagEngine.Core.Tests.Calibration;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Verifica por compilación real el contrato exigido por 9.1: construir
/// <c>RetrievalOptions</c> sin contexto explícito debe fallar ANTES de correr.
/// </summary>
public sealed class RetrievalOptionsCompileContractTests
{
    [Fact]
    public void RetrievalOptions_sin_contexto_explicito_no_compila()
    {
        var repoRoot = RepoRootLocator.Find();
        var project = Path.Combine(
            repoRoot,
            "tests",
            "RagEngine.Core.Tests",
            "CompileFixtures",
            "RetrievalOptionsMissingContext",
            "RetrievalOptionsMissingContext.csproj");

        var psi = new ProcessStartInfo("dotnet", $"build \"{project}\" --nologo")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("No se pudo lanzar 'dotnet build' para el fixture de compilación.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var output = stdout + Environment.NewLine + stderr;

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("RetrievalOptions", output, StringComparison.Ordinal);
        Assert.Contains("Context", output, StringComparison.Ordinal);
    }
}
