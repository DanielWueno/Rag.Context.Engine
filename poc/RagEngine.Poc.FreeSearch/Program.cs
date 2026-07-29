using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagEngine.Core.Abstractions;
using RagEngine.Core.Domain;
using RagEngine.Core.Infrastructure.Reranking;
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

var evaluator = new RecallEvaluator(settings);

if (args.Contains("sweep"))
{
    // Barrido de pesos: separado de rerank/Fase 4 normal porque sólo necesita los
    // rankings YA calculados (embeddings ya corrieron arriba) — nada de ONNX/Ollama
    // se repite por combinación, así que barrer decenas de pesos es casi instantáneo.
    Console.WriteLine("[4/4] Barriendo pesos RRF (cód+spa+res) …\n");
    var rankData = evaluator.PrepareRankData(indexed, evalSet, questionVectors, questionSparse);
    PrintWeightSweep(evaluator, rankData, indexed.Count, settings);
    sw.Stop();
    Console.WriteLine($"\nListo en {sw.Elapsed.TotalSeconds:F1}s.");
    return 0;
}

// ── Fase 4 · Recall: código vs. cód+sparse vs. cód+sparse+resumen [+ rerank] ──
// El Cross-Encoder ONNX real (mismo que producción); a propósito SIN `using`/Dispose
// explícito, mismo motivo que EmbeddingHarness más arriba: el teardown nativo de ONNX
// aborta en ARM si se dispone, y al no disponer el proceso sale limpio igual.
IReRanker? reranker = null;
if (settings.EnableRerank)
{
    Console.WriteLine($"[4/5] Cargando Cross-Encoder para rerank (TopK={settings.RerankTopK}, pool={settings.RerankTopK * 3}) …\n");
    reranker = new OnnxCrossEncoderReRanker(
        Options.Create(settings.CrossEncoder),
        NullLogger<OnnxCrossEncoderReRanker>.Instance);
}

Console.WriteLine($"[{(settings.EnableRerank ? 5 : 4)}/{(settings.EnableRerank ? 5 : 4)}] Evaluando recall@k …\n");
var report = await evaluator.EvaluateAsync(
    indexed, evalSet, questionVectors, questionSparse, reranker, settings.RerankTopK, ct);

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
    bool hasRerank = r.Configs.Contains(RecallEvaluator.CodSpaResRerank);

    Console.WriteLine("──────────────────────────────────────────────────────────────────");
    Console.WriteLine($"  Muestra: {r.Chunks} chunks ({r.ChunksWithSummary} con resumen) · {r.Questions} preguntas");
    Console.WriteLine($"  Pesos RRF: código={s.WeightCode}  sparse={s.WeightSparse}  resumen={s.WeightResumen}  (k={s.RrfK})");
    if (hasRerank)
        Console.WriteLine($"  Rerank: Cross-Encoder ON, TopK={s.RerankTopK} (pool {s.RerankTopK * 3})");
    Console.WriteLine("──────────────────────────────────────────────────────────────────");

    var header = $"  {"k",4} │ {RecallEvaluator.Codigo,11} │ {RecallEvaluator.CodSparse,11} │ {RecallEvaluator.CodSpaRes,11}";
    var sep = "  ─────┼─────────────┼─────────────┼────────────";
    if (hasRerank)
    {
        header += $" │ {RecallEvaluator.CodSpaResRerank,19}";
        sep += "┼─────────────────────";
    }
    Console.WriteLine(header);
    Console.WriteLine(sep);
    foreach (var k in kValues)
    {
        var a = r.RecallByConfig[RecallEvaluator.Codigo][k];
        var b = r.RecallByConfig[RecallEvaluator.CodSparse][k];
        var c = r.RecallByConfig[RecallEvaluator.CodSpaRes][k];
        var line = $"  {k,4} │ {a,10:P0}  │ {b,10:P0}  │ {c,10:P0}";
        if (hasRerank)
        {
            var d = r.RecallByConfig[RecallEvaluator.CodSpaResRerank][k];
            line += $"  │ {d,18:P0}";
        }
        Console.WriteLine(line);
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

    if (!hasRerank) return;

    // ¿El rerank sobre el pool ancho reduce la necesidad de calibrar pesos con precisión?
    var withRerank = r.SolvedAtMaxK[RecallEvaluator.CodSpaResRerank];
    var gainedR = withRerank.Except(withRes).ToList();
    var lostR = withRes.Except(withRerank).ToList();
    Console.WriteLine();
    Console.WriteLine($"  Aporte del rerank sobre cód+spa+res @k={r.MaxK}: " +
                      $"+{gainedR.Count} preguntas, -{lostR.Count} regresiones.");
    foreach (var q in gainedR.Take(12)) Console.WriteLine($"    + \"{Trunc(q)}\"");
    foreach (var q in lostR.Take(12)) Console.WriteLine($"    - \"{Trunc(q)}\"  (REGRESIÓN)");
}

