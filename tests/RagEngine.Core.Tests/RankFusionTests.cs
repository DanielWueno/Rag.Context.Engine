using RagEngine.Core.Domain;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 9.7 — <see cref="RankFusion"/> es el único núcleo de RRF ponderada usado tanto
/// por <c>QdrantSemanticRetriever.SearchWeightedFusionAsync</c> (producción, IDs
/// <see cref="string"/>) como por <c>RecallEvaluator.RankByRrf</c> (calibración del PoC,
/// IDs <see cref="int"/>). Estas pruebas fijan el comportamiento del núcleo directamente
/// —listas vacías, candidatos compartidos, empates y pesos cero/no uniformes— con ambos
/// tipos de ID, para que un cambio que reintroduzca una copia divergente en cualquiera de
/// los dos consumidores (en vez de llamar a este método) rompa aquí primero.
/// </summary>
public class RankFusionTests
{
    private static RankFusion.Branch<string> Branch(double weight, params (string Id, int Rank)[] entries) =>
        new(entries.ToDictionary(e => e.Id, e => e.Rank), weight);

    private static RankFusion.Branch<int> Branch(double weight, params (int Id, int Rank)[] entries) =>
        new(entries.ToDictionary(e => e.Id, e => e.Rank), weight);

    /// <summary>Sin candidatos y sin ramas, no hay resultado ni excepción.</summary>
    [Fact]
    public void ListaVacia_DevuelveResultadoVacio()
    {
        var resultado = RankFusion.Fuse(
            Array.Empty<string>(), k: 60, branches: [], tieBreak: StringComparer.Ordinal);

        Assert.Empty(resultado);
    }

    /// <summary>Candidatos sin ninguna rama que los mencione puntúan 0, no se descartan.</summary>
    [Fact]
    public void CandidatosSinRamas_PuntuanCeroYNoDesaparecen()
    {
        var resultado = RankFusion.Fuse(
            ["a", "b"], k: 60, branches: [], tieBreak: StringComparer.Ordinal);

        Assert.Equal(2, resultado.Count);
        Assert.All(resultado, r => Assert.Equal(0.0, r.Score));
        // Sin ramas que los distingan, el desempate decide el orden: "a" antes que "b".
        Assert.Equal(["a", "b"], resultado.Select(r => r.Id).ToArray());
    }

    /// <summary>
    /// Dos candidatos compartidos por dos ramas con pesos uniformes: el score es la suma
    /// exacta de 1/(k+rango) por rama, y el orden sigue el score, no el orden de entrada.
    /// </summary>
    [Fact]
    public void CandidatosCompartidos_SumaLosTerminosDeCadaRama()
    {
        var densa = Branch(weight: 1.0, ("x", 1), ("y", 2));
        var dispersa = Branch(weight: 1.0, ("x", 2), ("y", 1));

        var resultado = RankFusion.Fuse(
            ["x", "y"], k: 60, branches: [densa, dispersa], tieBreak: StringComparer.Ordinal);

        var esperadoX = 1.0 / 61 + 1.0 / 62;
        var esperadoY = 1.0 / 62 + 1.0 / 61;

        Assert.Equal(esperadoX, resultado.Single(r => r.Id == "x").Score, precision: 12);
        Assert.Equal(esperadoY, resultado.Single(r => r.Id == "y").Score, precision: 12);
        // Simétrico por construcción: mismo score exacto, empate real.
        Assert.Equal(resultado[0].Score, resultado[1].Score);
    }

    /// <summary>
    /// Empate exacto de score: el desempate explícito decide, no el orden de
    /// enumeración de <c>candidateIds</c>. Se pasan los candidatos en orden inverso al
    /// del desempate para demostrar que el resultado no depende de ese orden de entrada.
    /// </summary>
    [Fact]
    public void EmpateExacto_LoDecideElDesempateNoElOrdenDeEntrada()
    {
        var unica = Branch(weight: 1.0, ("gem1", 1), ("gem2", 1));

        var resultado = RankFusion.Fuse(
            ["gem2", "gem1"], k: 60, branches: [unica], tieBreak: StringComparer.Ordinal);

        Assert.Equal(resultado[0].Score, resultado[1].Score);
        Assert.Equal(["gem1", "gem2"], resultado.Select(r => r.Id).ToArray());
    }

