using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.Tokenizers;

namespace RagEngine.Core.Tests.Calibration;

/// <summary>
/// Ítem 11.5 del plan: calibra y acota el error de la heurística de caracteres por
/// token (<see cref="RagEngine.Core.Utilities.TokenEstimator.CharsPerToken"/>, fija en
/// 4.0 para todos los lenguajes) contra un tokenizador BPE real, por grupo de
/// lenguaje, sobre una muestra versionada en git.
///
/// Alcance deliberado: esta calibración NO cambia
/// <see cref="RagEngine.Core.Utilities.TokenEstimator.CharsPerToken"/> ni ninguna
/// constante usada por los chunkers (<c>ChunkBuilder</c>,
/// <c>RoslynCSharpChunkingStrategy</c>, <c>TypeScriptChunkingStrategy</c>,
/// <c>ParagraphBudget</c>): esa constante determina la geometría de la ventana
/// deslizante y, por tanto, las fronteras de los chunks ya indexados. Cambiarla
/// exigiría subir <c>ChunkingContract.Version</c> y reingestar (~19 h documentadas
/// en el ítem 1.5/TokenEstimator), muy por encima de los 0,2 h anunciados para este
/// ítem y sin la confirmación de coste que exige el protocolo. Ese cambio, si algún
/// día se justifica con evidencia, es del resorte de 11.3 (bloqueado, sin perseguir
/// tras el resultado nulo de 11.2). Aquí sólo se calibra y se publica el error de la
/// heurística compartida, y se usa una estimación DISTINTA y conservadora en el
/// único consumidor que la necesita sin tocar el índice: el presupuesto de contexto
/// de generación (ver <see cref="GenerationContextBudgetTests"/>).
///
/// Tokenizador de referencia: cl100k_base (familia GPT-3.5/4, vía
/// <c>Microsoft.ML.Tokenizers.Data.Cl100kBase</c>), el único tokenizador BPE de uso
/// general empaquetado como dependencia .NET offline disponible en este entorno. No
/// es el tokenizador del LLM de generación local (Qwen2.5-Coder vía Ollama, que no
/// expone un tokenizador .NET), por eso el error medido aquí es un proxy, no una
/// medición exacta contra ese modelo — se documenta así en
/// <c>docs/eval/quality/11.5/calibracion-tokens.md</c>.
/// </summary>
internal static class TokenCalibration
{
    private const int WindowLines = 40;
    private const int MinFragmentChars = 200;

    public static readonly Tokenizer RealTokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

    public sealed record Fragment(string Group, string SourcePath, int WindowIndex, string Text)
    {
        public int CharLength => Text.Length;
        public int RealTokens => RealTokenizer.CountTokens(Text);

        /// <summary>Misma fórmula que <see cref="RagEngine.Core.Utilities.TokenEstimator.Estimate"/>.</summary>
        public int EstimatedTokens => (int)Math.Ceiling(CharLength / RagEngine.Core.Utilities.TokenEstimator.CharsPerToken);

        /// <summary>
        /// Partición determinista calibración/validación: 70/30 por el primer byte
        /// del SHA-256 del fragmento (estable entre corridas, no depende del orden
        /// de enumeración del sistema de archivos).
        /// </summary>
        public bool IsValidation
        {
            get
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Text));
                return hash[0] % 10 >= 7;
            }
        }
    }

    public sealed record GroupStats(
        string Group, string Split, int N,
        double AggregateCharsPerToken, double MeanBiasTokens, double P95RelativeError);

    /// <summary>
    /// Fragmenta un texto en ventanas no solapadas de <see cref="WindowLines"/>
    /// líneas, descartando restos por debajo de <see cref="MinFragmentChars"/>
    /// caracteres (encabezados/cierres de archivo demasiado cortos para ser
    /// representativos de un chunk real).
    /// </summary>
    private static IEnumerable<Fragment> Windows(string group, string path, string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var index = 0;
        for (var start = 0; start < lines.Length; start += WindowLines)
        {
            var slice = lines.Skip(start).Take(WindowLines);
            var text = string.Join('\n', slice);
            if (text.Length >= MinFragmentChars)
                yield return new Fragment(group, path, index++, text);
        }
    }

    public static List<Fragment> CollectCSharp(string repoRoot)
    {
        var roots = new[] { "src", "tests" };
        var files = roots
            .Select(r => Path.Combine(repoRoot, r))
            .Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        return files
            .SelectMany(f => Windows("csharp", Path.GetRelativePath(repoRoot, f), File.ReadAllText(f)))
            .ToList();
    }

    /// <summary>
    /// No hay corpus TypeScript en este repositorio (0 archivos .ts): el chunker de
    /// TS existe para repos externos ingeridos, no para RagEngine mismo. La única
    /// muestra versionada disponible son los fixtures de
    /// <c>TypeScriptChunkingStrategyTests</c>; se usan las dos variantes con salto de
    /// línea LF (se excluyen los duplicados CRLF para no inflar n con el mismo
    /// contenido). Censo completo, no muestra: n queda documentado como pequeño.
    /// </summary>
    public static List<Fragment> CollectTypeScript(string repoRoot)
    {
        var fixturesDir = Path.Combine(repoRoot, "tests", "RagEngine.Core.Tests", "Fixtures");
        var files = new[] { "typescript-sample.fixture", "typescript-large.fixture" }
            .Select(f => Path.Combine(fixturesDir, f))
            .Where(File.Exists);

        return files
            .SelectMany(f => Windows("typescript", Path.GetRelativePath(repoRoot, f), File.ReadAllText(f)))
            .ToList();
    }

    public static List<Fragment> CollectProse(string repoRoot)
    {
        var docsDir = Path.Combine(repoRoot, "docs");
        var files = Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);

        return files
            .SelectMany(f => Windows("prose", Path.GetRelativePath(repoRoot, f), File.ReadAllText(f)))
            .ToList();
    }

    public static GroupStats Summarize(string group, string split, IReadOnlyList<Fragment> fragments)
    {
        if (fragments.Count == 0)
            return new GroupStats(group, split, 0, double.NaN, double.NaN, double.NaN);

        long totalChars = fragments.Sum(f => (long)f.CharLength);
        long totalRealTokens = fragments.Sum(f => (long)f.RealTokens);
        double aggregateRatio = totalRealTokens == 0 ? double.NaN : (double)totalChars / totalRealTokens;

        double meanBias = fragments.Average(f => f.EstimatedTokens - f.RealTokens);

        var relErrors = fragments
            .Where(f => f.RealTokens > 0)
            .Select(f => Math.Abs(f.EstimatedTokens - f.RealTokens) / (double)f.RealTokens)
            .OrderBy(e => e)
            .ToList();
        double p95 = relErrors.Count == 0 ? double.NaN : Percentile(relErrors, 0.95);

        return new GroupStats(group, split, fragments.Count, aggregateRatio, meanBias, p95);
    }

    private static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 1) return sortedAscending[0];
        double rank = p * (sortedAscending.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sortedAscending[lo];
        double frac = rank - lo;
        return sortedAscending[lo] + (sortedAscending[hi] - sortedAscending[lo]) * frac;
    }
}
