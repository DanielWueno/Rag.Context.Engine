using RagEngine.Core.Domain;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// La redacción de fuentes en modo Simple se decide con esta clasificación, así que un
/// cambio aquí cambia lo que ve un usuario no técnico. El caso que motivó la regla: una
/// colección de wiki (100 % Markdown) devolvía fuentes con sólo el score, inservibles.
/// </summary>
public class SourceLanguageProseTests
{
    [Theory]
    [InlineData(SourceLanguage.Markdown, true)]
    // Código: la redacción sí protege, se mantiene.
    [InlineData(SourceLanguage.CSharp, false)]
    [InlineData(SourceLanguage.TypeScript, false)]
    [InlineData(SourceLanguage.JavaScript, false)]
    [InlineData(SourceLanguage.Xaml, false)]
    [InlineData(SourceLanguage.Sql, false)]
    // PlainText agrupa .txt con .json/.xml/.csproj: indistinguibles aquí, se redacta.
    [InlineData(SourceLanguage.PlainText, false)]
    [InlineData(SourceLanguage.Unknown, false)]
    public void ClasificaProsaSoloDondeNoHayCodigoCrudo(SourceLanguage lenguaje, bool esperado)
        => Assert.Equal(esperado, lenguaje.IsProse());

    [Fact]
    public void TodoLenguajeNuevoEmpiezaSiendoNoProsa()
    {
        // Si se agrega un lenguaje al enum y resulta ser prosa, hay que decidirlo
        // explícitamente: el default seguro es redactar.
        var prosa = Enum.GetValues<SourceLanguage>().Where(l => l.IsProse()).ToArray();
        Assert.Equal([SourceLanguage.Markdown], prosa);
    }
}
