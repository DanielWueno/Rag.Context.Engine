using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;

namespace RagEngine.Core.Tests;

public sealed class ModuleBoundaryTests
{
    [Theory]
    [InlineData("alpha", "", "alpha", true)]
    [InlineData("alpha.Foo", "", "alpha", true)]
    [InlineData("alpha.Foo.Bar", "", "alpha", true)]
    [InlineData(null, "alpha", "alpha", true)]
    [InlineData(null, "alpha/Foo.cs", "alpha", true)]
    [InlineData(null, "alpha/child/Foo.cs", "alpha", true)]
    [InlineData("alpha-private", "", "alpha", false)]
    [InlineData(null, "alpha-private/Secrets.cs", "alpha", false)]
    [InlineData("alpha-oculto", "alpha-oculto/Archivo.cs", "alpha", false)]
    [InlineData("beta", "beta/Foo.cs", "alpha", false)]
    [InlineData("notalpha", "notalpha/Foo.cs", "alpha", false)]
    [InlineData("Company.alpha", "src/alpha/Foo.cs", "alpha", false)]
    [InlineData("alpha/Foo", "alpha.Foo.cs", "alpha", false)]
    [InlineData("Alpha", "Alpha/Foo.cs", "alpha", false)]
    [InlineData(null, "", "alpha", false)]
    [InlineData("", "", "alpha", false)]
    [InlineData("alpha", "beta/Foo.cs", "alpha", true)]
    [InlineData("beta", "alpha/Foo.cs", "alpha", true)]
    [InlineData("alpha.Foo", "", "alpha.Foo", true)]
    [InlineData("alpha.Foo.Bar", "", "alpha.Foo", true)]
    [InlineData("alpha.Foobar", "", "alpha.Foo", false)]
    [InlineData(null, "alpha/child/Foo.cs", "alpha/child", true)]
    [InlineData(null, "alpha/children/Foo.cs", "alpha/child", false)]
    [InlineData("alpha", "alpha/Foo.cs", "", false)]
    [InlineData("alpha", "alpha/Foo.cs", " ", false)]
    public void MatchesModuleBoundary_respects_exact_roots_and_descendants(
        string? namespaceName, string relativePath, string module, bool expected)
    {
        var metadata = new CodeChunkMetadata(
            "/repo/file.cs", relativePath, SourceLanguage.CSharp, namespaceName,
            null, null, 1, 1, DateTimeOffset.UnixEpoch, "fixture");

        Assert.Equal(expected, QdrantSemanticRetriever.MatchesModuleBoundary(metadata, module));
    }
}
