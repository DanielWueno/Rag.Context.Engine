using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RagEngine.Cli.Infrastructure;

/// <summary>
/// Procedencia de una corrida de eval: todo lo que hace falta saber para poder
/// decir si dos números de recall son comparables entre sí.
///
/// El problema que resuelve: el único baseline que existía en docs/eval/baselines
/// guardaba colección, top_k, rerank y min_score, pero no el commit, ni el modelo
/// de embeddings, ni los pesos RRF, ni la versión del chunker. Los pesos se
/// recalibraron después de generarlo, así que ese archivo dejó de ser comparable
/// con cualquier corrida nueva y no había forma de saberlo mirándolo.
/// </summary>
public sealed record EvalProvenance
{
    public required string GitCommit { get; init; }
    public required bool GitDirty { get; init; }
    public required string EvalSetPath { get; init; }

    /// <summary>
    /// Hash del contenido del eval-set. Detecta el re-etiquetado: si alguien
    /// corrige el ground-truth de una pregunta, el recall cambia sin que cambie
    /// nada del motor, y comparar contra el baseline anterior sería engañoso.
    /// </summary>
    public required string EvalSetHash { get; init; }

    public required string EmbeddingModel { get; init; }
    public required int EmbeddingDimensions { get; init; }
    public required int EmbeddingMaxSequenceLength { get; init; }
    public required string? CrossEncoderModel { get; init; }
    public required double WeightCodigo { get; init; }
    public required double WeightSparse { get; init; }
    public required double WeightResumen { get; init; }
    public required int RrfK { get; init; }
    public required int ChunkingContractVersion { get; init; }

    /// <summary>
    /// Versión del prompt de resumen de negocio. Entra aquí porque el tercer vector
    /// de la fusión (dense-resumen) se genera con ese prompt: cambiarlo cambia el
    /// contenido indexado, no solo la redacción.
    /// </summary>
    public required string ResumenPromptVersion { get; init; }

    /// <summary>
    /// Campos cuya diferencia hace que dos baselines NO sean comparables. El
    /// timestamp queda fuera a propósito, y también la ruta del eval-set: lo que
    /// importa de él es su contenido, no dónde está.
    /// </summary>
    public IReadOnlyDictionary<string, string> ComparabilityKeys() =>
        new Dictionary<string, string>
        {
            ["commit"] = GitCommit + (GitDirty ? " (arbol sucio)" : string.Empty),
            ["eval_set_hash"] = EvalSetHash,
            ["embedding_model"] = EmbeddingModel,
            ["embedding_dimensions"] = EmbeddingDimensions.ToString(),
            ["embedding_max_sequence_length"] = EmbeddingMaxSequenceLength.ToString(),
            ["cross_encoder_model"] = CrossEncoderModel ?? "(sin rerank)",
            ["weight_codigo"] = WeightCodigo.ToString("0.###"),
            ["weight_sparse"] = WeightSparse.ToString("0.###"),
            ["weight_resumen"] = WeightResumen.ToString("0.###"),
            ["rrf_k"] = RrfK.ToString(),
            ["chunking_contract_version"] = ChunkingContractVersion.ToString(),
            ["resumen_prompt_version"] = ResumenPromptVersion,
        };

    public static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..12].ToLowerInvariant();

    /// <summary>
    /// Commit actual y si el árbol tiene cambios sin commitear. Un baseline
    /// generado con el árbol sucio no es reproducible y hay que decirlo, no
    /// disimularlo. Si git no está disponible se devuelve "desconocido" en vez de
    /// fallar: el eval sigue siendo útil, solo menos rastreable.
    /// </summary>
    public static (string Commit, bool Dirty) ReadGitState()
    {
        try
        {
            var commit = RunGit("rev-parse --short HEAD");
            var status = RunGit("status --porcelain");
            return (string.IsNullOrWhiteSpace(commit) ? "desconocido" : commit.Trim(),
                    !string.IsNullOrWhiteSpace(status));
        }
        catch
        {
            return ("desconocido", false);
        }
    }

    private static string RunGit(string args)
    {
        using var proc = Process.Start(new ProcessStartInfo("git", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("no se pudo lanzar git");

        var salida = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5_000);
        return salida;
    }
}
