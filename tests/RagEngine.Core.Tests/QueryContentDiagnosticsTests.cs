using RagEngine.Core.Diagnostics;
using RagEngine.Core.Domain;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Ítem 12.9: fija el borde exacto de expiración del diagnóstico acotado de contenido
/// de consulta, con reloj sintético (nunca <see cref="DateTimeOffset.UtcNow"/>) para
/// que el test sea determinista.
/// </summary>
public class QueryContentDiagnosticsTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Apagado_por_defecto_nunca_esta_activo_aunque_haya_una_caducidad_futura()
    {
        var opciones = new LoggingOptions
        {
            EnableQueryContentDiagnostics = false,
            QueryContentDiagnosticsExpiresAt = Ahora.AddDays(1)
        };

        Assert.False(QueryContentDiagnostics.IsActive(opciones, Ahora));
        Assert.False(QueryContentDiagnostics.IsExpiredOrMisconfigured(opciones, Ahora));
    }

    [Fact]
    public void Activado_sin_fecha_de_caducidad_nunca_esta_activo_y_se_marca_mal_configurado()
    {
        var opciones = new LoggingOptions
        {
            EnableQueryContentDiagnostics = true,
            QueryContentDiagnosticsExpiresAt = null
        };

        Assert.False(QueryContentDiagnostics.IsActive(opciones, Ahora));
        Assert.True(QueryContentDiagnostics.IsExpiredOrMisconfigured(opciones, Ahora));
    }

    [Fact]
    public void Activado_con_caducidad_futura_esta_activo()
    {
        var opciones = new LoggingOptions
        {
            EnableQueryContentDiagnostics = true,
            QueryContentDiagnosticsExpiresAt = Ahora.AddMinutes(1)
        };

        Assert.True(QueryContentDiagnostics.IsActive(opciones, Ahora));
        Assert.False(QueryContentDiagnostics.IsExpiredOrMisconfigured(opciones, Ahora));
    }

    [Fact]
    public void Al_instante_exacto_de_caducidad_ya_esta_inactivo()
    {
        var opciones = new LoggingOptions
        {
            EnableQueryContentDiagnostics = true,
            QueryContentDiagnosticsExpiresAt = Ahora
        };

        Assert.False(QueryContentDiagnostics.IsActive(opciones, Ahora));
        Assert.True(QueryContentDiagnostics.IsExpiredOrMisconfigured(opciones, Ahora));
    }

    [Fact]
    public void Caducidad_ya_pasada_queda_inactiva_y_marcada_como_mal_configurada()
    {
        var opciones = new LoggingOptions
        {
            EnableQueryContentDiagnostics = true,
            QueryContentDiagnosticsExpiresAt = Ahora.AddDays(-1)
        };

        Assert.False(QueryContentDiagnostics.IsActive(opciones, Ahora));
        Assert.True(QueryContentDiagnostics.IsExpiredOrMisconfigured(opciones, Ahora));
    }
}
