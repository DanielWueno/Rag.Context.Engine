using System.Diagnostics;
using RagEngine.Core.Domain;
using RagEngine.Poc.FreeSearch;

// ─────────────────────────────────────────────────────────────────────────────
//  PoC — Búsqueda libre para usuarios no técnicos (RRF a 3 bandas)
//
//  Mide, offline y en memoria, si añadir un vector de "resumen de negocio" mejora
//  el RECALL frente al baseline de sólo-código. NO toca el pipeline de ingesta real
//  ni Qdrant. Ver README.md para el flujo completo y el criterio de go/no-go.
// ─────────────────────────────────────────────────────────────────────────────

var settingsArg = args.Length > 0 ? args[0] : "poc-settings.json";
Console.WriteLine($"== PoC búsqueda libre RRF ==  (config: {settingsArg})\n");

// Resuelve sin depender del CWD (ambiguo con `dotnet run`): CWD → junto al ejecutable.
var settingsPath = PathResolver.FindFile(settingsArg);
if (settingsPath is null)
{
    Console.Error.WriteLine($"ERROR: no se encontró el archivo de configuración '{settingsArg}'.");
    return 1;
}
var settingsDir = Path.GetDirectoryName(settingsPath)!;

PocSettings settings;
try
{
    settings = PocSettings.Load(settingsPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR de configuración: {ex.Message}");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;
var sw = Stopwatch.StartNew();

// ── Fase 1 · Chunking (reutiliza los chunkers del motor, sin pipeline) ────────
Console.WriteLine($"[1/4] Chunking de {settings.SourceRoot} …");
var harvester = new ChunkHarvester();
var chunks = await harvester.HarvestAsync(settings.SourceRoot, ct);
Console.WriteLine($"      {chunks.Count} chunks de {chunks.Select(c => c.Metadata.RelativeFilePath).Distinct().Count()} archivos.\n");
if (chunks.Count == 0)
{
    Console.Error.WriteLine("No se generaron chunks. Revisa SourceRoot.");
    return 1;
}

// ── Fase 2 · Resúmenes de negocio (qwen2.5-coder local) ───────────────────────
Console.WriteLine($"[2/4] Generando resúmenes con {settings.Ollama.ModelId} …");
var generator = new SummaryGenerator(settings);

// Caché (Reto A en pequeño): evita re-generar en cada corrida mientras iteras el eval.
var cachePath = Path.Combine(settingsDir, "poc-summaries-cache.json");
var cache = SummaryCache.Load(cachePath, $"{SummaryGenerator.PromptVersion}-{settings.Ollama.ModelId}");

var summaries = new string?[chunks.Count]; // null = sin resumen (sentinel o fallo)
var sentinelByLang = new Dictionary<SourceLanguage, (int total, int sinNegocio, int fail)>();
var statsLock = new object();
int done = 0;

Console.WriteLine($"      concurrencia = {settings.GenerationConcurrency}");
var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = settings.GenerationConcurrency, CancellationToken = ct };

await Parallel.ForEachAsync(Enumerable.Range(0, chunks.Count), parallelOpts, async (i, token) =>
{
    var chunk = chunks[i];
    bool fail = false, sinNegocio = false;

    var (found, cached) = cache.TryGet(chunk.ContentHash);
    if (found)
    {
        if (cached is null) sinNegocio = true; else summaries[i] = cached;
    }
    else
    {
        var result = await generator.GenerateAsync(chunk, token);
        if (result is null) { fail = true; }                    // fallo: no se cachea, se reintenta
        else if (result.SinNegocio) { sinNegocio = true; cache.Set(chunk.ContentHash, null); }
        else { summaries[i] = result.Text; cache.Set(chunk.ContentHash, result.Text); }
    }

    lock (statsLock)
    {
        var acc = sentinelByLang.GetValueOrDefault(chunk.Metadata.Language);
        acc.total++;
        if (fail) acc.fail++;
        else if (sinNegocio) acc.sinNegocio++;
        sentinelByLang[chunk.Metadata.Language] = acc;

        done++;
        if (done % 50 == 0 || done == chunks.Count)
            Console.WriteLine($"      {done}/{chunks.Count} …");
        if (done % 100 == 0) cache.Save(); // guardado periódico: un kill no pierde lo generado
    }
});
cache.Save();
Console.WriteLine($"      (caché: {cache.Hits} hits de {chunks.Count} → {cachePath})");
Console.WriteLine();

// Calidad del resumen por lenguaje (proxy: cuántos cayeron a sentinel o fallo).
Console.WriteLine("      Cobertura de resumen por lenguaje (gate cualitativo del paso 2):");
foreach (var (lang, s) in sentinelByLang.OrderBy(kv => kv.Key.ToString()))
{
    var withSummary = s.total - s.sinNegocio - s.fail;
    Console.WriteLine($"        {lang,-12} {withSummary,4}/{s.total,-4} con resumen  " +
                      $"(sentinel {s.sinNegocio}, fallo {s.fail})");
}
Console.WriteLine();

// ── Fase 3 · Embeddings (mismo OnnxBrain, standalone) ─────────────────────────
Console.WriteLine("[3/4] Vectorizando código, resúmenes y preguntas …");
// A propósito SIN `using`: el destructor nativo de ONNX aborta en el teardown en ARM
// (libc++abi: mutex lock failed) — un abort nativo, no un throw administrado. Al no
// disponer, ese destructor no corre y el proceso sale limpio (la RAM la reclama el SO).
var embedder = new EmbeddingHarness(settings);

// Baseline = EnrichedContent (lo que embebe el motor hoy), no Content crudo.
var codeVectors = await EmbedWithProgress(embedder, chunks.Select(c => c.EnrichedContent).ToList(), "código", ct);

// Resúmenes: sólo los chunks que tienen uno; se embeben en batch y se mapean de vuelta.
var summaryIdx = new List<int>();
var summaryTexts = new List<string>();
for (int i = 0; i < summaries.Length; i++)
    if (summaries[i] is { } t) { summaryIdx.Add(i); summaryTexts.Add(t); }
var summaryVecs = await EmbedWithProgress(embedder, summaryTexts, "resúmenes", ct);

var summaryByChunk = new float[chunks.Count][];
for (int j = 0; j < summaryIdx.Count; j++) summaryByChunk[summaryIdx[j]] = summaryVecs[j];

// Rama dispersa (BM25) — el mismo tokenizer del motor, sobre EnrichedContent. Barata, sin ONNX.
var sparse = new SparseHarness();
var sparseByChunk = chunks.Select(c => sparse.Vectorize(c.EnrichedContent)).ToArray();
Console.WriteLine($"      sparse: {chunks.Count} chunks tokenizados.");

var indexed = new List<RecallEvaluator.IndexedChunk>(chunks.Count);
for (int i = 0; i < chunks.Count; i++)
    indexed.Add(new RecallEvaluator.IndexedChunk(chunks[i], codeVectors[i], summaryByChunk[i], sparseByChunk[i]));

// EvalSetPath es relativo al archivo de configuración, no al CWD.
var evalPath = PathResolver.ResolveAgainst(settings.EvalSetPath, settingsDir);
var evalSet = EvalSetLoader.Load(evalPath);
var questionVectors = await embedder.EmbedBatchAsync(evalSet.Select(e => e.Question).ToList(), ct);
var questionSparse = evalSet.Select(e => sparse.Vectorize(e.Question)).ToList();
Console.WriteLine($"      {evalSet.Count} preguntas del set '{evalPath}'.\n");

// ── Fase 4 · Recall: código vs. cód+sparse vs. cód+sparse+resumen ─────────────
Console.WriteLine("[4/4] Evaluando recall@k …\n");
var evaluator = new RecallEvaluator(settings);
var report = evaluator.Evaluate(indexed, evalSet, questionVectors, questionSparse);

PrintReport(report, settings);
if (args.Contains("detail")) PrintDetail(report);
sw.Stop();
Console.WriteLine($"\nListo en {sw.Elapsed.TotalSeconds:F1}s.");

// Salir sin correr el destructor nativo de ONNX: en ARM lanza un abort de libc++abi
// (mutex lock failed) en el teardown — no es un throw administrado, así que un
// try/catch NO lo atrapa. Environment.Exit evita la finalización y sale limpio.
return 0;

// ─────────────────────────────────────────────────────────────────────────────
// Embebe una lista en ventanas, imprimiendo progreso — la fase 3 ya no es muda.
static async Task<float[][]> EmbedWithProgress(EmbeddingHarness emb, IReadOnlyList<string> texts, string label, CancellationToken ct)
{
    const int window = 100;
    var outv = new float[texts.Count][];
    if (texts.Count == 0) { Console.WriteLine($"      {label}: 0 …"); return outv; }
    for (int start = 0; start < texts.Count; start += window)
    {
        ct.ThrowIfCancellationRequested();
        var slice = texts.Skip(start).Take(Math.Min(window, texts.Count - start)).ToList();
        var vecs = await emb.EmbedBatchAsync(slice, ct);
        for (int k = 0; k < vecs.Length; k++) outv[start + k] = vecs[k];
        Console.WriteLine($"      {label}: {Math.Min(start + window, texts.Count)}/{texts.Count} …");
    }
    return outv;
}

static void PrintReport(RecallEvaluator.Report r, PocSettings s)
{
    var kValues = s.RecallAtK.Distinct().OrderBy(x => x).ToArray();
    Console.WriteLine("──────────────────────────────────────────────────────────────────");
    Console.WriteLine($"  Muestra: {r.Chunks} chunks ({r.ChunksWithSummary} con resumen) · {r.Questions} preguntas");
    Console.WriteLine($"  Pesos RRF: código={s.WeightCode}  sparse={s.WeightSparse}  resumen={s.WeightResumen}  (k={s.RrfK})");
    Console.WriteLine("──────────────────────────────────────────────────────────────────");
    Console.WriteLine($"  {"k",4} │ {RecallEvaluator.Codigo,11} │ {RecallEvaluator.CodSparse,11} │ {RecallEvaluator.CodSpaRes,11}");
    Console.WriteLine("  ─────┼─────────────┼─────────────┼────────────");
    foreach (var k in kValues)
    {
        var a = r.RecallByConfig[RecallEvaluator.Codigo][k];
        var b = r.RecallByConfig[RecallEvaluator.CodSparse][k];
        var c = r.RecallByConfig[RecallEvaluator.CodSpaRes][k];
        Console.WriteLine($"  {k,4} │ {a,10:P0}  │ {b,10:P0}  │ {c,10:P0}");
    }
    Console.WriteLine("──────────────────────────────────────────────────────────────────");

    // La pregunta que decide: ¿qué aporta el resumen SOBRE el híbrido real (cód+sparse)?
    var withRes = r.SolvedAtMaxK[RecallEvaluator.CodSpaRes];
    var noRes = r.SolvedAtMaxK[RecallEvaluator.CodSparse];
    var gained = withRes.Except(noRes).ToList();   // resuelve gracias al resumen
    var lost = noRes.Except(withRes).ToList();      // regresión por añadir el resumen
    Console.WriteLine($"  Aporte del resumen sobre cód+sparse @k={r.MaxK}: " +
                      $"+{gained.Count} preguntas, -{lost.Count} regresiones.");
    foreach (var q in gained.Take(12)) Console.WriteLine($"    + \"{Trunc(q)}\"");
    foreach (var q in lost.Take(12)) Console.WriteLine($"    - \"{Trunc(q)}\"  (REGRESIÓN)");
}

static string Trunc(string s) => s.Length <= 70 ? s : s[..67] + "…";

// Detalle por pregunta: qué recuperó la config completa (cód+sparse+resumen) y dónde
// cayó el objetivo. Sirve para juzgar a ojo la factibilidad de las preguntas y la
// calidad de lo recuperado. Nota: el PoC recupera contexto, NO redacta la respuesta.
static void PrintDetail(RecallEvaluator.Report r)
{
    Console.WriteLine("\n════════ Detalle por pregunta (config cód+sparse+resumen) ════════");
    foreach (var d in r.Details)
    {
        var estado = d.BestTargetRank is int br
            ? (br <= 10 ? $"objetivo en #{br} ✓" : $"objetivo lejos (#{br})")
            : "objetivo NO recuperado";
        Console.WriteLine($"\n▸ {d.Question}");
        Console.WriteLine($"    objetivo esperado: {string.Join(", ", d.TargetPaths.Select(ShortPath))}  →  {estado}");
        foreach (var c in d.Top)
        {
            var mark = c.IsTarget ? "✓" : " ";
            var res = c.HasSummary ? "" : "  [sin-resumen]";
            Console.WriteLine($"    {mark} #{c.Rank} {ShortPath(c.Path)}:{c.StartLine}-{c.EndLine} ({c.Type}){res}");
        }
    }
}

static string ShortPath(string p)
{
    var parts = p.Replace('\\', '/').Split('/');
    return parts.Length <= 2 ? p : string.Join('/', parts[^2..]);
}
