using System.Diagnostics;
using System.Text.RegularExpressions;
using RagEngine.Core.Tests.Calibration;
using Xunit;
using Xunit.Abstractions;

namespace RagEngine.Core.Tests;

/// <summary>
/// Verifica por compilación real el contrato exigido por 9.1: construir
/// <c>RetrievalOptions</c> sin contexto explícito debe fallar ANTES de correr.
/// </summary>
public sealed class RetrievalOptionsCompileContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RetrievalOptions_sin_contexto_explicito_no_compila()
    {
        var (exitCode, buildOutput) = await BuildFixtureAsync("RetrievalOptionsMissingContext");

        Assert.NotEqual(0, exitCode);
        var diagnostics = Regex.Matches(buildOutput, @"error (CS\d+):[^\r\n]*");
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics.Cast<Match>(), diagnostic =>
        {
            Assert.Equal("CS9035", diagnostic.Groups[1].Value);
            Assert.Contains("'RetrievalOptions.Context'", diagnostic.Value, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RetrievalOptions_con_contexto_local_compila()
    {
        var (exitCode, buildOutput) = await BuildFixtureAsync("RetrievalOptionsWithContext");

        Assert.True(exitCode == 0, buildOutput);
        Assert.DoesNotMatch(@"error [A-Z]+\d+:", buildOutput);
    }

    private async Task<(int ExitCode, string Output)> BuildFixtureAsync(string fixture)
    {
        var repoRoot = RepoRootLocator.Find();
        var project = Path.Combine(
            repoRoot,
            "tests",
            "RagEngine.Core.Tests",
            "CompileFixtures",
            fixture,
            $"{fixture}.csproj");

        var psi = new ProcessStartInfo("dotnet", $"build \"{project}\" --nologo")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("No se pudo lanzar 'dotnet build' para el fixture de compilación.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var buildOutput = await stdout + Environment.NewLine + await stderr;
        output.WriteLine(buildOutput);
        return (process.ExitCode, buildOutput);
    }
}