    /// <summary>Peso cero anula la contribución de esa rama sin lanzar ni distorsionar el resto.</summary>
    [Fact]
    public void PesoCero_AnulaLaRamaSinAfectarLasDemas()
    {
        var conPeso = Branch(weight: 1.0, ("a", 1), ("b", 2));
        var pesoCero = Branch(weight: 0.0, ("a", 2), ("b", 1)); // invertiría el orden si contara

        var resultado = RankFusion.Fuse(
            ["a", "b"], k: 60, branches: [conPeso, pesoCero], tieBreak: StringComparer.Ordinal);

        Assert.Equal(1.0 / 61, resultado.Single(r => r.Id == "a").Score, precision: 12);
        Assert.Equal(1.0 / 62, resultado.Single(r => r.Id == "b").Score, precision: 12);
        Assert.Equal("a", resultado[0].Id);
    }

    /// <summary>
    /// Pesos no uniformes: la rama con más peso puede voltear el orden de una rama con
    /// más peso pero rango peor. Distingue "aplica los pesos" de "sólo suma rangos".
    /// </summary>
    [Fact]
    public void PesosNoUniformes_LaRamaConMasPesoPuedeVoltearElOrden()
    {
        // ramaA (peso 3.0) favorece a "b" (rango 1 vs 2); ramaB (peso 0.1) favorece a "a".
        var ramaA = Branch(weight: 3.0, ("a", 2), ("b", 1));
        var ramaB = Branch(weight: 0.1, ("a", 1), ("b", 2));

        var resultado = RankFusion.Fuse(
            ["a", "b"], k: 60, branches: [ramaA, ramaB], tieBreak: StringComparer.Ordinal);

        // El peso dominante de ramaA decide: "b" gana pese a que "a" rankea mejor en ramaB.
        var scoreA = 3.0 / 62 + 0.1 / 61;
        var scoreB = 3.0 / 61 + 0.1 / 62;
        Assert.Equal(scoreA, resultado.Single(r => r.Id == "a").Score, precision: 12);
        Assert.Equal(scoreB, resultado.Single(r => r.Id == "b").Score, precision: 12);
        Assert.Equal("b", resultado[0].Id);
    }

    /// <summary>
    /// Mutar un solo peso rompe la equivalencia esperada: la prueba que demuestra que el
    /// núcleo SÍ reacciona a los pesos y no ignora silenciosamente una rama.
    /// </summary>
    [Fact]
    public void MutarUnPeso_CambiaElRankingFinal()
    {
        var ramaA = Branch(weight: 1.0, ("a", 1), ("b", 2));
        var ramaB = Branch(weight: 1.0, ("a", 2), ("b", 1));

        var baseline = RankFusion.Fuse(["a", "b"], k: 60, branches: [ramaA, ramaB], tieBreak: StringComparer.Ordinal);
        Assert.Equal(baseline[0].Score, baseline[1].Score); // empate simétrico, punto de partida

        var ramaBMutada = Branch(weight: 5.0, ("a", 2), ("b", 1)); // ↑ peso de la rama que favorece a "b"
        var mutado = RankFusion.Fuse(["a", "b"], k: 60, branches: [ramaA, ramaBMutada], tieBreak: StringComparer.Ordinal);

        Assert.Equal("b", mutado[0].Id);
        Assert.NotEqual(baseline.Select(r => r.Score), mutado.Select(r => r.Score));
    }

    /// <summary>
    /// Mismo escenario que las pruebas anteriores pero con <see cref="int"/> como tipo de
    /// ID — el que usa <c>RecallEvaluator</c> — y <see cref="Comparer{T}.Default"/> como
    /// desempate, para fijar que el núcleo es igual de determinista con enteros.
    /// </summary>
    [Fact]
    public void ConIdsEnteros_EmpateExactoLoDecideElDesempateNumerico()
    {
        var unica = Branch(weight: 1.0, (2, 1), (1, 1));

        var resultado = RankFusion.Fuse(
            [2, 1], k: 60, branches: [unica], tieBreak: Comparer<int>.Default);

        Assert.Equal(resultado[0].Score, resultado[1].Score);
        Assert.Equal([1, 2], resultado.Select(r => r.Id).ToArray());
    }
}
