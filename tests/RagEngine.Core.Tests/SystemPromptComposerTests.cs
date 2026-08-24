using RagEngine.Core.Services.Generation;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Fija el CABLEADO de la composición del prompt, no su texto.
///
/// Por qué existe: <see cref="SystemPromptComposer.ComposeGrounded"/> nació al partir
/// <c>RagGenerationService</c> (ítem 2.2), y su `string.Format` tiene dos huecos
/// posicionales. Si alguien invierte los argumentos, el bloque de contexto pasa a ocupar
/// el sitio de la frase de fallback y viceversa: **todos** los prompts anclados salen
/// mal, el compilador calla, y <see cref="PromptHashesTests"/> sigue verde porque sólo
/// hashea las plantillas crudas, nunca el resultado de componerlas. Era el hueco más
/// afilado que dejaba la descomposición.
/// </summary>
public class SystemPromptComposerTests
{
    private const string PlantillaDePrueba = "CTX=[{0}] FALLBACK=[{1}] FIN";

    [Fact]
    public void ElContextoVaEnElPrimerHuecoYLaFraseDeFallbackEnElSegundo()
    {
        var compuesto = SystemPromptComposer.ComposeGrounded(
            PlantillaDePrueba, contextBlock: "BLOQUE-DE-CONTEXTO", confidenceAddendum: null);

        Assert.Equal(
            $"CTX=[BLOQUE-DE-CONTEXTO] FALLBACK=[{SystemPromptComposer.NoContextFallbackMessage}] FIN",
            compuesto);
    }

    [Fact]
    public void SinMatizDeConfianzaNoSeAnadeNada()
    {
        var compuesto = SystemPromptComposer.ComposeGrounded(PlantillaDePrueba, "ctx", null);

        Assert.DoesNotContain(SystemPromptComposer.LowConfidenceAddendum, compuesto);
        Assert.EndsWith("FIN", compuesto);
    }

    /// <summary>
    /// El matiz se CONCATENA al final, no se interpola: va después de las reglas de la
    /// plantilla precisamente para que las module en vez de quedar sepultado entre ellas.
    /// </summary>
    [Fact]
    public void ElMatizDeConfianzaSeAnadeAlFinal()
    {
        var compuesto = SystemPromptComposer.ComposeGrounded(
            PlantillaDePrueba, "ctx", SystemPromptComposer.LowConfidenceAddendum);

        Assert.EndsWith(SystemPromptComposer.LowConfidenceAddendum, compuesto);
        Assert.StartsWith("CTX=[ctx]", compuesto);
    }

    /// <summary>
    /// El camino sin anclaje no lleva contexto recuperado: su único hueco es la
    /// autodescripción. Si se colara ahí cualquier otra cosa, el modelo hablaría de sí
    /// mismo con datos ajenos.
    /// </summary>
    [Fact]
    public void ElPromptSinAnclajeIncrustaLaAutodescripcion()
    {
        var compuesto = SystemPromptComposer.ComposeNoGrounding();

        Assert.Contains(SystemPromptComposer.SelfDescriptionBlock, compuesto);
        Assert.DoesNotContain("{0}", compuesto);
    }
}
