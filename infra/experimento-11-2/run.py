#!/usr/bin/env python3
"""Prepare/freeze, run sequentially, or clean up ONLY the registered 11.2 collections."""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "docs/eval/quality/11.2"
WORK = ROOT / ".experiment-11-2"
REPO = Path.home() / "Documents/Projects/BusinessSuite.Xaf"
MODEL = Path.home() / "models/paraphrase-multilingual-MiniLM-L12-v2"
EVAL = ROOT / "docs/eval/bsuite-repo.eval-set.json"
DLL = ROOT / "infra/experimento-11-2/bin/Release/net10.0/Experiment.dll"
URL = "http://localhost:6333"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def canonical(data):
    return json.dumps(data, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode()


def write(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n")


def api(path, body=None, method=None):
    request = urllib.request.Request(URL + path, method=method,
        data=canonical(body) if body is not None else None,
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.load(response)["result"]


def points(collection, vectors=False):
    offset = None
    while True:
        body = {"limit": 256, "with_payload": True, "with_vector": vectors}
        if offset is not None:
            body["offset"] = offset
        page = api(f"/collections/{collection}/points/scroll", body)
        yield from page["points"]
        offset = page.get("next_page_offset")
        if offset is None:
            break


def inventory():
    result = {}
    for collection in sorted(x["name"] for x in api("/collections")["collections"]):
        if collection.startswith("11-2-exp-"):
            continue
        info = api("/collections/" + collection)
        fingerprints = sorted((str(p["id"]), sha(canonical(p)))
                              for p in points(collection, vectors=True))
        result[collection] = {
            "points_count": info["points_count"],
            "config_sha256": sha(canonical(info["config"])),
            "all_points_payloads_vectors_sha256": sha(canonical(fingerprints))
        }
    return result


def corpus():
    paths = subprocess.check_output(["git", "-C", str(REPO), "ls-files", "-z"]).split(b"\0")
    return {os.fsdecode(p): sha((REPO / os.fsdecode(p)).read_bytes())
            for p in paths if p and (REPO / os.fsdecode(p)).is_file()}


def prepare():
    if (OUT / "protocolo.json").exists():
        raise RuntimeError("Frozen protocol already exists; do not overwrite.")
    OUT.mkdir(parents=True, exist_ok=True)
    WORK.mkdir(exist_ok=True)
    upstream = "https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2"
    with urllib.request.urlopen(
        "https://huggingface.co/api/models/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2",
        timeout=60) as response:
        revision = json.load(response)["sha"]
    sources = {}
    for filename in ["sentence_bert_config.json", "README.md"]:
        url = f"{upstream}/raw/{revision}/{filename}"
        with urllib.request.urlopen(url, timeout=60) as response:
            content = response.read()
        dest = OUT / ("model-card.md" if filename == "README.md" else filename)
        dest.write_bytes(content)
        sources[filename] = {"url": url, "sha256": sha(content), "artifact": str(dest.relative_to(ROOT))}
    shutil.copyfile(MODEL / "config.json", OUT / "model-config.json")
    assert json.loads((OUT / "sentence_bert_config.json").read_text())["max_seq_length"] == 128
    assert json.loads((OUT / "model-config.json").read_text())["max_position_embeddings"] == 512
    model_files = {name: sha((MODEL / name).read_bytes()) for name in
                   ["config.json", "model_qint8_arm64.onnx", "sentencepiece.bpe.model",
                    "tokenizer_config.json", "tokenizer.json"]}
    dataset = json.loads(EVAL.read_text())
    assert len(dataset) == 55
    assert sum(bool(q.get("SourceFile") or q.get("SourceFiles")) for q in dataset) == 47
    manifest = corpus()
    write(OUT / "corpus-manifest.json", manifest)
    initial = inventory()
    assert len(initial) == 10, f"Expected 10 served collections, got {len(initial)}"
    write(OUT / "served-before.json", initial)
    dirty = subprocess.check_output(["git", "status", "--short"], cwd=ROOT, text=True)
    code_files = subprocess.check_output(["git", "ls-files", "src", "tests"], cwd=ROOT, text=True).splitlines()
    engine_files = {p: sha((ROOT / p).read_bytes()) for p in code_files if (ROOT / p).is_file()}
    write(OUT / "engine-manifest.json", engine_files)
    protocol = {
        "item": "11.2-experimento-3-brazos", "frozen_at_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "execution": {"model": "gpt-6-astra", "effort": "high", "machine_hours_authorized": 1.5,
                      "multiagente": False, "environment": "Copilot CLI"},
        "engine_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "initial_git_status": dirty,
        "existing_unrelated_changes": "infra/bench/ is pre-existing, preserved and excluded; ledger en_curso/_ejecucion set by parent.",
        "engine_manifest_sha256": sha(canonical(engine_files)),
        "corpus_commit": subprocess.check_output(["git", "-C", str(REPO), "rev-parse", "HEAD"], text=True).strip(),
        "corpus_status": subprocess.check_output(["git", "-C", str(REPO), "status", "--short"], text=True),
        "corpus_manifest_sha256": sha(canonical(manifest)),
        "corpus_manifest_scope": "All tracked regular files; scanner input is additionally pinned by indexed ID/content/enriched hashes. Untracked .DS_Store is not source input.",
        "eval_set": str(EVAL.relative_to(ROOT)), "eval_set_sha256": sha(EVAL.read_bytes()),
        "n": 55, "answerable": 47, "negatives": 8,
        "model": {"upstream_revision": revision, "sources": sources, "local_sha256": model_files,
                  "effective_model": "model_qint8_arm64.onnx",
                  "recommended_sbert_max_seq_length": 128, "transformer_max_position_embeddings": 512,
                  "distinction": "128 is the verified SentenceTransformer recommended sequence setting, not proof of all training sequences; 512 is positional capacity, NOT evidence of training at 512.",
                  "special_tokens": 2},
        "arms": {
            "a": {"max_sequence_length": 256, "usable_tokens": 254, "label": "current deployed control"},
            "b": {"max_sequence_length": 128, "usable_tokens": 126, "label": "verified recommended effective token budget"},
            "c": {"max_sequence_length": 512, "usable_tokens": 510, "label": "out of recommended training distribution; require finite full-window ONNX probe"}
        },
        "budget_formula": "T=11.1.TotalTokens(enriched_content), including the complete header/prefix but excluding specials. S=2. M_A=256; M_B=min(256,128)=128; M_C=512. L=M-S; discarded=max(0,T-L); full ONNX input=min(T,L)+S<=M. Count all admitted chunks, no queries/summaries/retries. This experiment varies encoder windows, NOT chunk segmentation: B does not claim zero truncation or implement 11.3.",
        "constant_options": {"index_short_type_declarations": False, "chunking_contract_version": 3,
            "chunking_max_estimated_tokens": 512, "overlap_estimated_tokens": 64,
            "repository_name": "bsuite-repo", "batch_size": 32, "rerank": False,
            "min_score": 0.10, "top_k": 10, "bands": ["dense", "sparse-code"],
            "resumen": False, "weight_codigo_declared": 1.0, "weight_sparse_declared": 1.3,
            "weight_resumen_declared": 2.5, "rrf_k_declared": 60},
        "fusion_semantics": "Actual two-vector Qdrant schema => production native unweighted RRF; declared three-band weights and RrfK are NOT used by this branch. Sparse remains full-content. Dense-only is a direct dense QueryAsync using the same embedder, threshold and production payload mapper; weights=0 would NOT disable branches.",
        "summary_scope": "Ollama is available, but summaries are optional and deliberately excluded before measuring all arms. No LLM generation and no reads/writes of shared cache; each ingestion gets a new isolated SQLite path. Raw unique hash count equals cold isolated-cache misses, reported after ingestion. Does not substitute for historical three-band baseline or close 5.h.",
        "evaluation": {
            "primary_hit": "rag eval hit_any_at_k['10']: any literal anchor, Ordinal, in the expected source basename (case-insensitive) among first 10 results.",
            "secondary_hit": "hit_full_at_k['10'] reported, not used to move the primary threshold.",
            "gain": "A miss -> candidate hit on the SAME answerable question and paired replica",
            "loss": "A hit -> candidate miss; gross individual losses (not hidden by gains)",
            "symbol": "The 12 pre-existing Category=simbolo questions; report dense and fusion separately.",
            "truncation": "Predeclared strata per question: all/some/none anchor-bearing indexed chunks have T>L for each arm. Match every anchor candidate before evaluation using unchanged payload content; record chunk ID, raw hash, enriched hash and 11.1 stats. Strata are diagnostic, not a changed denominator or proof an anchor itself was cut.",
            "latency": "Per-question SearchAsync wall milliseconds, includes query embedding and Qdrant; all 55 in fixed original order; p50/p95 nearest-rank and mean, first query retained. Process elapsed separately. No latency acceptance threshold invented.",
            "negatives": "8 unchanged questions have no recall oracle: retain scores/ranks/latencies, do NOT count them as successes or invent a score threshold across cosine/RRF.",
            "replicas": "Two fresh independent ingestions per arm, sequential a1,b1,c1,a2,b2,c2. Compare each candidate replica with matching A replica; do not select best run. Candidate qualifies only when BOTH replicate comparisons meet the literal gate.",
            "gate": "dense net gains >=2 AND fusion gross losses <=1 AND fusion Category=simbolo gross losses <=1, same 47+8 n. Also report dense symbol losses; no activation of 11.3.",
            "failure_policy": "Fail if anchors, counts, required provenance, input hashes, trace coverage or command exits fail; no success from omitted infrastructure. Exclude C only for reproducible technical probe incompatibility, not poor recall. Null quality outcome is valid.",
            "instrument": "Experimental .NET host reuses unchanged IngestCommand and EvalCommand, IVectorizationBrain stats from 11.1, production fusion and private payload mapper. Decorators only write token/latency artifacts and select direct dense query. Production source/options files remain unchanged."
        },
        "collections": [f"11-2-exp-{a}-{r}" for r in [1, 2] for a in ["a", "b", "c"]],
        "rollback": "Delete ONLY collections listed here AND recorded as created in created.json; compare full sorted point/payload/vector SHA256 and schema hashes against served-before.json for all 10 served collections. Delete isolated working caches/logs after preserving evidence; subprocess env overrides do not persist.",
        "acceptance_command": "python3 infra/experimento-11-2/verify.py"
    }
    write(OUT / "protocolo.json", protocol)
    (OUT / "protocolo.sha256").write_text(sha((OUT / "protocolo.json").read_bytes()) + "\n")
    print("Protocol frozen", OUT / "protocolo.json", flush=True)


def env_for(arm, run):
    env = os.environ.copy()
    env.update({
        "OnnxBrain__MaxSequenceLength": str({"a": 256, "b": 128, "c": 512}[arm]),
        "Ingestion__IndexShortTypeDeclarations": "false",
        "Ingestion__ResumenCachePath": str(WORK / (run + ".sqlite3")),
        "EXPERIMENT_TOKENS": str(WORK / (run + ".tokens.jsonl")),
        "RAG_LOGS_DIR": str(WORK / "logs"),
        "DOTNET_NOLOGO": "1"
    })
    return env


def command(args, name, env=None, timeout=1800):
    start = time.monotonic()
    with (OUT / (name + ".stdout")).open("w") as stdout, (OUT / (name + ".stderr")).open("w") as stderr:
        process = subprocess.run(args, cwd=ROOT, env=env, stdout=stdout, stderr=stderr, timeout=timeout)
    result = {"command": args, "exit_code": process.returncode, "elapsed_seconds": time.monotonic()-start}
    write(OUT / (name + ".command.json"), result)
    print(name, result["exit_code"], round(result["elapsed_seconds"], 2), "s", flush=True)
    return process.returncode


def anchor_diagnostics(collection, run):
    token_rows = [json.loads(line) for line in (WORK / (run + ".tokens.jsonl")).read_text().splitlines()]
    stats = {r["enriched_sha256"]: r["stats"] for r in token_rows}
    questions = json.loads(EVAL.read_text())
    matches = [[] for _ in questions]
    hashes = []
    raw_hashes = set()
    for point in points(collection):
        p = point["payload"]
        enriched_sha = sha(p["enriched_content"].encode())
        assert enriched_sha in stats, "Indexed chunk missing 11.1 measurement"
        raw_hashes.add(p["content_hash"])
        hashes.append((str(point["id"]), p["content_hash"], enriched_sha))
        for i, question in enumerate(questions):
            filenames = question.get("SourceFiles") or [question.get("SourceFile")]
            if Path(p["relative_path"]).name.lower() not in [str(f).lower() for f in filenames]:
                continue
            anchors = [a for a in question["TargetContentContains"] if a in p["content"]]
            if anchors:
                matches[i].append({"id": point["id"], "content_hash": p["content_hash"],
                    "enriched_sha256": enriched_sha, "stats": stats[enriched_sha],
                    "class_name": p.get("class_name"), "method_name": p.get("method_name"),
                    "anchors": anchors})
    write(OUT / (run + ".anchors.json"), [
        {"question": q["Question"], "category": q["Category"], "chunks": m}
        for q, m in zip(questions, matches)])
    ts = sorted(r["stats"]["TotalTokens"] for r in token_rows)
    write(OUT / (run + ".ingestion.json"), {
        "chunks_observed": len(token_rows), "indexed_points": len(hashes),
        "indexed_id_content_enriched_sha256": sha(canonical(sorted(hashes))),
        "unique_raw_hashes": len(raw_hashes), "cold_isolated_summary_cache_misses": len(raw_hashes),
        "llm_calls": 0, "total_tokens": sum(ts),
        "p50": ts[__import__("math").ceil(.50*len(ts))-1],
        "p95": ts[__import__("math").ceil(.95*len(ts))-1],
        "usable_tokens": token_rows[0]["stats"]["MaxUsableTokens"],
        "truncated": sum(r["stats"]["Truncated"] for r in token_rows),
        "discarded_tokens": sum(r["stats"]["Discarded"] for r in token_rows)})
    with gzip.open(OUT / (run + ".tokens.jsonl.gz"), "wb") as dest:
        dest.write((WORK / (run + ".tokens.jsonl")).read_bytes())


def cleanup():
    if not (OUT / "created.json").exists():
        return
    protocol = json.loads((OUT / "protocolo.json").read_text())
    created = json.loads((OUT / "created.json").read_text())
    deleted = []
    present = {x["name"] for x in api("/collections")["collections"]}
    for name in created:
        assert name.startswith("11-2-exp-") and name in protocol["collections"]
        if name in present:
            api("/collections/" + name, method="DELETE")
            deleted.append(name)
    after = inventory()
    write(OUT / "served-after.json", after)
    before = json.loads((OUT / "served-before.json").read_text())
    remaining = sorted(x["name"] for x in api("/collections")["collections"])
    write(OUT / "rollback.json", {"deleted": deleted, "remaining": remaining,
        "served_fingerprints_identical": before == after,
        "no_experimental_collections_remaining": not set(created).intersection(remaining)})
    assert before == after, "Served collection fingerprint changed"
    assert not set(created).intersection(remaining)
    if WORK.exists():
        shutil.rmtree(WORK)
    print("Rollback verified: all 10 original collections byte-fingerprinted intact.", flush=True)


def run():
    protocol = json.loads((OUT / "protocolo.json").read_text())
    assert sha((OUT / "protocolo.json").read_bytes()) == (OUT / "protocolo.sha256").read_text().strip()
    assert sha(canonical(corpus())) == protocol["corpus_manifest_sha256"]
    assert sha(EVAL.read_bytes()) == protocol["eval_set_sha256"]
    assert not (OUT / "created.json").exists(), "Do not overwrite prior experiment evidence"
    created = []
    write(OUT / "created.json", created)
    present = {x["name"] for x in api("/collections")["collections"]}
    assert not set(protocol["collections"]).intersection(present), "Experimental names already exist"
    try:
        probe = command(["dotnet", str(DLL), "probe"], "c-probe", env_for("c", "probe"))
        if probe:
            write(OUT / "c-exclusion.json", {"reason": "Technical 512-position probe failed",
                "command_evidence": "c-probe.command.json", "error_evidence": ["c-probe.stdout", "c-probe.stderr"]})
        for replica in [1, 2]:
            for arm in ["a", "b", "c"]:
                if arm == "c" and probe:
                    continue
                assert sha(canonical(corpus())) == protocol["corpus_manifest_sha256"]
                name = f"11-2-exp-{arm}-{replica}"
                run_id = f"{arm}-{replica}"
                created.append(name)
                write(OUT / "created.json", created)
                env = env_for(arm, run_id)
                assert not (WORK / (run_id + ".tokens.jsonl")).exists()
                assert command(["dotnet", str(DLL), "ingest", str(REPO), "-c", name,
                    "-r", "bsuite-repo", "-b", "32"], run_id + ".ingest", env) == 0
                assert command(["python3", "infra/verificar-anclas-eval.py", "--eval-set",
                    str(EVAL.relative_to(ROOT)), "--collection", name, "--repo", str(REPO),
                    "--verbose"], run_id + ".anclas") == 0, "Anchor validation failed; no eval allowed"
                anchor_diagnostics(name, run_id)
                for mode in ["dense", "fusion"]:
                    prefix = run_id + "." + mode
                    env["EXPERIMENT_MODE"] = mode
                    env["EXPERIMENT_TRACE"] = str(OUT / (prefix + ".trace.jsonl"))
                    assert command(["dotnet", str(DLL), "eval", "--eval-set",
                        str(EVAL.relative_to(ROOT)), "-c", name, "-k", "10",
                        "--min-score", "0.10", "--json"], prefix, env) == 0
                    baseline = json.loads((OUT / (prefix + ".stdout")).read_text())
                    assert len(baseline["results"]) == 55
                    (OUT / (prefix + ".stdout")).rename(OUT / (prefix + ".baseline.json"))
        assert sha(canonical(corpus())) == protocol["corpus_manifest_sha256"]
        write(OUT / "execution-complete.json", {"completed": True,
            "eval_set_sha256_after": sha(EVAL.read_bytes()),
            "corpus_manifest_sha256_after": sha(canonical(corpus()))})
    finally:
        cleanup()


if __name__ == "__main__":
    os.chdir(ROOT)
    parser = argparse.ArgumentParser()
    parser.add_argument("phase", choices=["prepare", "run", "cleanup"])
    args = parser.parse_args()
    {"prepare": prepare, "run": run, "cleanup": cleanup}[args.phase]()
