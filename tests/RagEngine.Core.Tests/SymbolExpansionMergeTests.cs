using RagEngine.Core.Infrastructure.VectorStore;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 6.a — aritmética pura de <see cref="QdrantSemanticRetriever.MergeSymbolExpansionRanks"/>,
/// sin Qdrant de por medio. Existe porque una revisión externa (claude-opus-5, antes de
/// cerrar 6.a) detectó que una primera versión de este método era un no-op matemático:
/// con <c>k=RetrievalFusionOptions.RrfK</c> (60, calibrado para ramas de ~4×TopK) y
/// <c>Weight&lt;0.87</c>, ningún candidato del segundo salto podía superar jamás a un
/// candidato de la fusión primaria dentro del top-TopK — un test de esta aritmética,
/// con rangos sintéticos, lo habría detectado en segundos sin correr el eval-set contra
/// Qdrant real. Ver el hallazgo "B1" documentado en el resultado del ítem 6.a del ledger.
/// </summary>
public class SymbolExpansionMergeTests
{
    /// <summary>
    /// El escenario que el bug original volvía imposible: un candidato del salto en la
    /// mejor posición posible (rango 1) debe poder desplazar a un candidato primario
    /// débil (cola del pool), cuando <c>k</c> está calibrado a la escala de TopK y no a
    /// la escala de la fusión primaria (60).
    /// </summary>
    [Fact]
    public void UnCandidatoDelSaltoEnRango1_DesplazaAUnPrimarioDebilConKDeEscalaTopK()
    {
        // Pool primario de 10 (TopK=10 sin rerank), el salto encuentra un solo punto
        // nuevo que no estaba en la fusión primaria, en su mejor posición posible.
        var primaryRank = Enumerable.Range(1, 10)
            .ToDictionary(r => $"primario-{r}", r => r);
        var hopRank = new Dictionary<string, int> { ["salto-nuevo"] = 1 };

        var merged = QdrantSemanticRetriever.MergeSymbolExpansionRanks(
            primaryRank, hopRank, weight: 0.8, k: 10, take: 10);

        Assert.Equal(10, merged.Count);
        Assert.Contains("salto-nuevo", merged);
        // El desplazado debe ser el peor primario (rango 10), no uno de los mejores:
        // el salto sólo debe poder competir por la cola.
        Assert.DoesNotContain("primario-10", merged);
        Assert.Contains("primario-1", merged);
        Assert.Contains("primario-2", merged);
        Assert.Contains("primario-3", merged);
    }

    /// <summary>
    /// Reproduce EXACTAMENTE la configuración con la que el bug original medía cero
    /// efecto en las 84 preguntas del eval-set: k=60 (RrfK de la fusión primaria,
    /// reusado por error) con Weight=0.8. Ningún candidato del salto, ni en su mejor
    /// posición posible, debe poder entrar al resultado final — documenta el no-op
    /// detectado, para que nadie vuelva a reintroducir <c>_fusionOptions.RrfK</c> aquí
    /// sin que este test lo señale.
    /// </summary>
    [Fact]
    public void ConKDeLaFusionPrimaria60_YPesoPorDebajoDelUmbral_ElSaltoEsUnNoOp()
    {
        var primaryRank = Enumerable.Range(1, 10)
            .ToDictionary(r => $"primario-{r}", r => r);
        var hopRank = new Dictionary<string, int> { ["salto-nuevo"] = 1 };

        var merged = QdrantSemanticRetriever.MergeSymbolExpansionRanks(
            primaryRank, hopRank, weight: 0.8, k: 60, take: 10);

        Assert.DoesNotContain("salto-nuevo", merged);
        Assert.Equal(
            Enumerable.Range(1, 10).Select(r => $"primario-{r}"),
            merged);
    }

    /// <summary>
    /// Un id presente en AMBAS listas (consenso: cercano semánticamente Y define un
    /// símbolo consumido por la semilla) debe sumar los dos términos y quedar mejor
    /// posicionado que uno presente en una sola lista con rangos comparables — la señal
    /// que una primera versión del método destruía al deduplicar antes de rankear.
    /// </summary>
    [Fact]
    public void UnIdEnAmbasListas_SumaLosDosTerminosYQuedaPorEncimaDeUnoEnUnaSola()
    {
        var primaryRank = new Dictionary<string, int>
        {
            ["consenso"] = 5,
            ["solo-primario"] = 4,
        };
        var hopRank = new Dictionary<string, int>
        {
            ["consenso"] = 5,
            ["solo-salto"] = 4,
        };

        var merged = QdrantSemanticRetriever.MergeSymbolExpansionRanks(
            primaryRank, hopRank, weight: 1.0, k: 10, take: 4);

        var posicionConsenso = merged.IndexOf("consenso");
        var posicionSoloPrimario = merged.IndexOf("solo-primario");
        var posicionSoloSalto = merged.IndexOf("solo-salto");

        Assert.True(posicionConsenso >= 0 && posicionSoloPrimario >= 0 && posicionSoloSalto >= 0);
        Assert.True(posicionConsenso < posicionSoloPrimario);
        Assert.True(posicionConsenso < posicionSoloSalto);
    }

    [Fact]
    public void RespetaElLimiteDeTake()
    {
        var primaryRank = Enumerable.Range(1, 5).ToDictionary(r => $"p{r}", r => r);
        var hopRank = Enumerable.Range(1, 5).ToDictionary(r => $"h{r}", r => r);

        var merged = QdrantSemanticRetriever.MergeSymbolExpansionRanks(
            primaryRank, hopRank, weight: 1.0, k: 10, take: 3);

        Assert.Equal(3, merged.Count);
    }
}
