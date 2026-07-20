using Microsoft.Extensions.Configuration;
using RagEngine.Core.Infrastructure.Vectorization;

namespace RagEngine.Poc.FreeSearch;

/// <summary>
/// Configuración del PoC de búsqueda libre (RRF a 3 bandas).
/// Se carga de poc-settings.json. Las secciones OnnxBrain y Ollama son un espejo
/// de las del appsettings.json del CLI para reutilizar el mismo modelo/endpoint local.
/// </summary>
public sealed class PocSettings
{
    /// <summary>Carpeta raíz de la muestra a analizar (50-100 archivos de un módulo, cruzando stacks).</summary>
    public string SourceRoot { get; init; } = "";

    /// <summary>Ruta al set de evaluación etiquetado a mano (ver eval/eval-set.sample.json).</summary>
    public string EvalSetPath { get; init; } = "eval/eval-set.sample.json";

    /// <summary>Valores de k para los que se reporta recall@k.</summary>
    public int[] RecallAtK { get; init; } = [1, 3, 5, 10];

    /// <summary>
    /// Peticiones concurrentes a Ollama en la generación de resúmenes. Ollama procesa
    /// en paralelo hasta OLLAMA_NUM_PARALLEL (default 4). Subirlo de más no acelera y
    /// puede saturar memoria/GPU.
    /// </summary>
    public int GenerationConcurrency { get; init; } = 4;

    /// <summary>Constante RRF estándar (la misma que usa Qdrant internamente).</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>Pesos base de la fusión (Reto B). Calibrables; arrancan en los del documento.</summary>
    public double WeightCode { get; init; } = 1.0;
    public double WeightResumen { get; init; } = 1.3;

    // Espejos de appsettings.json del CLI ------------------------------------
    public OnnxBrainOptions OnnxBrain { get; init; } = new();
    public OllamaSection Ollama { get; init; } = new();

    public sealed class OllamaSection
    {
        public string Endpoint { get; init; } = "http://localhost:11434/v1";
        public string ModelId { get; init; } = "qwen2.5-coder";
        public int TimeoutSeconds { get; init; } = 120;
    }

    public static PocSettings Load(string path)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();

        var settings = config.Get<PocSettings>()
            ?? throw new InvalidOperationException($"No se pudo enlazar PocSettings desde '{path}'.");

        if (string.IsNullOrWhiteSpace(settings.SourceRoot))
            throw new InvalidOperationException("PocSettings.SourceRoot es obligatorio (carpeta de la muestra).");

        return settings;
    }
}
