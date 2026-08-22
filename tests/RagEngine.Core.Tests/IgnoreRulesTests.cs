using RagEngine.Core.Infrastructure.Scanning;
using Xunit;

namespace RagEngine.Core.Tests;

public class IgnoreRulesTests
{
    private static IgnoreRules Rules(params string[] lines) => IgnoreRules.FromLines(lines);

    [Theory]
    // Sin '/': coincide a cualquier nivel, archivo o directorio.
    [InlineData("logs", "logs", true, true)]
    [InlineData("logs", "src/RagEngine.Api/logs", true, true)]
    [InlineData("logs", "src/logs/rag-20260822.json", false, true)]
    [InlineData("logs", "catalogs/x.cs", false, false)]      // no es coincidencia parcial
    // Sólo-directorio.
    [InlineData("logs/", "logs", true, true)]
    [InlineData("logs/", "logs", false, false)]              // un ARCHIVO llamado "logs" no
    // Anclado a la raíz por llevar '/' interna.
    [InlineData("docs/eval", "docs/eval/baselines/x.json", false, true)]
    [InlineData("docs/eval", "src/docs/eval/x.json", false, false)]
    [InlineData("/build", "build/out.js", false, true)]
    [InlineData("/build", "src/build/out.js", false, false)]
    // Comodines que NO cruzan '/'.
    [InlineData("*.baseline.json", "docs/eval/a.baseline.json", false, true)]
    [InlineData("*.baseline.json", "docs/eval/a.json", false, false)]
    [InlineData("docs/*/tmp", "docs/eval/tmp", true, true)]
    [InlineData("docs/*/tmp", "docs/a/b/tmp", true, false)]  // '*' no cruza separador
    // '**' sí cruza.
    [InlineData("docs/**/tmp", "docs/a/b/tmp", true, true)]
    [InlineData("**/logs", "src/api/logs", true, true)]
    public void Coincidencias(string pattern, string path, bool isDir, bool expected)
        => Assert.Equal(expected, Rules(pattern).IsIgnored(path, isDir));

    [Fact]
    public void Comentarios_y_lineas_vacias_se_ignoran()
    {
        var r = Rules("# esto es un comentario", "", "   ", "logs");
        Assert.Equal(1, r.Count);
        Assert.True(r.IsIgnored("logs", true));
    }

    [Fact]
    public void Gana_la_ultima_regla_que_coincide()
    {
        var r = Rules("docs/eval", "!docs/eval/innovapp-docs.eval-set.json");
        Assert.True(r.IsIgnored("docs/eval/a.baseline.json", false));
        Assert.False(r.IsIgnored("docs/eval/innovapp-docs.eval-set.json", false));
    }

    [Fact]
    public void La_negacion_puede_reincluir_algo_excluido_por_gitignore()
    {
        // .gitignore aporta primero, .ragignore después: por eso puede revertirlo.
        var r = IgnoreRules.FromLines(["logs/", "!logs/importante.md"]);
        Assert.True(r.IsIgnored("logs/rag.json", false));
        Assert.False(r.IsIgnored("logs/importante.md", false));
    }

    [Fact]
    public void Excluir_un_directorio_excluye_su_contenido()
    {
        var r = Rules("docs/eval/baselines/");
        Assert.True(r.IsIgnored("docs/eval/baselines", true));
        Assert.True(r.IsIgnored("docs/eval/baselines/innovapp.json", false));
        Assert.False(r.IsIgnored("docs/eval/otro.json", false));
    }

    [Fact]
    public void Sin_reglas_no_excluye_nada()
    {
        Assert.False(IgnoreRules.Empty.IsIgnored("cualquier/cosa.cs", false));
        Assert.True(IgnoreRules.Empty.IsEmpty);
    }

    [Fact]
    public void Normaliza_separadores_de_Windows()
        => Assert.True(Rules("docs/eval").IsIgnored(@"docs\eval\a.json", false));
}
