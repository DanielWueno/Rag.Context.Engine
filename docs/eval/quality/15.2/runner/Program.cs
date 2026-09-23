using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Extensions;
using RagEngine.Core.Infrastructure.VectorStore.Expansion;

namespace RagEngine.Experiment152;

/// <summary>
/// Driver del experimento 15.2.2. Ejecuta los cinco brazos del protocolo congelado sobre
/// el indice servido en solo lectura y emite la evidencia que consume accept.py
/// --experiment. No decide promociones: ese juicio vive en el comparador sellado.
/// </summary>
public static class Program
{
    private static readonly string[] Arms = { "no-expansion", "name-join", "syntax", "semantic", "graph" };

    public static async Task<int> Main(string[] rawArgs)
    {
        var args = Parse(rawArgs);
        var bundle = args["bundle"];
        var corpus = args["corpus"];
        var scratch = args["scratch"];
        var output = args["output"];
        var collection = args.GetValueOrDefault("collection", "bsuite-repo");
        Directory.CreateDirectory(scratch);

        var totalWatch = Stopwatch.StartNew();

        // ── Artefactos congelados ────────────────────────────────────────────────────
        var protocol = Load(Path.Combine(bundle, "protocol.json"))!.AsObject();
        var preparation = Load(Path.Combine(bundle, "preparation.json"))!.AsObject();
        var cohort = Load(Path.Combine(bundle, "cohort.json"))!.AsArray();
        var opportunities = Load(Path.Combine(bundle, "opportunities.json"))!.AsArray();
        var fixtures = Load(Path.Combine(bundle, "graph-fixtures.json"))!.AsObject();
        var freezeSha = FileSha(Path.Combine(bundle, "freeze.json"));

        Console.Error.WriteLine($"[15.2.2] freeze={freezeSha[..12]} cohorte={cohort.Count} oportunidades={opportunities.Count}");

        // Modos parciales para depurar sin gastar la corrida completa. Ninguno produce
        // evidencia de aceptacion: "full" es el unico que emite kind=measured_experiment.
        var mode = args.GetValueOrDefault("mode", "full");
        if (mode == "fixtures")
        {
            var only = GraphFixtures.Execute(fixtures, Path.Combine(scratch, "fixtures"));
            await File.WriteAllTextAsync(output, only.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine($"[15.2.2] fixtures escritos en {output}");
            return 0;
        }

        // ── Huella inicial y volcado de payloads ─────────────────────────────────────
        var payloadDump = Path.Combine(scratch, "payloads.json.gz");
        var before = Fingerprint(bundle, corpus, scratch, payloadDump);
        Expect(before["index_sha256"]!.GetValue<string>() == preparation["index_sha256"]!.GetValue<string>(),
            "El indice servido no coincide con el sello de 15.2.1");
        Expect(before["corpus_manifest_sha256"]!.GetValue<string>() == preparation["corpus_manifest_sha256"]!.GetValue<string>(),
            "El corpus no coincide con el sello de 15.2.1");

        var payloads = PayloadStore.Load(payloadDump);
        var byId = payloads.ToDictionary(p => p.Id, StringComparer.Ordinal);
        Console.Error.WriteLine($"[15.2.2] payloads={payloads.Count}");

        // ── Construccion de los tres mecanismos (build_seconds por brazo) ────────────
        var relativePaths = payloads.Where(p => p.RelativePath is not null)
                                    .Select(p => p.RelativePath!)
                                    .Distinct(StringComparer.Ordinal)
                                    .OrderBy(p => p, StringComparer.Ordinal)
                                    .ToList();

        var syntaxWatch = Stopwatch.StartNew();
        var syntaxBuilder = new SyntaxQualificationBuilder();
        syntaxBuilder.IndexCorpus(corpus, relativePaths);
        var syntaxMap = syntaxBuilder.Qualify(corpus, payloads);
        syntaxWatch.Stop();
        Console.Error.WriteLine($"[15.2.2] syntax: {syntaxWatch.Elapsed.TotalSeconds:F1}s " +
            $"chunks={syntaxBuilder.ChunksLocated} sin_localizar={syntaxBuilder.ChunksUnlocatable} " +
            $"invocaciones={syntaxBuilder.InvocationsSeen} cualificadas={syntaxBuilder.InvocationsQualified}");

        var semanticWatch = Stopwatch.StartNew();
        var projects = Directory.GetFiles(corpus, "*.csproj", SearchOption.AllDirectories)
                                .Where(p => !p.Contains("/obj/") && !p.Contains("/bin/"))
                                .OrderBy(p => p, StringComparer.Ordinal)
                                .ToList();
        var semanticBuilder = new SemanticQualificationBuilder(corpus);
        var semanticMap = semanticBuilder.Qualify(projects, payloads, Path.Combine(scratch, "msbuild"));
        semanticWatch.Stop();
        Console.Error.WriteLine($"[15.2.2] semantic: {semanticWatch.Elapsed.TotalSeconds:F1}s " +
            $"proyectos={semanticBuilder.ProjectsCompiled}/{projects.Count} fallidos={semanticBuilder.ProjectsFailed} " +
            $"chunks={semanticBuilder.ChunksLocated} invocaciones={semanticBuilder.InvocationsSeen} " +
            $"resueltas={semanticBuilder.InvocationsResolved} externas={semanticBuilder.InvocationsExternal}");
        foreach (var diagnostic in semanticBuilder.Diagnostics.Take(5))
            Console.Error.WriteLine($"[15.2.2]   semantic diag: {diagnostic}");
        Console.Error.WriteLine($"[15.2.2] semantic errores de compilacion por proyecto: " +
            string.Join(", ", semanticBuilder.CompilationErrors.OrderByDescending(kv => kv.Value)
                .Take(6).Select(kv => $"{kv.Key}={kv.Value}")));
        Console.Error.WriteLine($"[15.2.2] semantic codigos de error mas frecuentes: " +
            string.Join(", ", semanticBuilder.ErrorCodes.OrderByDescending(kv => kv.Value)
                .Take(8).Select(kv => $"{kv.Key}x{kv.Value}")));

        // El grafo materializa los destinos del brazo semantico como aristas persistidas:
        // su build incluye ese coste, no se le regala la resolucion ya hecha.
        var graphWatch = Stopwatch.StartNew();
        var graphPath = Path.Combine(scratch, "graph.sqlite3");
        foreach (var stale in Directory.GetFiles(scratch, "graph.sqlite3*")) File.Delete(stale);
        var graphStore = new GraphStore(graphPath);
        var (graphNodes, graphEdges) = BuildGraph(graphStore, collection, payloads, semanticMap);
        graphWatch.Stop();
        Console.Error.WriteLine($"[15.2.2] graph: {graphWatch.Elapsed.TotalSeconds:F1}s nodos={graphNodes} aristas={graphEdges}");

        var buildSeconds = new Dictionary<string, double>
        {
            ["no-expansion"] = 0,
            ["name-join"] = 0,
            ["syntax"] = syntaxWatch.Elapsed.TotalSeconds,
            ["semantic"] = semanticWatch.Elapsed.TotalSeconds,
            // El grafo no existe sin la resolucion semantica que lo alimenta: cobrarle solo
            // la escritura en SQLite lo haria parecer diez veces mas barato de lo que es.
            ["graph"] = semanticWatch.Elapsed.TotalSeconds + graphWatch.Elapsed.TotalSeconds,
        };

        // ── Contenedores: el calificador debe estar AUSENTE para los dos controles ───
        var switching = new SwitchingQualifier();
        var qualifiers = new Dictionary<string, ISymbolExpansionQualifier>
        {
            ["syntax"] = new PrecomputedQualifier("syntax", syntaxMap),
            ["semantic"] = new PrecomputedQualifier("semantic", semanticMap),
            ["graph"] = new GraphQualifier(graphStore, collection),
        };

        await using var plain = BuildProvider(twoHop: false, null);
        await using var nameJoin = BuildProvider(twoHop: true, null);
        await using var qualified = BuildProvider(twoHop: true, switching);

        IServiceProvider ProviderFor(string arm) => arm switch
        {
            "no-expansion" => plain,
            "name-join" => nameJoin,
            _ => qualified,
        };

        var retrievers = new Dictionary<string, ISemanticRetriever>(StringComparer.Ordinal);
        var scopes = new List<IServiceScope>();
        foreach (var arm in Arms)
        {
            var scope = ProviderFor(arm).CreateScope();
            scopes.Add(scope);
            retrievers[arm] = scope.ServiceProvider.GetRequiredService<ISemanticRetriever>();
        }

        var brain = plain.GetRequiredService<IVectorizationBrain>();
        using var qdrant = new QdrantClient("localhost", 6334);

        var parameters = protocol["parameters"]!.AsObject();
        var topK = parameters["top_k"]!.GetValue<int>();
        var minScore = (float)parameters["min_score"]!.GetValue<double>();
        var maxExpansion = (ulong)parameters["max_expansion_results"]!.GetValue<int>();
        var maxSeedSymbols = parameters["max_seed_symbols"]!.GetValue<int>();

        RetrievalOptions OptionsFor() => new()
        {
            Context = RetrievalContext.Local,
            CollectionName = collection,
            TopK = topK,
            MinimumSimilarityScore = minScore,
            UseReRanking = parameters["rerank"]!.GetValue<bool>(),
        };

        // ── Precision: 30 oportunidades con semilla explicita, por brazo ─────────────
        var precision = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        var precisionMs = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var arm in Arms)
        {
            if (arm == "no-expansion") { precision[arm] = new JsonArray(); precisionMs[arm] = 0; continue; }
            switching.Current = qualifiers.GetValueOrDefault(arm);

            var rows = new JsonArray();
            double elapsed = 0;
            foreach (var opportunity in opportunities)
            {
                var row = await PrecisionRowAsync(
                    opportunity!.AsObject(), arm, byId, brain, qdrant, collection,
                    qualifiers.GetValueOrDefault(arm), maxSeedSymbols, maxExpansion, minScore);
                elapsed += row["elapsed_ms"]!.GetValue<double>();
                rows.Add(row);
            }
            precision[arm] = rows;
            precisionMs[arm] = elapsed;
            var correct = rows.Count(r => r!["candidates"]!.AsArray().Count > 0);
            Console.Error.WriteLine($"[15.2.2] precision {arm}: {correct}/30 con candidatos, {elapsed / 1000:F1}s");
        }

        if (mode == "build")
        {
            foreach (var arm in Arms.Where(a => a != "no-expansion"))
            {
                var rows = precision[arm];
                var withCandidates = rows.Count(r => r!["candidates"]!.AsArray().Count > 0);
                Console.Error.WriteLine($"[15.2.2] {arm}: {withCandidates}/30 con candidatos");
            }
            var summary = new JsonObject();
            foreach (var arm in Arms) summary[arm] = precision[arm].DeepClone();

            // Diagnostico: que cualifico cada mecanismo para las 30 semillas. Distingue una
            // abstencion real (semilla presente, cero destinos) de una limitacion del banco
            // (semilla ausente del mapa porque no se pudo localizar en la fuente).
            var maps = new JsonObject();
            foreach (var (label, map) in new[] { ("syntax", syntaxMap), ("semantic", semanticMap) })
            {
                var perSeed = new JsonObject();
                foreach (var opportunity in opportunities)
                {
                    var seedId = opportunity!["seed_ids"]!.AsArray()[0]!.GetValue<string>();
                    perSeed[opportunity["id"]!.GetValue<string>()] = map.TryGetValue(seedId, out var targets)
                        ? new JsonArray(targets.Select(t => (JsonNode)(t.ClassName + "." + t.MethodName)).ToArray())
                        : null;
                }
                maps[label] = perSeed;
            }
            summary["_seed_maps"] = maps;
            await File.WriteAllTextAsync(output, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            foreach (var scope in scopes) scope.Dispose();
            graphStore.Dispose();
            return 0;
        }

        // ── Latencia y recall: el calendario exacto del protocolo ───────────────────
        var runs = new JsonArray();
        // Se acumulan MILISEGUNDOS y se divide una sola vez al final, igual que el
        // comparador: dividir en cada llamada dejaba machine_seconds unos microsegundos por
        // debajo de search_seconds y el oraculo lo rechazaba como coste subcontado.
        var armMs = Arms.ToDictionary(a => a, _ => 0.0, StringComparer.Ordinal);
        var sequence = 0;
        var scheduleWatch = Stopwatch.StartNew();

        foreach (var (phase, replicas) in new[] { ("warmup", 1), ("measure", 5) })
        {
            for (var replica = 0; replica < replicas; replica++)
            {
                for (var qi = 0; qi < cohort.Count; qi++)
                {
                    var query = cohort[qi]!.AsObject();
                    var queryId = query["id"]!.GetValue<string>();
                    var question = query["item"]!["Question"]!.GetValue<string>();
                    var shift = phase == "measure" ? (replica + qi) % Arms.Length : 0;
                    var order = Arms.Skip(shift).Concat(Arms.Take(shift));

                    foreach (var arm in order)
                    {
                        switching.Current = qualifiers.GetValueOrDefault(arm);
                        var watch = Stopwatch.StartNew();
                        var results = await retrievers[arm].SearchAsync(question, OptionsFor());
                        watch.Stop();

                        var ms = watch.Elapsed.TotalMilliseconds;
                        if (ms <= 0) ms = 1e-3;
                        armMs[arm] += ms;

                        var hits = new JsonArray();
                        foreach (var result in results.Take(topK))
                            hits.Add(new JsonObject
                            {
                                ["id"] = result.ChunkId,
                                ["content_hash"] = result.ContentHash,
                            });

                        runs.Add(new JsonObject
                        {
                            ["sequence"] = sequence++,
                            ["phase"] = phase,
                            ["replica"] = replica,
                            ["query_id"] = queryId,
                            ["question"] = question,
                            ["arm"] = arm,
                            ["succeeded"] = true,
                            ["elapsed_ms"] = ms,
                            ["hits"] = hits,
                        });
                    }
                }

                Console.Error.WriteLine(
                    $"[15.2.2] {phase} replica {replica}: {sequence} llamadas, {scheduleWatch.Elapsed.TotalSeconds:F0}s");
            }
        }
        scheduleWatch.Stop();

        foreach (var scope in scopes) scope.Dispose();

        // ── Contrato del grafo: fixtures ejecutados de verdad ───────────────────────
        var fixtureWatch = Stopwatch.StartNew();
        var observations = GraphFixtures.Execute(fixtures, Path.Combine(scratch, "fixtures"));
        fixtureWatch.Stop();
        graphStore.Dispose();

        // ── Huella final ────────────────────────────────────────────────────────────
        var after = Fingerprint(bundle, corpus, scratch, null);

        // ── Evidencia ───────────────────────────────────────────────────────────────
        var runnerSha = SourcesSha(AppContext.BaseDirectory, bundle);
        // La raiz se pregunta a git en vez de contar ".." desde el bundle: contarlos mal
        // tiraba la corrida ENTERA en la ultima linea, despues de 2.520 llamadas ya medidas.
        var repoRoot = Git("rev-parse --show-toplevel");
        var retrieverSha = FileSha(Path.Combine(repoRoot,
            "src", "RagEngine.Core", "Infrastructure", "VectorStore", "QdrantSemanticRetriever.cs"));

        var arms = new JsonObject();
        foreach (var arm in Arms)
        {
            // Coste de maquina del brazo: construccion + busquedas + precision, mas su parte
            // de la instrumentacion compartida (las dos capturas de huella del indice, que se
            // corrieron para este experimento y no por otra razon).
            var machine = buildSeconds[arm] + armMs[arm] / 1000 + precisionMs[arm] / 1000
                          + (before["seconds"]!.GetValue<double>() + after["seconds"]!.GetValue<double>())
                            / Arms.Length;
            if (arm == "graph") machine += fixtureWatch.Elapsed.TotalSeconds;

            arms[arm] = new JsonObject
            {
                ["implementation_sha256"] = ImplementationSha(arm, bundle, retrieverSha,
                    syntaxMap, semanticMap, graphPath),
                ["index_before"] = before["index_sha256"]!.GetValue<string>(),
                ["index_after"] = after["index_sha256"]!.GetValue<string>(),
                ["corpus_before"] = before["corpus_manifest_sha256"]!.GetValue<string>(),
                ["corpus_after"] = after["corpus_manifest_sha256"]!.GetValue<string>(),
                ["collection_config_before"] = before["collection_config_sha256"]!.GetValue<string>(),
                ["collection_config_after"] = after["collection_config_sha256"]!.GetValue<string>(),
                ["models_before"] = before["models"]!.DeepClone(),
                ["models_after"] = after["models"]!.DeepClone(),
                ["build_seconds"] = buildSeconds[arm],
                ["update_seconds"] = 0.0,
                ["machine_seconds"] = machine,
                ["precision"] = precision[arm].DeepClone(),
            };
        }

        var evidence = new JsonObject
        {
            ["kind"] = "measured_experiment",
            ["freeze_sha256"] = freezeSha,
            ["parameters"] = parameters.DeepClone(),
            ["runner_sha256"] = runnerSha,
            ["engine_commit"] = Git("rev-parse HEAD"),
            ["command"] = new JsonArray(rawArgs.Select(a => (JsonNode)a!).ToArray()),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["hardware"] = $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture} " +
                           $"{Environment.ProcessorCount} cores",
            ["arms"] = arms,
            ["runs"] = runs,
            ["graph_execution"] = new JsonObject
            {
                ["implementation_sha256"] = arms["graph"]!["implementation_sha256"]!.GetValue<string>(),
                ["runner_sha256"] = runnerSha,
                ["seconds"] = fixtureWatch.Elapsed.TotalSeconds,
                ["command"] = new JsonArray("graph-fixtures", "lifecycle+authorization"),
                ["observations"] = observations,
            },
            ["_instrumentation"] = new JsonObject
            {
                ["syntax_chunks_located"] = syntaxBuilder.ChunksLocated,
                ["syntax_chunks_unlocatable"] = syntaxBuilder.ChunksUnlocatable,
                ["syntax_invocations_seen"] = syntaxBuilder.InvocationsSeen,
                ["syntax_invocations_qualified"] = syntaxBuilder.InvocationsQualified,
                ["semantic_projects_compiled"] = semanticBuilder.ProjectsCompiled,
                ["semantic_projects_failed"] = semanticBuilder.ProjectsFailed,
                ["semantic_chunks_located"] = semanticBuilder.ChunksLocated,
                ["semantic_chunks_unlocatable"] = semanticBuilder.ChunksUnlocatable,
                ["semantic_invocations_seen"] = semanticBuilder.InvocationsSeen,
                ["semantic_invocations_resolved"] = semanticBuilder.InvocationsResolved,
                ["semantic_invocations_external"] = semanticBuilder.InvocationsExternal,
                ["graph_nodes"] = graphNodes,
                ["graph_edges"] = graphEdges,
                ["schedule_seconds"] = scheduleWatch.Elapsed.TotalSeconds,
                ["wall_seconds"] = totalWatch.Elapsed.TotalSeconds,
                ["fingerprint_before_seconds"] = before["seconds"]!.GetValue<double>(),
                ["fingerprint_after_seconds"] = after["seconds"]!.GetValue<double>(),
            },
        };

        await File.WriteAllTextAsync(output, evidence.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        Console.Error.WriteLine($"[15.2.2] evidencia escrita en {output} ({totalWatch.Elapsed.TotalSeconds:F0}s)");
        return 0;
    }

    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Una oportunidad de precision. El brazo recibe la semilla y la pregunta, NUNCA los
    /// destinos esperados ni el nombre del metodo del oraculo: ese campo es metadato de
    /// revision, y pasarlo convertiria la medicion en un ejercicio de emparejar la
    /// respuesta que ya se conoce.
    /// </summary>
    private static async Task<JsonObject> PrecisionRowAsync(
        JsonObject opportunity, string arm, IReadOnlyDictionary<string, ChunkPayload> byId,
        IVectorizationBrain brain, QdrantClient qdrant, string collection,
        ISymbolExpansionQualifier? qualifier, int maxSeedSymbols, ulong maxExpansion, float minScore)
    {
        var seedIds = opportunity["seed_ids"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        var question = opportunity["question"]!.GetValue<string>();

        var row = new JsonObject
        {
            ["opportunity_id"] = opportunity["id"]!.GetValue<string>(),
            ["query_id"] = opportunity["query_id"]!.GetValue<string>(),
            ["question"] = question,
            ["seed_ids"] = new JsonArray(seedIds.Select(s => (JsonNode)s!).ToArray()),
            ["seed_version"] = opportunity["seed_version"]!.GetValue<string>(),
            ["arm"] = arm,
            ["succeeded"] = true,
        };

        var watch = Stopwatch.StartNew();
        var candidates = new JsonArray();

        var symbols = new List<string>();
        foreach (var seedId in seedIds)
        {
            if (!byId.TryGetValue(seedId, out var seed) || seed.ConsumedSymbols is null) continue;
            foreach (var symbol in seed.ConsumedSymbols)
            {
                if (symbols.Count >= maxSeedSymbols) break;
                if (!string.IsNullOrWhiteSpace(symbol) && !symbols.Contains(symbol)) symbols.Add(symbol);
            }
        }

        Filter? filter = null;
        if (symbols.Count > 0)
        {
            var hop = new Filter();
            if (qualifier is null)
            {
                hop.Must.Add(Conditions.Match("defined_symbols", symbols));
            }
            else
            {
                var seeds = seedIds
                    .Where(byId.ContainsKey)
                    .Select(id => byId[id])
                    .Select(p => new SymbolExpansionSeed(p.Id, p.RelativePath, p.ClassName,
                        p.MethodName, p.Content, p.ConsumedSymbols ?? new List<string>()))
                    .ToList();

                var plan = qualifier.Qualify(seeds, symbols);
                if (!plan.IsEmpty)
                {
                    if (plan.ChunkIds.Count > 0)
                    {
                        hop.Must.Add(Conditions.HasId(plan.ChunkIds.Select(Guid.Parse).ToList()));
                    }
                    else
                    {
                        var pairs = new Filter();
                        foreach (var target in plan.Targets)
                        {
                            var pair = new Filter();
                            pair.Must.Add(Conditions.MatchKeyword("class_name", target.ClassName));
                            pair.Must.Add(Conditions.Match("defined_symbols", new List<string> { target.MethodName }));
                            pairs.Should.Add(new Condition { Filter = pair });
                        }
                        hop.Must.Add(new Condition { Filter = pairs });
                    }
                    filter = hop;
                }
            }

            if (qualifier is null) filter = hop;
        }

        if (filter is not null)
        {
            // La semilla se excluye en el propio filtro: el oraculo prohibe la auto-expansion,
            // y descartarla despues gastaria una plaza del presupuesto de 20 candidatos.
            foreach (var seedId in seedIds)
                filter.MustNot.Add(Conditions.HasId(new List<Guid> { Guid.Parse(seedId) }));

            var vector = await brain.GenerateEmbeddingAsync(question);

            // SIN scoreThreshold, igual que ExpandBySymbolAsync en produccion: el min_score
            // del protocolo filtra el prefetch DENSO de la fusion primaria, no el salto.
            // Aplicarlo aqui convertia expansiones correctas de baja similitud en
            // abstenciones inventadas por el banco.
            //
            // Prefijo completo como DeterministicVectorQuery: se pide una plaza de mas y se
            // crece mientras el corte caiga dentro de un empate, para que las cinco replicas
            // devuelvan los mismos ids en el mismo orden y no un empate resuelto al azar.
            var requested = maxExpansion + 1;
            IReadOnlyList<ScoredPoint> ordered;
            while (true)
            {
                var page = await qdrant.QueryAsync(
                    collection, query: vector, usingVector: "dense", filter: filter,
                    limit: requested, payloadSelector: new WithPayloadSelector { Enable = true },
                    searchParams: new SearchParams { Exact = true });

                ordered = page
                    .OrderByDescending(p => p.Score)
                    .ThenBy(p => Guid.Parse(p.Id.Uuid).ToString("D"), StringComparer.Ordinal)
                    .ToArray();

                if (ordered.Count < (int)requested ||
                    ordered[(int)maxExpansion - 1].Score != ordered[^1].Score) break;
                if (requested >= 32768) throw new InvalidOperationException("Empate de ranking sin resolver");
                requested = Math.Min(requested * 2, 32768);
            }

            foreach (var point in ordered.Take((int)maxExpansion))
            {
                var id = Guid.Parse(point.Id.Uuid).ToString("D");
                candidates.Add(new JsonObject
                {
                    ["id"] = id,
                    ["content_hash"] = byId[id].ContentHash,
                });
            }
        }

        watch.Stop();
        var ms = watch.Elapsed.TotalMilliseconds;
        row["elapsed_ms"] = ms <= 0 ? 1e-3 : ms;
        row["candidates"] = candidates;
        return row;
    }

    /// <summary>Materializa el grafo real: un nodo por chunk, una arista por destino resuelto.</summary>
    private static (int Nodes, int Edges) BuildGraph(
        GraphStore store, string collection, IReadOnlyList<ChunkPayload> payloads,
        IReadOnlyDictionary<string, IReadOnlyList<QualifiedSymbolTarget>> semantic)
    {
        // Indice (clase, metodo) -> chunks que lo definen, construido una vez.
        var definitions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            if (payload.ClassName is null || payload.DefinedSymbols is null) continue;
            foreach (var symbol in payload.DefinedSymbols)
            {
                var key = payload.ClassName + " " + symbol;
                if (!definitions.TryGetValue(key, out var list)) list = definitions[key] = new List<string>();
                list.Add(payload.Id);
            }
        }

        var nodes = payloads.Select(p => new GraphNode(
            p.Id, 1, collection, null,
            p.Namespace ?? p.RelativePath ?? "")).ToList();

        var edges = new List<GraphEdge>();
        foreach (var (source, targets) in semantic)
        {
            foreach (var target in targets)
            {
                if (!definitions.TryGetValue(target.ClassName + " " + target.MethodName, out var ids)) continue;
                foreach (var id in ids)
                    if (id != source)
                        edges.Add(new GraphEdge(source, 1, id, 1, 1));
            }
        }

        // Una sola transaccion: 23k nodos insertados uno a uno con su propio commit
        // convertirian build_seconds en una medida de fsync.
        store.BulkLoad(nodes, edges);
        return (nodes.Count, edges.Count);
    }

    private static ServiceProvider BuildProvider(bool twoHop, ISymbolExpansionQualifier? qualifier)
    {
        var models = Environment.GetEnvironmentVariable("RAG_MODELS_DIR")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "models");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OnnxBrain:ModelPath"] = Path.Combine(models, "paraphrase-multilingual-MiniLM-L12-v2", "model_qint8_arm64.onnx"),
            ["OnnxBrain:VocabPath"] = Path.Combine(models, "paraphrase-multilingual-MiniLM-L12-v2", "sentencepiece.bpe.model"),
            ["OnnxBrain:TokenizerType"] = "SentencePiece",
            ["OnnxBrain:MaxSequenceLength"] = "256",
            ["OnnxBrain:BatchSize"] = "32",
            ["OnnxBrain:EmbeddingDimensions"] = "384",
            ["Qdrant:Host"] = "localhost",
            ["Qdrant:GrpcPort"] = "6334",
            ["Qdrant:HttpPort"] = "6333",
            ["Qdrant:DefaultCollection"] = "bsuite-repo",
            ["TwoHop:Enabled"] = twoHop ? "true" : "false",
            ["TwoHop:SeedResults"] = "5",
            ["TwoHop:MaxExpansionResults"] = "20",
            ["TwoHop:Weight"] = "0.8",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
        services.AddRagEngineCore(configuration);
        if (qualifier is not null) services.AddSingleton(qualifier);
        return services.BuildServiceProvider();
    }

