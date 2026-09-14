#!/usr/bin/env python3
"""Fail-closed acceptance/evidence verifier; null recall improvement is valid."""
import gzip
import json
import math
from pathlib import Path
import statistics
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from run import ROOT, OUT, EVAL, sha, canonical, write


def load(name):
    return json.loads((OUT / name).read_text())


def hit(row, kind="hit_any_at_k"):
    return row[kind].get("10", False)


def latency(rows):
    values = sorted(r["elapsed_ms"] for r in rows)
    return {"n": len(values), "mean_ms": statistics.mean(values),
            "p50_ms": values[math.ceil(.50*len(values))-1],
            "p95_ms": values[math.ceil(.95*len(values))-1],
            "total_ms": sum(values)}


def strata(chunks):
    if not chunks:
        return "no_anchor_chunks"
    n = sum(c["stats"]["Truncated"] for c in chunks)
    return "all_truncated" if n == len(chunks) else "some_truncated" if n else "none_truncated"


def validate():
    protocol = load("protocolo.json")
    assert sha((OUT / "protocolo.json").read_bytes()) == (OUT / "protocolo.sha256").read_text().strip()
    assert sha(EVAL.read_bytes()) == protocol["eval_set_sha256"]
    dataset = json.loads(EVAL.read_text())
    questions = [q["Question"] for q in dataset]
    assert len(set(questions)) == 55
    assert sum(q["Category"] == "simbolo" for q in dataset) == 12
    assert load("execution-complete.json")["completed"]
    assert load("execution-complete.json")["corpus_manifest_sha256_after"] == protocol["corpus_manifest_sha256"]
    assert load("execution-complete.json")["eval_set_sha256_after"] == protocol["eval_set_sha256"]
    for name in ["engine", "corpus"]:
        assert sha(canonical(load(name + "-manifest.json"))) == protocol[name + "_manifest_sha256"]
    for path, digest in load("engine-manifest.json").items():
        assert sha((ROOT / path).read_bytes()) == digest, f"Production file changed: {path}"
    assert len(load("served-before.json")) == 10
    assert load("served-before.json") == load("served-after.json")
    rollback = load("rollback.json")
    assert rollback["served_fingerprints_identical"] and rollback["no_experimental_collections_remaining"]
    arms = ["a", "b", "c"]
    probe = load("c-probe.command.json")
    if (OUT / "c-exclusion.json").exists():
        assert probe["exit_code"] != 0
        assert (OUT / "c-probe.stderr").stat().st_size + (OUT / "c-probe.stdout").stat().st_size > 0
        arms.remove("c")
    else:
        assert probe["exit_code"] == 0
        probe_result = load("c-probe.stdout")
        assert probe_result["window"] == 512 and probe_result["Stats"][0]["TotalTokens"] > 510
    expected_created = {f"11-2-exp-{a}-{r}" for a in arms for r in [1, 2]}
    assert set(load("created.json")) == expected_created
    assert set(rollback["deleted"]) == expected_created
    baselines, traces, anchor_sets, ingestion = {}, {}, {}, {}
    index_hashes = set()
    for a in arms:
        for r in [1, 2]:
            key = f"{a}-{r}"
            assert load(key + ".ingest.command.json")["exit_code"] == 0
            assert load(key + ".anclas.command.json")["exit_code"] == 0
            assert "Todo verificado — 55 preguntas" in (OUT / (key + ".anclas.stdout")).read_text()
            measured = load(key + ".ingestion.json")
            ingestion[key] = measured
            assert measured["indexed_points"] == measured["chunks_observed"] > 0
            index_hashes.add(measured["indexed_id_content_enriched_sha256"])
            with gzip.open(OUT / (key + ".tokens.jsonl.gz"), "rt") as f:
                tokens = [json.loads(line) for line in f]
            assert len(tokens) == measured["chunks_observed"]
            usable = protocol["arms"][a]["usable_tokens"]
            for token in tokens:
                s = token["stats"]
                assert s["MaxUsableTokens"] == usable
                assert s["Discarded"] == max(0, s["TotalTokens"]-usable)
                assert s["Truncated"] == (s["TotalTokens"] > usable)
            assert measured["truncated"] == sum(t["stats"]["Truncated"] for t in tokens)
            assert measured["discarded_tokens"] == sum(t["stats"]["Discarded"] for t in tokens)
            anchors = load(key + ".anchors.json")
            assert [q["question"] for q in anchors] == questions
            for q, anchor in zip(dataset, anchors):
                for text in q["TargetContentContains"]:
                    assert any(text in c["anchors"] for c in anchor["chunks"])
            anchor_sets[key] = anchors
            for mode in ["dense", "fusion"]:
                prefix = key + "." + mode
                assert load(prefix + ".command.json")["exit_code"] == 0
                baseline = load(prefix + ".baseline.json")
                provenance = baseline["provenance"]
                assert baseline["top_k"] == 10 and baseline["min_score"] == .1 and not baseline["rerank"]
                assert baseline["collection"] == f"11-2-exp-{key}"
                assert provenance["embedding_max_sequence_length"] == usable+2
                assert provenance["embedding_dimensions"] == 384
                assert provenance["embedding_model"] == protocol["model"]["effective_model"]
                assert provenance["index_short_type_declarations"] is False
                assert provenance["chunking_contract_version"] == 3
                assert provenance["eval_set_hash"] == protocol["eval_set_sha256"][:12]
                assert provenance["git_commit"] == protocol["engine_commit"][:len(provenance["git_commit"])]
                assert provenance["weight_codigo"] == 1.0 and provenance["weight_sparse"] == 1.3
                assert provenance["weight_resumen"] == 2.5 and provenance["rrf_k"] == 60
                assert provenance["cross_encoder_model"] is None
                assert provenance["resumen_prompt_version"]
                rows = baseline["results"]
                assert [q["question"] for q in rows] == questions
                assert sum(bool(q["hit_any_at_k"]) for q in rows) == 47
                assert sum(not q["hit_any_at_k"] for q in rows) == 8
                trace = [json.loads(line) for line in (OUT / (prefix + ".trace.jsonl")).read_text().splitlines()]
                assert [q["question"] for q in trace] == questions
                for t in trace:
                    assert 0 <= t["elapsed_ms"] < 120000 and math.isfinite(t["elapsed_ms"])
                    assert 0 < len(t["hits"]) <= 10
                    assert all(h["score_scale"] == ("CosineSimilarity" if mode == "dense" else "RankFusionNative")
                               for h in t["hits"])
                baselines[prefix], traces[prefix] = rows, trace
    assert len(index_hashes) == 1, "Corpus/chunk identity changed across arms or replicas"
    # All fields except the experimental window and collection must be identical.
    reference = load("a-1.dense.baseline.json")["provenance"].copy()
    reference.pop("embedding_max_sequence_length")
    for key in baselines:
        p = load(key + ".baseline.json")["provenance"].copy()
        p.pop("embedding_max_sequence_length")
        assert p == reference, f"Unexpected provenance drift: {key}"
    aggregates = {}
    for key, rows in baselines.items():
        aggregates[key] = {
            "hits_at_10": sum(hit(q) for q in rows),
            "full_hits_at_10": sum(hit(q, "hit_full_at_k") for q in rows),
            "symbol_hits_at_10": sum(hit(q) for q in rows if q["category"] == "simbolo"),
            "latency": latency(traces[key]),
            "by_category": {cat: {"n": sum(q["category"] == cat for q in rows),
                "hits_at_10": sum(hit(q) for q in rows if q["category"] == cat)}
                for cat in sorted({q["category"] for q in rows})}}
    comparisons = {}
    decisions = {}
    for a in arms[1:]:
        passes = []
        for r in [1, 2]:
            stats = {}
            for mode in ["dense", "fusion"]:
                baseline_key, candidate_key = f"a-{r}.{mode}", f"{a}-{r}.{mode}"
                control, candidate = baselines[baseline_key], baselines[candidate_key]
                rows = []
                for i, (b, c) in enumerate(zip(control, candidate)):
                    rows.append({
                        "question_number": i+1, "question": b["question"], "category": b["category"],
                        "answerable": bool(b["hit_any_at_k"]),
                        "a_hit": hit(b), "candidate_hit": hit(c),
                        "gain": not hit(b) and hit(c), "loss": hit(b) and not hit(c),
                        "a_full_hit": hit(b, "hit_full_at_k"), "candidate_full_hit": hit(c, "hit_full_at_k"),
                        "a_anchor_stratum": strata(anchor_sets[f"a-{r}"][i]["chunks"]),
                        "candidate_anchor_stratum": strata(anchor_sets[f"{a}-{r}"][i]["chunks"]),
                        "a_latency_ms": traces[baseline_key][i]["elapsed_ms"],
                        "candidate_latency_ms": traces[candidate_key][i]["elapsed_ms"],
                        "a_top_score": b["top_score"], "candidate_top_score": c["top_score"]})
                gains, losses = sum(q["gain"] for q in rows), sum(q["loss"] for q in rows)
                symbol_losses = sum(q["loss"] for q in rows if q["category"] == "simbolo")
                stats[mode] = {"gains": gains, "losses": losses, "net": gains-losses,
                    "symbol_losses": symbol_losses, "by_question": rows,
                    "by_a_truncation_stratum": {s: {
                        "n": sum(q["a_anchor_stratum"] == s and q["answerable"] for q in rows),
                        "gains": sum(q["gain"] for q in rows if q["a_anchor_stratum"] == s),
                        "losses": sum(q["loss"] for q in rows if q["a_anchor_stratum"] == s)}
                        for s in ["none_truncated", "some_truncated", "all_truncated"]}}
            passed = (stats["dense"]["net"] >= 2 and stats["fusion"]["losses"] <= 1
                      and stats["fusion"]["symbol_losses"] <= 1)
            passes.append(passed)
            comparisons[f"{a}-{r}"] = {"passes_literal_gate": passed, **stats}
        decisions[a] = {"replica_gates": passes, "recommend_11_3": all(passes)}
    report = {
        "item": protocol["item"], "n": 55, "answerable": 47, "negatives": 8,
        "c_viable": "c" in arms, "protocol_sha256": sha((OUT / "protocolo.json").read_bytes()),
        "ingestion": ingestion, "aggregates": aggregates, "comparisons": comparisons,
        "replica_hit_disagreements": {f"{a}.{mode}": [
            q1["question"] for q1, q2 in zip(baselines[f"{a}-1.{mode}"], baselines[f"{a}-2.{mode}"])
            if hit(q1) != hit(q2)] for a in arms for mode in ["dense", "fusion"]},
        "decision": decisions,
        "recommend_11_3": any(d["recommend_11_3"] for d in decisions.values()),
        "activated_11_3": False, "served_collections_unchanged": True,
        "limitations": [
            "Two-band native RRF only, no summaries/rerank; not the historical three-band baseline.",
            "B is a measured encoder-budget/window intervention at 128, not token-aware chunk resegmentation.",
            "Gross fusion/symbol losses used conservatively; no cancellation of individual losses by gains.",
            "Anchor-stratum means chunk truncated, not proof that this anchor was outside the embedded prefix.",
            "Negatives have no calibrated rejection oracle; their scores and latency are diagnostic only.",
            "Local quantized ARM64 ONNX model; not a result for other exports/models/hardware.",
            "HNSW approximate retrieval uses independent fresh index replicas, not exact search.",
            "Dirty experimental source/evidence recorded; production source fingerprints unchanged; no commit by executor."
        ]
    }
    write(OUT / "report.json", report)
    print(json.dumps({"evidence_valid": True, "aggregates": aggregates, "decision": decisions,
                      "recommend_11_3": report["recommend_11_3"],
                      "served_collections_unchanged": True}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    validate()
