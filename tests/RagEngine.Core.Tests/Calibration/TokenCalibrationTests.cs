using Xunit;

namespace RagEngine.Core.Tests.Calibration;

/// <summary>
/// Ítem 11.5: fija los ratios calibrados de caracteres-por-token medidos contra el
/// tokenizador real cl100k_base, con partición calibración/validación 70/30, para
/// detectar drift silencioso si el censo del repositorio cambia de forma que altere
/// significativamente la densidad de tokens por lenguaje.
///
/// Valores medidos el 2026-09-14 sobre commit 8c1e978 (ver
/// <c>docs/eval/quality/11.5/calibracion-tokens.md</c> para el detalle completo,
/// n por grupo/split, sesgo y error p95). Las tolerancias son deliberadamente
/// anchas (no un umbral estrecho inventado): existen para detectar un cambio
/// GRANDE de composición del censo (p. ej. borrar toda la documentación), no para
/// pinnear un dígito decimal.
/// </summary>
public class TokenCalibrationTests
{
    private static string RepoRoot => RepoRootLocator.Find();

    private static (TokenCalibration.GroupStats Calib, TokenCalibration.GroupStats Valid) Partition(
        string group, List<TokenCalibration.Fragment> fragments)
    {
        var calib = fragments.Where(f => !f.IsValidation).ToList();
        var valid = fragments.Where(f => f.IsValidation).ToList();
        return (TokenCalibration.Summarize(group, "calibracion", calib),
                TokenCalibration.Summarize(group, "validacion", valid));
    }

    [Fact]
    public void CSharp_RatioMedidoDentroDelRangoObservado()
    {
        var (calib, valid) = Partition("csharp", TokenCalibration.CollectCSharp(RepoRoot));

        Assert.True(calib.N >= 100, $"n de calibración C# demasiado pequeño: {calib.N}");
        Assert.True(valid.N >= 30, $"n de validación C# demasiado pequeño: {valid.N}");

        // Medido: calib≈4.31, valid≈4.36 chars/token (más holgado que el supuesto
        // global de 4.0: la heurística SOBREESTIMA tokens en C#, dirección segura).
        AssertRatioEnRango(calib.AggregateCharsPerToken, 3.8, 4.8);
        AssertRatioEnRango(valid.AggregateCharsPerToken, 3.8, 4.8);
        Assert.True(valid.P95RelativeError < 0.60, $"p95 error relativo C# fuera de rango: {valid.P95RelativeError}");
    }

    [Fact]
    public void TypeScript_RatioMedidoDentroDelRangoObservado_MuestraPequenaDocumentada()
    {
        var fragments = TokenCalibration.CollectTypeScript(RepoRoot);

        // No hay corpus TypeScript en este repositorio (0 archivos .ts): el chunker
        // de TS sirve a repos externos ingeridos. n=3 es CENSO COMPLETO de los
        // fixtures versionados disponibles, no una muestra de >=100 — limitación
        // documentada en calibracion-tokens.md, no oculta aquí.
        Assert.True(fragments.Count is > 0 and <= 10, "El censo de fixtures TS cambió: revisar el fixture.");

        var stats = TokenCalibration.Summarize("typescript", "censo-completo", fragments);

        // Medido: ~3.2-3.3 chars/token (MÁS DENSO que el supuesto de 4.0: la
        // heurística SUBESTIMA tokens en TS — dirección insegura para presupuestos
        // que asuman 4.0 sin margen; ver GenerationContextBudgetTests, que usa un
        // ratio conservador de 3.0 precisamente por este hallazgo).
        AssertRatioEnRango(stats.AggregateCharsPerToken, 2.5, 4.0);
    }

    [Fact]
    public void Prosa_RatioMedidoDentroDelRangoObservado()
    {
        var (calib, valid) = Partition("prose", TokenCalibration.CollectProse(RepoRoot));

        Assert.True(calib.N >= 100, $"n de calibración prosa demasiado pequeño: {calib.N}");
        Assert.True(valid.N >= 30, $"n de validación prosa demasiado pequeño: {valid.N}");

        // Medido: calib≈3.49, valid≈3.47 chars/token — también más denso que 4.0,
        // misma dirección insegura que TypeScript si se usara el supuesto global
        // sin margen.
        AssertRatioEnRango(calib.AggregateCharsPerToken, 3.0, 4.0);
        AssertRatioEnRango(valid.AggregateCharsPerToken, 3.0, 4.0);
        Assert.True(valid.P95RelativeError < 0.60, $"p95 error relativo prosa fuera de rango: {valid.P95RelativeError}");
    }

    private static void AssertRatioEnRango(double ratio, double min, double max) =>
        Assert.True(ratio >= min && ratio <= max, $"Ratio {ratio} fuera de [{min}, {max}]");
}
