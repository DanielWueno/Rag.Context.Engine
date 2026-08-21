using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using RagEngine.Core.Services.Generation.Prompts;
using RagEngine.Core.Services.Summary;
using Xunit;

namespace RagEngine.Core.Tests;

/// <summary>
/// Fija el hash de cada prompt del sistema.
///
/// Dos razones, y la segunda tiene precio en horas:
///
/// 1. Prueba que la Fase 1 de docs/analisis-futuro/centralizacion-prompts-vault.md
///    fue un refactor byte-idéntico: estos hashes se midieron ANTES de mover los
///    prompts de RagGenerationService a Prompts/, y siguen valiendo después.
///
/// 2. El prompt de resumen entra en <c>prompt_version</c>
///    (<see cref="OllamaBusinessSummaryGenerator.ComputePromptVersion"/>), que es
///    parte de la clave de la caché de resúmenes. Cambiarlo —aunque sea un espacio—
///    invalida las 18.784 entradas y obliga a regenerarlas vía Ollama: ~19 h para
///    bsuite-repo. Un test que falle en 80 ms es preferible a descubrirlo a mitad de
///    una re-ingesta.
///
/// Si un cambio de prompt es INTENCIONAL, actualiza el hash aquí y di en el commit
/// qué cambió y por qué. Si es el prompt de resumen, di además que asumes la
/// regeneración.
/// </summary>
public class PromptHashesTests
{
    private static string Hash(string valor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valor)))[..16].ToLowerInvariant();

    private static string Leer(Type tipo, string nombre) =>
        (string)tipo.GetField(nombre, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
                    .GetRawConstantValue()!;

    public static TheoryData<string, string, string> Prompts => new()
    {
        // nombre legible                         clase                                 hash medido antes de mover
        { nameof(CodeSystemPrompt),        "Template",          "d32cd4727304ccc0" },
        { nameof(DocsSystemPrompt),        "Template",          "a3ba96b72efbd120" },
        { nameof(SimpleSystemPrompt),      "Template",          "0d29eb17a902a5d2" },
        { nameof(NoGroundingSystemPrompt), "Template",          "cd3d1a5538b5ef00" },
        { nameof(LowConfidencePrompt),     "Addendum",          "a8e099b20a849b76" },
        { nameof(SelfDescription),         "Block",             "4b8f2c0dcffbb014" },
        { nameof(AnswerNotices),           "NoContextFallback", "7aa0e1c9191b849f" },
    };

    [Theory]
    [MemberData(nameof(Prompts))]
    public void ElPromptNoCambio(string clase, string miembro, string hashEsperado)
    {
        var tipo = typeof(CodeSystemPrompt).Assembly
            .GetType($"RagEngine.Core.Services.Generation.Prompts.{clase}")!;

        var real = Hash(Leer(tipo, miembro));

        Assert.True(hashEsperado == real,
            $"{clase}.{miembro} cambio: esperado {hashEsperado}, ahora {real}. "
          + "Si el cambio es intencional, actualiza el hash y explicalo en el commit.");
    }

    [Fact]
    public void ElPromptDeResumenNoCambio_PorqueInvalidaLaCache()
    {
        var real = Hash(Leer(typeof(OllamaBusinessSummaryGenerator), "SystemPrompt"));

        Assert.True("ad080baeb19b2360" == real,
            $"El prompt de resumen cambio (ahora {real}). Eso mueve prompt_version y deja las 18.784 "
          + "entradas de la cache en fallo: la proxima ingesta con --con-resumen regenerara todo via "
          + "Ollama (~19 h para bsuite-repo). Si es intencional, actualiza el hash y dilo en el commit.");
    }
}
