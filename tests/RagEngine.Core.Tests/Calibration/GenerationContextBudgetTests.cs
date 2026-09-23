using RagEngine.Core.Domain;
using RagEngine.Core.Services.Generation;
using Xunit;

namespace RagEngine.Core.Tests.Calibration;

/// <summary>
/// Ítem 11.5: demuestra que el presupuesto de contexto de generación
/// (<see cref="GenerationContextAssembler"/>, 12 000 caracteres) nunca excede, en
/// tokens REALES (cl100k_base), una estimación conservadora DISTINTA de la
/// heurística compartida de chunking (4 chars/token). No se cambia
/// <c>MaxContextCharacters</c> ni el golden byte-a-byte de
/// <see cref="GenerationContextGoldenTests"/>: sólo se añade la comprobación de
/// presupuesto que pedía el criterio de aceptación de 11.5.
///
/// Ratio conservador usado aquí: el peor caso medido en
/// <c>docs/eval/quality/11.5/calibracion-tokens.md</c> es prosa/TypeScript en
/// ~3.2–3.5 caracteres/token (más denso en tokens que el supuesto de 4.0), no el
/// de C# (~4.3, más holgado). <see cref="TokenBudgetTokens"/> se calcula con el
/// peor ratio observado para no subestimar el consumo real de tokens.
/// </summary>
public class GenerationContextBudgetTests
{
    /// <summary>
    /// MaxContextCharacters (12 000) / peor ratio medido (~3.2 chars/token, ver
    /// calibración de TypeScript/prosa) con margen de seguridad redondeando hacia
    /// abajo el ratio a 3.0 para no depender de la incertidumbre de una muestra de
    /// TypeScript con n=3. 12 000 / 3.0 = 4 000 tokens.
    /// </summary>
    private const int TokenBudgetTokens = 4_000;

    public static TheoryData<string> CasosDeContexto()
    {
        var datos = new TheoryData<string>();
        foreach (var (nombre, _) in GenerationGoldenCasos.Contexto)
            datos.Add(nombre);
        return datos;
    }

    [Theory]
    [MemberData(nameof(CasosDeContexto))]
    public void BloqueDeContexto_NuncaExcedeElPresupuestoConservadorEnTokensReales(string nombre)
    {
        var chunks = GenerationGoldenCasos.Contexto.Single(c => c.Nombre == nombre).Chunks;
        var bloque = GenerationContextAssembler.BuildContextBlock(chunks);

        var tokensReales = TokenCalibration.RealTokenizer.CountTokens(bloque);

        Assert.True(tokensReales <= TokenBudgetTokens,
            $"Caso '{nombre}': {tokensReales} tokens reales (cl100k) exceden el presupuesto conservador de {TokenBudgetTokens}.");
    }

    /// <summary>
    /// Caso adversarial: muchos chunks pequeños de PROSA REAL (el grupo con menor
    /// chars/token medido), suficientes para llenar el presupuesto de caracteres,
    /// simulando el peor caso de densidad de tokens que puede llegar a producir
    /// <see cref="GenerationContextAssembler"/> en un despliegue real con
    /// documentación en vez de código.
    /// </summary>
    [Fact]
    public void BloqueDeContexto_PeorCasoDeProsaReal_NuncaExcedeElPresupuesto()
    {
        var repoRoot = RepoRootLocator.Find();
        var fragments = TokenCalibration.CollectProse(repoRoot);
        Assert.True(fragments.Count > 20, "Se esperaba un censo de prosa suficiente para el caso adversarial.");

        var chunks = fragments
            .OrderByDescending(f => f.CharLength) // los más densos primero: peor caso
            .Take(60)
            .Select((f, i) => new RetrievalResult(
                ChunkId: $"prosa-{i}",
                Content: f.Text,
                SimilarityScore: 0.9f,
                ScoreScale: RetrievalScoreScale.CrossEncoderStable,
                Metadata: new CodeChunkMetadata(
                    FilePath: "/abs/" + f.SourcePath,
                    RelativeFilePath: f.SourcePath,
                    Language: SourceLanguage.Markdown,
                    Namespace: null,
                    ClassName: null,
                    MethodName: "Sección " + i,
                    StartLine: 1,
                    EndLine: 40,
                    LastModified: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    RepositoryName: "calibracion-11.5"),
                ContentHash: "hash-" + i))
            .ToList();

        var bloque = GenerationContextAssembler.BuildContextBlock(chunks);
        var tokensReales = TokenCalibration.RealTokenizer.CountTokens(bloque);

        Assert.True(bloque.Length <= 12_000, "El bloque debería respetar MaxContextCharacters.");
        Assert.True(tokensReales <= TokenBudgetTokens,
            $"Peor caso de prosa real: {tokensReales} tokens reales (cl100k) exceden el presupuesto conservador de {TokenBudgetTokens}.");
    }
}
