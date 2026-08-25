using RagEngine.Core.Domain;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// La plantilla de sistema y etiquetado de cabecera del chunk se deciden con esta
/// clasificación, así que un cambio aquí afecta cómo se presenta la documentación.
/// </summary>
public class SourceLanguageDocumentationTests
{
    [Theory]
    [InlineData(SourceLanguage.Markdown, true)]
    [InlineData(SourceLanguage.PlainText, true)]
    // Código: no se etiqueta como documentación.
    [InlineData(SourceLanguage.CSharp, false)]
    [InlineData(SourceLanguage.TypeScript, false)]
    [InlineData(SourceLanguage.JavaScript, false)]
    [InlineData(SourceLanguage.Xaml, false)]
    [InlineData(SourceLanguage.Sql, false)]
    [InlineData(SourceLanguage.Unknown, false)]
    public void ClasificaDocumentacionEnMarkdownYPlainText(SourceLanguage lenguaje, bool esperado)
        => Assert.Equal(esperado, lenguaje.IsDocumentation());

    [Fact]
    public void TodoLenguajeNuevoEmpiezaSiendoNoDocumentacion()
    {
        // Si se agrega un lenguaje al enum y resulta ser documentación, hay que decidirlo
        // explícitamente: el default seguro es código.
        var documentacion = Enum.GetValues<SourceLanguage>().Where(l => l.IsDocumentation()).ToArray();
        Assert.Equal([SourceLanguage.Markdown, SourceLanguage.PlainText], documentacion);
    }
}
