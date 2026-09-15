namespace RagEngine.Core.Domain;

/// <summary>
/// Familia de prompt de sistema para la generación (ítem 7.a). <see cref="Auto"/>
/// conserva la heurística de contenido actual (mayoría de chunks de documentación vs.
/// código, ver <c>SystemPromptComposer.SelectTemplate</c>); <see cref="Code"/>/
/// <see cref="Docs"/> fuerzan la plantilla sin mirar los chunks recuperados.
/// </summary>
public enum PromptFamily
{
    Auto = 0,
    Code = 1,
    Docs = 2
}

/// <summary>
/// Overrides opcionales de los tres pesos de fusión ponderada y su constante RRF
/// (ver <see cref="Infrastructure.VectorStore.RetrievalFusionOptions"/>). Cada campo
/// null conserva el valor global configurado en la sección "RetrievalFusion" de
/// appsettings.json — un perfil solo necesita declarar lo que quiere cambiar.
/// </summary>
public sealed record RetrievalProfileFusionWeights
{
    public double? WeightCodigo { get; init; }
    public double? WeightSparse { get; init; }
    public double? WeightResumen { get; init; }
    public int? RrfK { get; init; }
}

/// <summary>
/// Perfil de recuperación por colección (ítem 7.a-perfil-por-coleccion): pesos de
/// fusión, min_score, rerank, two_hop, familia de prompt y top_k por defecto. Se
/// referencia por nombre desde <see cref="CollectionManifest.Profile"/> y se declara
/// en la sección "RetrievalProfiles" de appsettings.json
/// (<see cref="RetrievalProfileCatalogOptions"/>).
///
/// TODOS los campos son opcionales (null = "usa el global/baseline de hoy"): una
/// colección sin <see cref="CollectionManifest.Profile"/> declarado, o con un nombre
/// que no existe en el catálogo, se resuelve a null en
/// <see cref="Infrastructure.VectorStore.IRetrievalProfileResolver"/> y el
/// comportamiento es IDÉNTICO al que existía antes de este ítem — ese es el
/// contrato de compatibilidad hacia atrás que exige la ficha, no un detalle interno.
/// </summary>
public sealed record RetrievalProfile
{
    /// <summary>Top-K por defecto cuando el llamador no lo especifica explícitamente.</summary>
    public int? TopK { get; init; }

    /// <summary>Umbral mínimo de similitud por defecto (ver <c>RetrievalOptions.MinimumSimilarityScore</c>).</summary>
    public float? MinScore { get; init; }

    /// <summary>Si se aplica re-ranking por defecto cuando el llamador no lo especifica.</summary>
    public bool? UseReRanking { get; init; }

    /// <summary>Interruptor de two-hop (ítem 6.a) específico de esta colección.</summary>
    public bool? TwoHopEnabled { get; init; }

    /// <summary>Familia de prompt forzada para esta colección.</summary>
    public PromptFamily? PromptFamily { get; init; }

    /// <summary>Overrides de pesos de fusión ponderada, solo aplican a colecciones con vector "dense-resumen".</summary>
    public RetrievalProfileFusionWeights? Fusion { get; init; }
}

/// <summary>
/// Catálogo de perfiles nombrados, bound desde la sección "RetrievalProfiles" de
/// appsettings.json. Las claves son los nombres que <see cref="CollectionManifest.Profile"/>
/// referencia; la comparación es sensible a mayúsculas/minúsculas (el nombre debe
/// coincidir exactamente con la clave declarada en configuración).
/// </summary>
public sealed class RetrievalProfileCatalogOptions
{
    public const string SectionName = "RetrievalProfiles";

    public Dictionary<string, RetrievalProfile> Profiles { get; init; } = new();
}
