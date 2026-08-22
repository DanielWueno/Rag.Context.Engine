using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RagEngine.Core.Services.Summary;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// La caché de resúmenes es la pieza que hace tolerable re-ingestar: sin ella hay
/// que regenerar cada resumen vía Ollama, el paso más lento de la ingesta (~19 h
/// para bsuite-repo). Estos tests fijan las dos propiedades de las que depende
/// esa economía: que un hit devuelva lo guardado, y que un cambio de
/// prompt_version lo invalide.
///
/// Son hermeticos a propósito — cada uno usa su propio archivo temporal, sin
/// tocar la caché real ni depender de que exista.
/// </summary>
public class SummaryCacheTests : IDisposable
{
    private readonly string _rutaTemporal =
        Path.Combine(Path.GetTempPath(), $"ragengine-cache-test-{Guid.NewGuid():N}.sqlite3");

    public void Dispose()
    {
        foreach (var sufijo in new[] { "", "-wal", "-shm" })
        {
            var archivo = _rutaTemporal + sufijo;
            if (File.Exists(archivo)) File.Delete(archivo);
        }
    }

    /// <summary>
    /// Propiedad de la que depende operar con la API consultando y una ingesta escribiendo:
    /// un escritor que encuentra el lock tomado ESPERA, no falla. Se verificó de forma
    /// aislada que quien la garantiza es el reintento ante SQLITE_BUSY de
    /// Microsoft.Data.Sqlite (ocurre con y sin `PRAGMA busy_timeout`, y también con
    /// CommandTimeout=0, que en este proveedor significa "sin límite"). El pragma que
    /// SummaryCache aplica en cada conexión es defensa adicional, no lo que sostiene esto.
    ///
    /// Este test NO discrimina la presencia del pragma — fija la garantía observable, que
    /// es lo que importa si algún día se cambia de proveedor o de política de reintentos.
    /// </summary>
    [Fact]
    public async Task Escribir_EsperaAlLockDeOtroEscritorEnVezDeFallar()
    {
        var cache = SummaryCache.Open(_rutaTemporal, "v1");
        var cadena = new SqliteConnectionStringBuilder { DataSource = _rutaTemporal }.ToString();

        await using var bloqueador = new SqliteConnection(cadena);
        await bloqueador.OpenAsync();
        await using (var begin = bloqueador.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";   // toma el lock de escritura
            await begin.ExecuteNonQueryAsync();
        }

        var soltar = Task.Run(async () =>
        {
            await Task.Delay(400);
            await using var commit = bloqueador.CreateCommand();
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync();
        });

        var reloj = Stopwatch.StartNew();
        await cache.SetAsync("hash-concurrente", "Escrito pese a la contención.");
        reloj.Stop();
        await soltar;

        Assert.True(reloj.ElapsedMilliseconds >= 300,
            $"debió esperar al lock, tardó {reloj.ElapsedMilliseconds} ms");

        var (encontrado, resumen) = await cache.TryGetAsync("hash-concurrente");
        Assert.True(encontrado);
        Assert.Equal("Escrito pese a la contención.", resumen);
    }

    [Fact]
    public async Task LoGuardado_SeRecuperaConElMismoHashYPromptVersion()
    {
        var cache = SummaryCache.Open(_rutaTemporal, "v1");
        await cache.SetAsync("hash-abc", "Cancela un ticket si el usuario lo reportó.");

        var (encontrado, resumen) = await cache.TryGetAsync("hash-abc");

        Assert.True(encontrado);
        Assert.Equal("Cancela un ticket si el usuario lo reportó.", resumen);
    }

    [Fact]
    public async Task CambiarPromptVersion_InvalidaLaEntrada()
    {
        // Esta es la propiedad con consecuencias economicas: externalizar o retocar
        // un prompt cambia su version y deja la cache entera en fallo, obligando a
        // regenerar todos los resumenes via Ollama. El item 2.3 del plan depende de
        // hacerlo como refactor byte-identico precisamente por esto.
        var conV1 = SummaryCache.Open(_rutaTemporal, "v1");
        await conV1.SetAsync("hash-abc", "resumen generado con el prompt v1");

        var conV2 = SummaryCache.Open(_rutaTemporal, "v2");
        var (encontrado, _) = await conV2.TryGetAsync("hash-abc");

        Assert.False(encontrado);
    }

    [Fact]
    public async Task HashDesconocido_EsFallo()
    {
        var cache = SummaryCache.Open(_rutaTemporal, "v1");

        var (encontrado, resumen) = await cache.TryGetAsync("hash-que-nunca-se-guardo");

        Assert.False(encontrado);
        Assert.Null(resumen);
    }

    [Fact]
    public async Task ChunkSinValorDeNegocio_SeRecuerdaComoTal()
    {
        // Guardar null es el centinela de "ya se evaluo y no tiene contenido de
        // negocio". Tiene que distinguirse de "nunca se evaluo", o la ingesta
        // volveria a preguntarle a Ollama por cada chunk sin valor en cada corrida.
        var cache = SummaryCache.Open(_rutaTemporal, "v1");
        await cache.SetAsync("hash-sin-negocio", null);

        var (encontrado, resumen) = await cache.TryGetAsync("hash-sin-negocio");

        Assert.True(encontrado);
        Assert.Null(resumen);
    }

    [Fact]
    public async Task ReabrirElArchivo_ConservaLoGuardado()
    {
        var primera = SummaryCache.Open(_rutaTemporal, "v1");
        await primera.SetAsync("hash-persistente", "sobrevive al cierre");

        var segunda = SummaryCache.Open(_rutaTemporal, "v1");
        var (encontrado, resumen) = await segunda.TryGetAsync("hash-persistente");

        Assert.True(encontrado);
        Assert.Equal("sobrevive al cierre", resumen);
    }
}
