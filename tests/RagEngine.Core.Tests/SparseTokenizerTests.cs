using RagEngine.Core.Infrastructure.Vectorization;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// El tokenizador sparse no se prueba por su salida literal sino por sus
/// PROPIEDADES, porque lo que el sistema necesita de él no es un stem
/// lingüísticamente correcto: es simetría determinista entre índice y consulta.
/// El TermIndex es un MurmurHash3 del término normalizado, así que "la misma
/// palabra escrita de otra forma cae en el mismo bucket" es exactamente la
/// aserción que importa. Cualquier cambio en la normalización rompe esa simetría
/// y exige re-ingesta completa del corpus (~19 h), de modo que estos tests son
/// la única forma barata de detectarlo.
/// </summary>
public class SparseTokenizerTests
{
    private static readonly SparseTokenizer Tokenizer = new();

    private static uint SingleTerm(string text)
    {
        var entries = Tokenizer.Tokenize(text);
        Assert.Single(entries);
        return entries[0].TermIndex;
    }

    // ── Simetría de normalización ─────────────────────────────────────────────

    [Theory]
    [InlineData("auditoria", "auditorias")]
    [InlineData("regla", "reglas")]
    [InlineData("rule", "rules")]
    [InlineData("finding", "findings")]
    public void SingularYPlural_CaenEnElMismoTermino(string singular, string plural)
    {
        Assert.Equal(SingleTerm(singular), SingleTerm(plural));
    }

    [Theory]
    [InlineData("codigo", "código")]
    [InlineData("auditoria", "auditoría")]
    [InlineData("operacion", "operación")]
    public void ConYSinAcento_CaenEnElMismoTermino(string sinAcento, string conAcento)
    {
        Assert.Equal(SingleTerm(sinAcento), SingleTerm(conAcento));
    }

    [Theory]
    [InlineData("auditoria", "AUDITORIA")]
    [InlineData("auditoria", "Auditoria")]
    public void MayusculasYMinusculas_CaenEnElMismoTermino(string minuscula, string variante)
    {
        Assert.Equal(SingleTerm(minuscula), SingleTerm(variante));
    }

    [Fact]
    public void PalabrasDistintas_NoColisionanEntreSi()
    {
        // Negativo adversarial: si la normalización fuera demasiado agresiva,
        // términos sin relación acabarían en el mismo bucket y el recall sparse se
        // degradaría sin que ningún test lo notara.
        var terminos = new[] { "auditoria", "cliente", "factura", "pedido", "usuario" }
            .Select(SingleTerm)
            .ToArray();

        Assert.Equal(terminos.Length, terminos.Distinct().Count());
    }

    // ── Determinismo ──────────────────────────────────────────────────────────

    [Fact]
    public void MismoTexto_ProduceExactamenteElMismoVector()
    {
        const string texto = "El usuario que reportó la petición puede cancelar el ticket.";

        var primera = Tokenizer.Tokenize(texto);
        var segunda = Tokenizer.Tokenize(texto);

        Assert.Equal(primera.Count, segunda.Count);
        for (int i = 0; i < primera.Count; i++)
        {
            Assert.Equal(primera[i].TermIndex, segunda[i].TermIndex);
            Assert.Equal(primera[i].Weight, segunda[i].Weight);
        }
    }

    [Fact]
    public void SinTerminosRepetidos_EnLaSalida()
    {
        // Propiedad de MergeCollisions: un mismo TermIndex no puede aparecer dos
        // veces, o Qdrant recibiría un vector sparse malformado.
        var entries = Tokenizer.Tokenize(
            "auditoria auditoria auditorias AUDITORIA auditoría cliente cliente");

        var indices = entries.Select(e => e.TermIndex).ToArray();
        Assert.Equal(indices.Length, indices.Distinct().Count());
    }

    [Fact]
    public void TerminoRepetido_PesaMasQueUnoQueApareceUnaVez()
    {
        var entries = Tokenizer.Tokenize("factura factura factura cliente");

        var factura = entries.Single(e => e.TermIndex == SingleTerm("factura"));
        var cliente = entries.Single(e => e.TermIndex == SingleTerm("cliente"));

        Assert.True(factura.Weight > cliente.Weight,
            $"esperaba que el término repetido pesara más: factura={factura.Weight}, cliente={cliente.Weight}");
    }

    // ── Bordes ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void TextoVacioOEnBlanco_DevuelveVectorVacio(string texto)
    {
        Assert.Empty(Tokenizer.Tokenize(texto));
    }

    [Fact]
    public void PesosSiemprePositivos()
    {
        var entries = Tokenizer.Tokenize(
            "La auditoría de la operación genera un plan con reglas y hallazgos.");

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.True(e.Weight > 0f, $"peso no positivo: {e.Weight}"));
    }
}
