namespace RagEngine.Core.Infrastructure.VectorStore.Expansion;

/// <summary>
/// Semilla del segundo salto: el chunk del que se cosecharon símbolos consumidos.
/// Lleva sólo datos de payload ya indexados — un calificador no vuelve a Qdrant.
/// </summary>
public sealed record SymbolExpansionSeed(
    string ChunkId,
    string? RelativePath,
    string? ClassName,
    string? MethodName,
    string? Content,
    IReadOnlyList<string> ConsumedSymbols);

/// <summary>
/// Destino cualificado: nombre de método MÁS el tipo que lo declara. Es la diferencia
/// con el join por nombre de 6.d, que sólo dispone del método y por eso colisiona entre
/// homónimos de clases distintas (24/30 colisiones en la auditoría histórica).
/// </summary>
public sealed record QualifiedSymbolTarget(string ClassName, string MethodName);

/// <summary>
/// Restricción que un calificador impone al segundo salto. Dos formas excluyentes en la
/// práctica: pares (clase, método) — cualificación sintáctica o semántica — o ids de chunk
/// ya resueltos — aristas de un grafo persistido.
/// </summary>
public sealed record SymbolExpansionPlan
{
    public static readonly SymbolExpansionPlan Empty = new();

    public IReadOnlyList<QualifiedSymbolTarget> Targets { get; init; } = Array.Empty<QualifiedSymbolTarget>();

    public IReadOnlyList<string> ChunkIds { get; init; } = Array.Empty<string>();

    public bool IsEmpty => Targets.Count == 0 && ChunkIds.Count == 0;
}

/// <summary>
/// Punto de extensión del ítem 15.2.2: acota el segundo salto por símbolo a destinos
/// cualificados en lugar del join por nombre puro de <c>ExpandBySymbolAsync</c>.
///
/// SIN implementación registrada, <c>QdrantSemanticRetriever</c> se comporta EXACTAMENTE
/// como antes de 15.2.2 — se inyecta como <c>IEnumerable</c> precisamente para que la
/// ausencia de registro sea el caso por defecto y no haga falta un flag adicional. Es la
/// costura que permitió medir las tres alternativas locales contra el mismo control sin
/// duplicar el camino de recuperación de producción; no promueve ninguna por sí sola.
/// </summary>
public interface ISymbolExpansionQualifier
{
    /// <summary>Identificador del brazo, para trazas y evidencia del experimento.</summary>
    string Name { get; }

    /// <summary>
    /// Devuelve la restricción del salto. <see cref="SymbolExpansionPlan.Empty"/> significa
    /// "no sé a dónde saltar": el llamador debe abstenerse, NO caer al join por nombre —
    /// degradar silenciosamente convertiría una abstención en una colisión medida como
    /// éxito del calificador.
    /// </summary>
    SymbolExpansionPlan Qualify(
        IReadOnlyList<SymbolExpansionSeed> seeds,
        IReadOnlyCollection<string> harvestedSymbols);
}
