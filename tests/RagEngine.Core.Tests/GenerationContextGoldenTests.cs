using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RagEngine.Core.Services.Generation;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Fija byte a byte el bloque de contexto y la plantilla elegida.
///
/// Por qué existe: el ítem 2.2 del plan partió <c>RagGenerationService</c> en
/// colaboradores, y "mover código sin cambiar comportamiento" no se puede afirmar
/// sin medirlo. El golden de <c>GoldenMaster/generacion-contexto.json</c> se capturó
/// ejecutando <see cref="GenerationGoldenCasos"/> contra el código PREVIO a la
/// descomposición, por reflexión sobre los estáticos privados que entonces vivían en
/// el servicio. Si estos tests pasan, la descomposición no movió una coma.
///
/// La plantilla se compara por hash, no por texto: el texto ya está fijado en
/// <see cref="PromptHashesTests"/> y duplicarlo aquí obligaría a actualizarlo en dos
/// sitios. Lo que este test aporta es <em>cuál</em> de las plantillas se elige.
/// </summary>
public class GenerationContextGoldenTests
{
    private static readonly Dictionary<string, string> Golden = Cargar();

    private static Dictionary<string, string> Cargar()
    {
        var ruta = Path.Combine(AppContext.BaseDirectory, "GoldenMaster", "generacion-contexto.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ruta))!;
    }

    private static string Sha(string valor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valor)))[..16].ToLowerInvariant();

    public static TheoryData<string> NombresDeContexto()
    {
        var datos = new TheoryData<string>();
        foreach (var (nombre, _) in GenerationGoldenCasos.Contexto)
            datos.Add(nombre);
        return datos;
    }

    public static TheoryData<string> NombresDeSeleccion()
    {
        var datos = new TheoryData<string>();
        foreach (var (nombre, _, _) in GenerationGoldenCasos.Seleccion)
            datos.Add(nombre);
        return datos;
    }

    [Theory]
    [MemberData(nameof(NombresDeContexto))]
    public void ElBloqueDeContextoNoCambio(string nombre)
    {
        var chunks = GenerationGoldenCasos.Contexto.Single(c => c.Nombre == nombre).Chunks;

        var real = GenerationContextAssembler.BuildContextBlock(chunks);

        Assert.Equal(Golden["contexto:" + nombre], real);
    }

    [Theory]
    [MemberData(nameof(NombresDeSeleccion))]
    public void LaPlantillaElegidaNoCambio(string nombre)
    {
        var caso = GenerationGoldenCasos.Seleccion.Single(c => c.Nombre == nombre);

        var real = SystemPromptComposer.SelectTemplate(caso.Chunks, caso.Modo);

        Assert.Equal(Golden["plantilla:" + nombre], Sha(real));
    }

    /// <summary>
    /// El score de la cabecera del chunk se formatea con <c>CultureInfo.InvariantCulture</c>
    /// explícito en producción (ver <c>GenerationContextAssembler.BuildChunkHeader</c>), así
    /// que el resultado no debe depender de <c>CurrentCulture</c>. Este test lo fija bajo
    /// de-DE, donde el separador decimal es coma: si el formateo interno perdiera la cultura
    /// explícita, "Score: 0.91" se convertiría en "Score: 0,91" y este test lo detectaría.
    /// </summary>
    [Fact]
    public void ElScoreDeLaCabeceraEsInvariantePorCulturaDeDe()
    {
        var previa = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var chunks = GenerationGoldenCasos.Contexto.Single(c => c.Nombre == "un-chunk-de-codigo-completo").Chunks;

            var real = GenerationContextAssembler.BuildContextBlock(chunks);

            Assert.Contains("Score: 0.91", real);
            Assert.DoesNotContain("Score: 0,91", real);
        }
        finally
        {
            CultureInfo.CurrentCulture = previa;
        }
    }

    /// <summary>
    /// El golden y los casos tienen que cubrir lo mismo: si alguien añade un caso y
    /// olvida regenerar el golden, o borra uno del golden, esto lo dice en vez de
    /// dejar la cobertura silenciosamente menguada.
    /// </summary>
    [Fact]
    public void ElGoldenCubreExactamenteLosCasosDeclarados()
    {
        var esperadas = GenerationGoldenCasos.Contexto.Select(c => "contexto:" + c.Nombre)
            .Concat(GenerationGoldenCasos.Seleccion.Select(c => "plantilla:" + c.Nombre))
            .OrderBy(x => x, StringComparer.Ordinal);

        Assert.Equal(esperadas, Golden.Keys.OrderBy(x => x, StringComparer.Ordinal));
    }
}
