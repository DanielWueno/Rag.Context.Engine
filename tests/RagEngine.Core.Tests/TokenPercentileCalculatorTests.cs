using RagEngine.Core.Diagnostics;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Percentil nearest-rank sobre conteos de tokens reales (ítem 11.1). Aislado
/// de la tokenización real y del pipeline: cubre solo la aritmética exigida
/// por el oráculo de aceptación (n=0, off-by-one, indexado 1-based).
/// </summary>
public sealed class TokenPercentileCalculatorTests
{
    [Fact]
    public void Compute_ConMuestraVacia_NoEmitePercentilesFicticios()
    {
        var (n, p50, p95) = TokenPercentileCalculator.Compute([]);

        Assert.Equal(0, n);
        Assert.Null(p50);
        Assert.Null(p95);
    }

    // Oráculo: para T=[L-1,L,L+1] (n=3), p50=L, p95=L+1 vía nearest-rank
    // (índice=ceil(p*n), 1-indexado). L es arbitrario aquí: la aritmética no
    // depende del tokenizador, solo de los valores.
    [Theory]
    [InlineData(8)]   // L pequeño (equivalente a un MaxSequenceLength de prueba)
    [InlineData(256)] // L de un perfil SentencePiece real (MaxSequenceLength - 2)
    [InlineData(510)] // L de un perfil WordPiece con MaxSequenceLength=512
    public void Compute_ConTripletaLMenosUnoLYLMasUno_DaP50IgualAL_YP95IgualALMasUno(int l)
    {
        var (n, p50, p95) = TokenPercentileCalculator.Compute([l - 1, l, l + 1]);

        Assert.Equal(3, n);
        Assert.Equal(l, p50);
        Assert.Equal(l + 1, p95);
    }

    [Fact]
    public void Compute_ConUnSoloValor_P50YP95IgualAEseValor()
    {
        var (n, p50, p95) = TokenPercentileCalculator.Compute([42]);

        Assert.Equal(1, n);
        Assert.Equal(42, p50);
        Assert.Equal(42, p95);
    }

    [Fact]
    public void Compute_EsInsensibleAlOrdenDeEntrada()
    {
        var ordenAscendente = TokenPercentileCalculator.Compute([7, 8, 9]);
        var ordenDescendente = TokenPercentileCalculator.Compute([9, 8, 7]);

        Assert.Equal(ordenAscendente, ordenDescendente);
    }

    // n=20: p50 -> índice ceil(0.5*20)=10; p95 -> índice ceil(0.95*20)=19.
    [Fact]
    public void Compute_ConVeinteValores_UsaIndiceNearestRankCorrecto()
    {
        var valores = Enumerable.Range(1, 20).ToList(); // 1..20

        var (n, p50, p95) = TokenPercentileCalculator.Compute(valores);

        Assert.Equal(20, n);
        Assert.Equal(10, p50); // valores[10-1] tras ordenar = 10
        Assert.Equal(19, p95); // valores[19-1] tras ordenar = 19
    }
}