static string Trunc(string s) => s.Length <= 70 ? s : s[..67] + "…";

// Grilla de pesos para cód+spa+res: código queda fijo en 1.0 como ancla (igual que el
// documento), sparse y resumen se barren. La métrica que decide es recall@10 (¿el
// objetivo sobrevive en el pool fusionado?) — @1/@3/@5 se muestran solo de contexto,
// porque ese es el trabajo del rerank, no de los pesos (ver hallazgo de la corrida anterior).
static void PrintWeightSweep(
    RecallEvaluator evaluator, List<RecallEvaluator.QuestionRankData> rankData, int chunkCount, PocSettings s)
{
    var kValues = s.RecallAtK.Distinct().OrderBy(x => x).ToArray();
    var maxK = kValues.Length == 0 ? 10 : kValues.Max();

    double[] sparseGrid = [0.7, 1.0, 1.3];
    double[] resumenGrid = [1.0, 1.3, 1.6, 2.0, 2.5, 3.0, 4.0];

    var results = new List<RecallEvaluator.WeightSweepResult>();
    foreach (var wSparse in sparseGrid)
        foreach (var wResumen in resumenGrid)
            results.Add(evaluator.EvaluateWeights(rankData, chunkCount, 1.0, wSparse, wResumen, s.RrfK, kValues));

    var ordered = results
        .OrderByDescending(r => r.RecallByK[maxK])
        .ThenByDescending(r => r.RecallByK.TryGetValue(5, out var r5) ? r5 : 0.0)
        .ToList();

    var baseline = results.First(r => r.WeightSparse == s.WeightSparse && r.WeightResumen == s.WeightResumen);
    var best = ordered[0];

    Console.WriteLine("─────────────────────────────────────────────────────────────────────");
    Console.WriteLine($"  Barrido de pesos RRF — cód+spa+res (código=1.0 fijo, k={s.RrfK})");
    Console.WriteLine($"  {results.Count} combinaciones · orden: recall@{maxK} desc, luego recall@5 desc");
    Console.WriteLine("─────────────────────────────────────────────────────────────────────");
    Console.WriteLine($"  {"sparse",6} │ {"resumen",7} │ " + string.Join(" │ ", kValues.Select(k => $"@{k}".PadLeft(5))));
    Console.WriteLine("  ───────┼─────────┼" + string.Concat(kValues.Select(_ => "───────┼")).TrimEnd('┼'));
    foreach (var r in ordered)
    {
        var flag = (r.WeightSparse == best.WeightSparse && r.WeightResumen == best.WeightResumen) ? "★"
                  : (r.WeightSparse == baseline.WeightSparse && r.WeightResumen == baseline.WeightResumen) ? "•"
                  : " ";
        var cells = string.Join(" │ ", kValues.Select(k => $"{r.RecallByK[k],5:P0}"));
        Console.WriteLine($"{flag} {r.WeightSparse,6:0.0} │ {r.WeightResumen,7:0.0} │ {cells}");
    }
    Console.WriteLine("─────────────────────────────────────────────────────────────────────");
    Console.WriteLine($"  ★ mejor: sparse={best.WeightSparse:0.0} resumen={best.WeightResumen:0.0} " +
                      $"→ recall@{maxK}={best.RecallByK[maxK]:P0} (baseline actual • sparse={baseline.WeightSparse:0.0} " +
                      $"resumen={baseline.WeightResumen:0.0} → recall@{maxK}={baseline.RecallByK[maxK]:P0})");
}

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
