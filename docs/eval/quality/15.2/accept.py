#!/usr/bin/env python3
"""Strict oracle verification / paired comparator. Exit 2 means incomplete evidence."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import gzip
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess
import sys
import time
import unittest

from capture import digest
from prepare import sha, source_evidence, write

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
ARTIFACTS = (
    "protocol.json", "review.json", "graph-fixtures.json", "opportunities.json",
    "cohort.json", "source-evidence.json", "preparation.json", "index-manifest.json.gz",
    "corpus-manifest.json.gz", "capture.py", "prepare.py", "accept.py", "test_accept.py",
)
INPUTS = ("docs/eval/bsuite-repo.eval-set.json",
          "docs/eval/baselines/bsuite-repo.6d-symbol-join-audit.json")
ARMS = ("no-expansion", "name-join", "syntax", "semantic", "graph")
ALTERNATIVES = ARMS[2:]
SHA256 = re.compile(r"[0-9a-f]{64}")


class Incomplete(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise Incomplete(message)


def read(path):
    data = path.read_bytes()
    return json.loads(gzip.decompress(data) if path.suffix == ".gz" else data)


def finite(value, positive=False):
    return (type(value) in (int, float) and math.isfinite(value)
            and (value > 0 if positive else value >= 0))


def hash_value(value):
    return isinstance(value, str) and SHA256.fullmatch(value) is not None


def load_bundle(directory=HERE, sealed=True):
    if sealed:
        freeze = read(directory / "freeze.json")
        require(set(freeze["artifacts"]) == set(ARTIFACTS), "Frozen artifact coverage changed")
        require(set(freeze["inputs"]) == set(INPUTS), "Frozen input coverage changed")
        for name, expected in freeze["artifacts"].items():
            require(sha(directory / name) == expected, f"Frozen artifact changed: {name}")
        for name, expected in freeze["inputs"].items():
            require(sha(ROOT / name) == expected, f"Historical input changed: {name}")
    bundle = {name: read(directory / f"{name}.json") for name in
              ("protocol", "review", "graph-fixtures", "opportunities",
               "cohort", "source-evidence", "preparation")}
    bundle["index"] = read(directory / "index-manifest.json.gz")
    bundle["corpus"] = read(directory / "corpus-manifest.json.gz")
    bundle["freeze_sha256"] = sha(directory / "freeze.json") if sealed else "0" * 64
    validate_bundle(bundle)
    return bundle


def validate_bundle(b):
    p, opportunities, cohort = b["protocol"], b["opportunities"], b["cohort"]
    require(p["thresholds"] == {"correct_destinations": 27, "denominator": 30,
                               "jump_gain": 5, "outside_losses": 0, "p95_delta_ms": 150},
            "Thresholds altered")
    require(p["arms"] == list(ARMS) and p["alternatives"] == list(ALTERNATIVES), "Arms altered")
    require(p["latency"]["replicas"] == 5 and p["latency"]["warmup_passes"] == 1,
            "Latency method altered")
    dataset = read(ROOT / INPUTS[0])
    require([q["item"] for q in cohort] == dataset and len(cohort) == 84, "Cohort/anchors altered")
    require([q["id"] for q in cohort] == [f"q{i:02d}" for i in range(84)], "Query IDs altered")
    categories = Counter(q["item"]["Category"] for q in cohort)
    require(categories == {"literal": 12, "parafraseada": 18, "simbolo": 12, "ambigua": 5,
                           "salto": 20, "salto-negativo": 9, "fuera-de-dominio": 8},
            "Missing controls/negatives")
    audit = read(ROOT / INPUTS[1])
    require(len(audit["results"]) == 30 and
            Counter(r["label"] for r in audit["results"]) == {"correcto": 6, "colision": 24},
            "Historical labels changed")
    index = {r["id"]: r for r in b["index"]}
    require(len(index) == len(b["index"]) == b["preparation"]["points"] and
            digest(b["index"]) == b["preparation"]["index_manifest_digest"], "Index IDs/hash changed")
    for row in index.values():
        require(all(hash_value(row[k]) for k in ("content_hash", "payload_sha256", "vector_sha256")),
                "Missing index hashes")
    for q in cohort:
        require(type(q["anchored"]) is bool, "Invalid anchor flag")
        require(len(set(q["anchor_ids"])) == len(q["anchor_ids"]) and
                set(q["anchor_ids"]) <= index.keys(), "Invalid anchor IDs")
        require(bool(q["anchor_ids"]) == q["anchored"], "Missing anchored evidence")
    require(len(opportunities) == len({o["id"] for o in opportunities}) == 30,
            "Exactly 30 unique opportunities required")
    jump_ids = [q["id"] for q in cohort if q["item"]["Category"] == "salto"]
    require([o["query_id"] for o in opportunities[:20]] == jump_ids, "Primary selection changed")
    require([o["query_id"] for o in opportunities[20:]] ==
            [jump_ids[r["query"]] for r in b["review"]["secondary"]], "Secondary selection changed")
    chunks = b["source-evidence"]["chunks"]
    known_targets = {}
    for o in opportunities:
        q = next(q for q in cohort if q["id"] == o["query_id"])
        require(o["question"] == q["item"]["Question"] and o["reason"], "Question/review changed")
        require(o["id"] == f'{o["query_id"]}-{o["stage"]}' and
                o["stage"] in ("entry", "internal"), "Opportunity ID changed")
        require(len(o["seed_ids"]) == 1 and o["valid_target_ids"], "Missing seed/destination")
        require(len(set(o["valid_target_ids"])) == len(o["valid_target_ids"]), "Duplicate destinations")
        require(not set(o["seed_ids"]) & set(o["valid_target_ids"]), "Self expansion in oracle")
        require(o["seed_version"] == b["preparation"]["index_sha256"], "Seed version changed")
        used = known_targets.setdefault(o["query_id"], set())
        require(not used.intersection(o["valid_target_ids"]), "Reusable oracle target within a query")
        used.update(o["valid_target_ids"])
        for id_ in o["seed_ids"] + o["valid_target_ids"]:
            require(id_ in chunks and id_ in index, "Missing source evidence")
            chunk = chunks[id_]
            require(chunk["id"] == id_ and digest(chunk["payload"]) == index[id_]["payload_sha256"]
                    and chunk["vector_sha256"] == index[id_]["vector_sha256"], "Chunk changed")
            require(hashlib.sha256(chunk["payload"]["content"].encode()).hexdigest() ==
                    chunk["indexed_content_sha256"], "Content changed")
            evidence = chunk["source_evidence"]
            require(b["corpus"]["files"][evidence["file"]] == evidence["file_sha256"] and
                    evidence["text"] and evidence["end_line"] >= evidence["start_line"] > 0,
                    "Source evidence changed")
        seed = chunks[o["seed_ids"][0]]["payload"]
        require(re.search(r"\b" + re.escape(o["method"]) + r"\s*\(", seed["content"]),
                "Seed lacks reviewed call")
        for id_ in o["valid_target_ids"]:
            target = chunks[id_]["payload"]
            require(target.get("method_name") == o["method"], "Wrong oracle definition")
    require(len(b["graph-fixtures"]["scenarios"]) == 2, "Missing graph fixtures")
    for scenario in b["graph-fixtures"]["scenarios"]:
        require(len(scenario["input"]) == len(scenario["expected"]) > 0, "Missing graph steps")
    return b


def schedule(b):
    seq = 0
    for phase, replicas in (("warmup", 1), ("measure", 5)):
        for replica in range(replicas):
            for qi, q in enumerate(b["cohort"]):
                shift = (replica + qi) % len(ARMS) if phase == "measure" else 0
                order = ARMS[shift:] + ARMS[:shift]
                for arm in order:
                    yield {"sequence": seq, "phase": phase, "replica": replica,
                           "query_id": q["id"], "question": q["item"]["Question"], "arm": arm}
                    seq += 1


def validate_ids(hits, index, limit):
    require(isinstance(hits, list) and len(hits) <= limit, "Invalid result size")
    ids = []
    for hit in hits:
        require(set(hit) == {"id", "content_hash"}, "Hit must identify actual indexed content")
        id_ = hit["id"]
        require(id_ in index and hit["content_hash"] == index[id_]["content_hash"],
                "Unknown ID/altered content hash")
        ids.append(id_)
    require(len(ids) == len(set(ids)), "Duplicate returned IDs")
    return ids


def precision(b, rows):
    require(isinstance(rows, list) and len(rows) == 30, "Missing precision opportunities")
    index = {p["id"]: p for p in b["index"]}
    correct, errors, abstentions, reused, used = [], [], [], [], {}
    elapsed = 0
    for o, row in zip(b["opportunities"], rows):
        require(all(row[k] == o[k] for k in
                    ("query_id", "question", "seed_ids", "seed_version")), "Wrong precision provenance")
        require(row["opportunity_id"] == o["id"], "Missing/reordered opportunity")
        require(row["succeeded"] is True, "Precision execution failed")
        require(finite(row["elapsed_ms"], True), "Missing precision latency")
        elapsed += row["elapsed_ms"]
        ids = validate_ids(row["candidates"], index, 20)
        require(not set(ids) & set(o["seed_ids"]), "Seed returned as expansion")
        if not ids:
            abstentions.append(o["id"])
            continue
        prediction = ids[0]
        previous = used.setdefault(o["query_id"], set())
        if prediction in previous:
            reused.append(o["id"])
            errors.append(o["id"])
        elif prediction in o["valid_target_ids"]:
            correct.append(o["id"])
        else:
            errors.append(o["id"])
        previous.add(prediction)
    return {"correct": len(correct), "denominator": 30, "success_rate": len(correct) / 30,
            "returned_precision": len(correct) / (30 - len(abstentions)) if len(abstentions) < 30 else None,
            "correct_ids": correct, "error_ids": errors, "abstention_ids": abstentions,
            "reused_ids": reused, "elapsed_ms": elapsed}


def p95(values):
    require(bool(values) and all(finite(v, True) for v in values), "Missing/nonfinite latency")
    return sorted(values)[math.ceil(0.95 * len(values)) - 1]


def graph_contract(b, evidence):
    require(isinstance(evidence, dict), "Graph contract evidence missing")
    scenarios = b["graph-fixtures"]["scenarios"]
    require(set(evidence) == {s["id"] for s in scenarios}, "Missing graph scenarios")
    errors = []
    for s in scenarios:
        observations = evidence[s["id"]]
        require(isinstance(observations, list) and len(observations) == len(s["input"]),
                "Missing graph observations")
        for i, (actual, expected) in enumerate(zip(observations, s["expected"])):
            require(isinstance(actual, dict) and set(actual) == set(expected),
                    "Missing graph observation fields")
            if digest(actual) != digest(expected):
                errors.append(f"{s['id']}:{i}")
    return errors


def compare(b, experiment):
    require(experiment["kind"] == "measured_experiment", "Not measured experiment evidence")
    require(experiment["freeze_sha256"] == b["freeze_sha256"], "Wrong freeze hash")
    require(experiment["parameters"] == b["protocol"]["parameters"], "Common config changed")
    require(hash_value(experiment["runner_sha256"]), "Missing runner identity")
    require(re.fullmatch(r"[0-9a-f]{40}", experiment["engine_commit"]) is not None,
            "Missing engine revision")
    require(isinstance(experiment["command"], list) and experiment["command"] and
            all(isinstance(x, str) and x for x in experiment["command"]), "Missing executable command")
    require(isinstance(experiment["runtime"], str) and experiment["runtime"] and
            isinstance(experiment["hardware"], str) and experiment["hardware"], "Missing runtime/hardware")
    arms = experiment["arms"]
    require(set(arms) == set(ARMS), "A control/alternative was omitted")
    expected_index = b["preparation"]["index_sha256"]
    expected_corpus = b["preparation"]["corpus_manifest_sha256"]
    summaries = {}
    for arm in ARMS:
        spec = arms[arm]
        require(hash_value(spec["implementation_sha256"]), "Missing implementation hash")
        require(spec["index_before"] == spec["index_after"] == expected_index, "Index drift")
        require(spec["corpus_before"] == spec["corpus_after"] == expected_corpus, "Corpus drift")
        require(spec["collection_config_before"] == spec["collection_config_after"] ==
                digest(b["preparation"]["collection_config"]), "Collection config drift")
        require(spec["models_before"] == spec["models_after"] == b["preparation"]["models"],
                "Model/tokenizer drift")
        require(all(finite(spec[k]) for k in ("build_seconds", "update_seconds", "machine_seconds")),
                "Missing construction/update/total cost")
        summary = {"build_seconds": spec["build_seconds"], "update_seconds": spec["update_seconds"],
                   "machine_seconds": spec["machine_seconds"]}
        if arm == "no-expansion":
            require(spec["precision"] == [], "No-expansion is not the precision control")
        else:
            summary["precision"] = precision(b, spec["precision"])
        summaries[arm] = summary
    calls = experiment["runs"]
    expected_calls = list(schedule(b))
    require(isinstance(calls, list) and len(calls) == len(expected_calls), "Incomplete latency/recall coverage")
    index = {p["id"]: p for p in b["index"]}
    hits, latency, all_ms = {}, {a: [] for a in ARMS}, {a: 0 for a in ARMS}
    for call, expected in zip(calls, expected_calls):
        require(all(call[k] == v for k, v in expected.items()), "Execution order/IDs/questions altered")
        require(call["succeeded"] is True, "Retrieval failure")
        require(finite(call["elapsed_ms"], True), "Missing/nonfinite latency")
        ids = validate_ids(call["hits"], index, 10)
        arm = call["arm"]
        all_ms[arm] += call["elapsed_ms"]
        if call["phase"] == "measure":
            key = (arm, call["query_id"])
            require(key not in hits or hits[key] == ids, "Replica result drift")
            hits[key] = ids
            latency[arm].append(call["elapsed_ms"])
    for arm in ARMS:
        s = summaries[arm]
        s["p95_ms"] = p95(latency[arm])
        require(len(latency[arm]) == 420, "Wrong percentile sample size")
        s["search_seconds"] = all_ms[arm] / 1000
        minimum_cost = (s["build_seconds"] + s["update_seconds"] + s["search_seconds"]
                        + s.get("precision", {}).get("elapsed_ms", 0) / 1000)
        require(s["machine_seconds"] >= minimum_cost, "Machine cost undercounts executed work")
        s["jump_hit_ids"], s["outside_loss_ids"], s["jump_loss_ids"], s["jump_gain_ids"] = [], [], [], []
        s["hit_any_at_10"] = {}
        s["unanchored_new_ids"] = {}
        for q in b["cohort"]:
            baseline_ids = hits[("no-expansion", q["id"])]
            actual_ids = hits[(arm, q["id"])]
            before, after = bool(set(baseline_ids) & set(q["anchor_ids"])), bool(set(actual_ids) & set(q["anchor_ids"]))
            s["hit_any_at_10"][q["id"]] = after if q["anchored"] else None
            if q["item"]["Category"] == "salto":
                if after:
                    s["jump_hit_ids"].append(q["id"])
                if before and not after:
                    s["jump_loss_ids"].append(q["id"])
                if after and not before:
                    s["jump_gain_ids"].append(q["id"])
            elif q["anchored"] and before and not after:
                s["outside_loss_ids"].append(q["id"])
            elif not q["anchored"]:
                new_ids = sorted(set(actual_ids) - set(baseline_ids))
                if new_ids:
                    s["unanchored_new_ids"][q["id"]] = new_ids
        s["jump_net_gain"] = len(s["jump_gain_ids"]) - len(s["jump_loss_ids"])
        s["p95_delta_ms"] = s["p95_ms"] - summaries["no-expansion"]["p95_ms"]
    graph_execution = experiment.get("graph_execution")
    graph_errors = None
    if graph_execution is not None:
        require(isinstance(graph_execution, dict), "Invalid graph execution record")
        require(graph_execution["implementation_sha256"] == arms["graph"]["implementation_sha256"]
                and graph_execution["runner_sha256"] == experiment["runner_sha256"],
                "Graph contract implementation differs from measured arm")
        require(finite(graph_execution["seconds"], True) and
                isinstance(graph_execution["command"], list) and graph_execution["command"] and
                all(isinstance(x, str) and x for x in graph_execution["command"]),
                "Missing graph contract execution provenance")
        graph_errors = graph_contract(b, graph_execution["observations"])
    eligible = []
    for arm in ALTERNATIVES:
        s = summaries[arm]
        passed = (s["precision"]["correct"] >= 27 and s["jump_net_gain"] >= 5
                  and not s["outside_loss_ids"] and not s["unanchored_new_ids"]
                  and s["p95_delta_ms"] <= 150)
        s["graph_contract_errors"] = graph_errors if arm == "graph" else None
        if arm == "graph" and passed:
            require(graph_execution is not None, "Promotable graph requires complete contract evidence")
            s["graph_contract_errors"] = graph_errors
            passed = not s["graph_contract_errors"]
        s["eligible"] = passed
        if passed:
            eligible.append(arm)
    return {"status": "complete_with_eligible" if eligible else "complete_no_promotion",
            "eligible": eligible, "preferred": eligible[0] if eligible else None,
            "summaries": summaries,
            "machine_seconds": sum(s["machine_seconds"] for s in summaries.values())
            + (graph_execution["seconds"] if graph_execution else 0),
            "deployment_performed": False}


def verify_sources(b, corpus):
    require(corpus.is_dir(), "Required corpus is unavailable")
    actual_revision = subprocess.check_output(["git", "-C", str(corpus), "rev-parse", "HEAD"],
                                              text=True).strip()
    require(actual_revision == b["corpus"]["revision"], "Corpus revision changed")
    names = subprocess.check_output(["git", "-C", str(corpus), "ls-files", "-z"]).decode().split("\0")
    require(set(filter(None, names)) == set(b["corpus"]["files"]), "Corpus file coverage changed")
    for path, expected in b["corpus"]["files"].items():
        require(sha(corpus / path) == expected, f"Corpus file changed: {path}")
    for point in b["source-evidence"]["chunks"].values():
        require(source_evidence(corpus, point) == point["source_evidence"], "Source excerpt changed")
    for evidence in b["source-evidence"]["receiver_types"]:
        path = corpus / evidence["file"]
        require(sha(path) == evidence["file_sha256"] and
                path.read_text(encoding="utf-8-sig").splitlines()[evidence["line"] - 1] == evidence["text"],
                "Receiver evidence changed")


def verify_instrument(corpus):
    started = time.monotonic()
    b = load_bundle()
    verify_sources(b, corpus)
    suite = unittest.defaultTestLoader.discover(str(HERE), pattern="test_accept.py")
    def names(tests):
        for test in tests:
            if isinstance(test, unittest.TestSuite):
                yield from names(test)
            else:
                yield test.id()
    selected_tests = list(names(suite))
    require(suite.countTestCases() >= 30, "Insufficient selected adversarial tests")
    result = unittest.TextTestRunner(stream=sys.stderr, verbosity=1).run(suite)
    require(result.wasSuccessful() and not result.skipped and result.testsRun == len(selected_tests),
            "Failed/skipped/missing instrument tests")
    return {"status": "instrument_verified_not_experiment", "freeze_sha256": b["freeze_sha256"],
            "tests_run": result.testsRun, "selected_tests": selected_tests,
            "skipped": len(result.skipped), "opportunities": 30,
            "queries": 84, "jump_queries": 20, "jump_negatives": 9,
            "source_chunks": len(b["source-evidence"]["chunks"]),
            "corpus_files": len(b["corpus"]["files"]), "index_points": len(b["index"]),
            "acceptance_seconds": time.monotonic() - started,
            "arms_executed": [], "retrieval_gain_demonstrated": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    actions = parser.add_mutually_exclusive_group(required=True)
    actions.add_argument("--seal", action="store_true")
    actions.add_argument("--verify-instrument", action="store_true")
    actions.add_argument("--experiment", type=Path)
    parser.add_argument("--corpus", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        if args.seal:
            require(not (HERE / "freeze.json").exists(), "Already sealed; preserve the original freeze")
            load_bundle(sealed=False)
            freeze = {"schema_version": 1, "sealed_at": datetime.now(timezone.utc).isoformat(),
                      "artifacts": {name: sha(HERE / name) for name in ARTIFACTS},
                      "inputs": {name: sha(ROOT / name) for name in INPUTS}}
            write(HERE / "freeze.json", freeze)
            report = {"status": "sealed_not_verified", "freeze_sha256": sha(HERE / "freeze.json")}
        elif args.verify_instrument:
            require(args.corpus is not None, "--corpus is required, never skip source verification")
            report = verify_instrument(args.corpus)
        else:
            report = compare(load_bundle(), read(args.experiment))
        if args.output:
            require(args.experiment is None or args.output.resolve() != args.experiment.resolve(),
                    "Cannot overwrite the measured experiment")
            require(args.output.resolve() not in
                    {HERE / name for name in (*ARTIFACTS, "freeze.json")} |
                    {ROOT / name for name in INPUTS}, "Cannot overwrite frozen evidence")
            write(args.output, report)
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 0
    except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as error:
        print(json.dumps({"status": "incomplete", "error": str(error)}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
