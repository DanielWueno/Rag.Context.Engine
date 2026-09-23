using RagEngine.Core.Infrastructure.VectorStore.Expansion;

namespace RagEngine.Experiment152;

/// <summary>
/// Calificador respaldado por un mapa chunk -> destinos ya resuelto en tiempo de
/// construccion. Es la forma honesta de medir los brazos sintactico y semantico: la
/// cualificacion es trabajo de ingesta, no de consulta, y su coste se cobra en
/// build_seconds en vez de contaminar la latencia de busqueda.
/// </summary>
public sealed class PrecomputedQualifier : ISymbolExpansionQualifier
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<QualifiedSymbolTarget>> _byChunk;

    public PrecomputedQualifier(string name, IReadOnlyDictionary<string, IReadOnlyList<QualifiedSymbolTarget>> byChunk)
    {
        Name = name;
        _byChunk = byChunk;
    }

    public string Name { get; }

    public SymbolExpansionPlan Qualify(
        IReadOnlyList<SymbolExpansionSeed> seeds, IReadOnlyCollection<string> harvestedSymbols)
    {
        var targets = new List<QualifiedSymbolTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var seed in seeds)
        {
            if (!_byChunk.TryGetValue(seed.ChunkId, out var list)) continue;
            foreach (var target in list)
                if (seen.Add(target.ClassName + " " + target.MethodName))
                    targets.Add(target);
        }

        return targets.Count == 0 ? SymbolExpansionPlan.Empty : new SymbolExpansionPlan { Targets = targets };
    }
}

/// <summary>
/// Calificador del brazo "graph": consulta el almacen persistido y devuelve ids de chunk
/// ya resueltos. A diferencia de los anteriores NO devuelve pares (clase, metodo): la
/// arista ya identifica el destino, asi que Qdrant no vuelve a emparejar nombres.
/// </summary>
public sealed class GraphQualifier : ISymbolExpansionQualifier
{
    private readonly GraphStore _store;
    private readonly string _collection;

    public GraphQualifier(GraphStore store, string collection)
    {
        _store = store;
        _collection = collection;
    }

    public string Name => "graph";

    public SymbolExpansionPlan Qualify(
        IReadOnlyList<SymbolExpansionSeed> seeds, IReadOnlyCollection<string> harvestedSymbols)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var seed in seeds)
        foreach (var target in _store.Neighbours(seed.ChunkId, _collection))
            if (seen.Add(target)) ids.Add(target);

        return ids.Count == 0 ? SymbolExpansionPlan.Empty : new SymbolExpansionPlan { ChunkIds = ids };
    }
}

/// <summary>
/// Indireccion para no levantar un contenedor de DI por brazo cualificado: el retriever
/// captura este objeto una vez y el driver cambia el brazo activo entre llamadas. El
/// orden de ejecucion del protocolo intercala brazos, asi que un proveedor por brazo
/// obligaria a mantener cinco sesiones ONNX vivas sin ganar nada.
/// </summary>
public sealed class SwitchingQualifier : ISymbolExpansionQualifier
{
    public ISymbolExpansionQualifier? Current { get; set; }

    public string Name => Current?.Name ?? "none";

    public SymbolExpansionPlan Qualify(
        IReadOnlyList<SymbolExpansionSeed> seeds, IReadOnlyCollection<string> harvestedSymbols) =>
        Current?.Qualify(seeds, harvestedSymbols)
        ?? throw new InvalidOperationException("Brazo cualificado sin calificador activo");
}