    // ── utilidades ──────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length - 1; i += 2)
            result[args[i].TrimStart('-')] = args[i + 1];
        return result;
    }

    private static JsonNode? Load(string path) => JsonNode.Parse(File.ReadAllText(path));

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static JsonObject Fingerprint(string bundle, string corpus, string scratch, string? dump)
    {
        var script = Path.Combine(bundle, "runner", "fingerprint.py");
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--corpus"); psi.ArgumentList.Add(corpus);
        psi.ArgumentList.Add("--tmp"); psi.ArgumentList.Add(Path.Combine(scratch, "corpus-manifest.json.gz"));
        if (dump is not null) { psi.ArgumentList.Add("--dump-payloads"); psi.ArgumentList.Add(dump); }
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"fingerprint.py fallo: {stderr}");
        return JsonNode.Parse(stdout)!.AsObject();
    }

    private static string Git(string arguments)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true };
        foreach (var part in arguments.Split(' ')) psi.ArgumentList.Add(part);
        using var process = Process.Start(psi)!;
        var value = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return value;
    }

    private static string FileSha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.GetFullPath(path)))).ToLowerInvariant();

    private static string TextSha(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string SourcesSha(string _, string bundle)
    {
        var runner = Path.Combine(bundle, "runner");
        var builder = new StringBuilder();
        foreach (var file in Directory.GetFiles(runner, "*.*", SearchOption.TopDirectoryOnly)
                                      .Where(f => f.EndsWith(".cs") || f.EndsWith(".py") || f.EndsWith(".csproj"))
                                      .OrderBy(f => f, StringComparer.Ordinal))
            builder.Append(Path.GetFileName(file)).Append(':').Append(FileSha(file)).Append('\n');
        return TextSha(builder.ToString());
    }

    private static string ImplementationSha(
        string arm, string bundle, string retrieverSha,
        IReadOnlyDictionary<string, IReadOnlyList<QualifiedSymbolTarget>> syntax,
        IReadOnlyDictionary<string, IReadOnlyList<QualifiedSymbolTarget>> semantic,
        string graphPath)
    {
        string MapSha(IReadOnlyDictionary<string, IReadOnlyList<QualifiedSymbolTarget>> map)
        {
            var builder = new StringBuilder();
            foreach (var (id, targets) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                builder.Append(id).Append('=');
                foreach (var target in targets.OrderBy(t => t.ClassName + " " + t.MethodName, StringComparer.Ordinal))
                    builder.Append(target.ClassName).Append('.').Append(target.MethodName).Append(',');
                builder.Append('\n');
            }
            return TextSha(builder.ToString());
        }

        var runner = Path.Combine(bundle, "runner");
        return arm switch
        {
            "no-expansion" => TextSha("no-expansion|" + retrieverSha),
            "name-join" => TextSha("name-join|" + retrieverSha),
            "syntax" => TextSha("syntax|" + retrieverSha + "|" +
                                FileSha(Path.Combine(runner, "SyntaxQualification.cs")) + "|" + MapSha(syntax)),
            "semantic" => TextSha("semantic|" + retrieverSha + "|" +
                                  FileSha(Path.Combine(runner, "SemanticQualification.cs")) + "|" + MapSha(semantic)),
            "graph" => TextSha("graph|" + retrieverSha + "|" +
                               FileSha(Path.Combine(runner, "GraphStore.cs")) + "|" +
                               FileSha(graphPath) + "|" + MapSha(semantic)),
            _ => throw new ArgumentOutOfRangeException(nameof(arm)),
        };
    }
}
