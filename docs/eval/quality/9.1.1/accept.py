#!/usr/bin/env python3
"""Strict acceptance for 9.1.1; archived evidence is not a substitute for live contracts."""
import argparse
import json
import math
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
sys.path.insert(0, str(HERE.parent / "9.1"))
import verify as historical

digest = historical.digest
tree_digest = historical.tree_digest
require = historical.require
write_json = historical.write_json
SETS = historical.SETS
BASES = {"A": "bb9b11fb4477f7a34fe4452bd7e242c26d334ada",
         "B": "9690c49afbde5806e4859636623247234639efc8"}
CONTRACTS = {**historical.CONTRACTS, "DeterministicRankingTests": 9,
             "SymbolExpansionMergeTests": 4, "RetrievalScoreContractTests": 9,
             "CrossEncoderStableGateScoreTests": 1}
UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")


def validate_run(items, run, collection, dataset_hash):
    historical.compare(items, run, run, collection, dataset_hash)
    for row in run["results"]:
        require(row["retrieval_succeeded"] is True, "Retrieval failure absorbed")
        require(math.isfinite(row["elapsed_ms"]) and row["elapsed_ms"] > 0, "Missing latency")
        require(type(row["vector_queries"]) is int and row["vector_queries"] >= 2, "Missing query counters")
        require(type(row["candidates_returned"]) is int and row["candidates_returned"] >= len(row["hits"]),
                "Missing candidate counters")
        ids = [hit["chunk_id"] for hit in row["hits"]]
        require(len(set(ids)) == len(ids) and all(UUID.fullmatch(id_) for id_ in ids),
                "Invalid/duplicate/noncanonical chunk IDs")
        for hit in row["hits"]:
            require(math.isfinite(hit["ranking_score"]), "Missing/nonfinite ranking score")
            require(hit["ranking_score_scale"] in {"RankFusionNative", "RankFusionWeighted"},
                    "Unexpected ranking path in frozen eval")


def compare_replicas(items, runs, collection, dataset_hash):
    require(len(runs) == 3, "Exactly three independent replicas required")
    for run in runs:
        validate_run(items, run, collection, dataset_hash)
    for index, run in enumerate(runs[1:], 2):
        historical.compare(items, runs[0], run, collection, dataset_hash)
        for left, right in zip(runs[0]["results"], run["results"]):
            require(left["hits"] == right["hits"] and left["top_score"] == right["top_score"],
                    f"Replica {index}: IDs/order/scores differ: {left['question']}")


def compare_arms(items, left, right, collection, dataset_hash):
    validate_run(items, left, collection, dataset_hash)
    validate_run(items, right, collection, dataset_hash)
    return historical.compare(items, left, right, collection, dataset_hash)


def validate_coverage(protocol, capture):
    require(set(protocol["sets"]) == set(capture["sets"]) == set(SETS), "Missing set")
    require(set(protocol["arms"]) == set(BASES), "Missing arm")
    for name, evidence in capture["sets"].items():
        require(len(evidence["index_checks"]) == 7 and
                all(snapshot == protocol["sets"][name]["before"] for snapshot in evidence["index_checks"]),
                "Index changed/missing per-replica fingerprints")
        for arm in BASES:
            require(set(evidence[arm]) == {"1", "2", "3"}, "Missing/extra replica")


def run_contracts(directory):
    selector = "|".join(f"FullyQualifiedName~{name}" for name in CONTRACTS)
    with tempfile.TemporaryDirectory(prefix="rag-9.1.1-contracts-") as temp:
        result = subprocess.run([
            "dotnet", "test", "tests/RagEngine.Core.Tests", "--no-restore", "--nologo",
            "--filter", selector, "--logger", "trx;LogFileName=acceptance.trx",
            "--results-directory", temp, "--", "RunConfiguration.CollectSourceInformation=false"
        ], cwd=ROOT, capture_output=True, text=True)
        require(result.returncode == 0, result.stdout + result.stderr)
        root = ET.parse(Path(temp) / "acceptance.trx").getroot()
        rows = root.findall(".//{*}UnitTestResult")
        require(rows and all(r.attrib["outcome"] == "Passed" for r in rows), "Missing/skipped/failed tests")
        counts = {name: sum(f".{name}." in r.attrib["testName"] for r in rows) for name in CONTRACTS}
        for name, minimum in CONTRACTS.items():
            require(counts[name] >= minimum, f"Missing contract coverage: {name}: {counts[name]} < {minimum}")
        (directory / "contracts").mkdir(exist_ok=True)
        (directory / "contracts/final.trx").write_bytes((Path(temp) / "acceptance.trx").read_bytes())
        return {"total": len(rows), "classes": counts}


def historical_deltas(name, runs):
    result = {}
    for old_dir in (HERE.parent / "9.1", HERE.parent / "9.1/replica"):
        for old_arm, new_arm in (("control", "A"), ("candidate", "B")):
            old = json.loads((old_dir / f"{name}.{old_arm}.json").read_text())
            changes = []
            for left, right in zip(old["results"], runs[new_arm][0]["results"]):
                require(left["question"] == right["question"], "Historical question mismatch")
                delta = {}
                for field in ("hit_any_at_k", "hit_full_at_k"):
                    for k, value in left[field].items():
                        if value != right[field][k]:
                            delta[f"{field}@{k}"] = {"before": value, "after": right[field][k]}
                if delta:
                    changes.append({"question": left["question"], "category": left["category"], "changes": delta})
            result[f"{old_dir.name}/{old_arm}"] = changes
    return result


def verify(directory=HERE):
    protocol = json.loads((directory / "protocol.json").read_text())
    capture = json.loads((directory / "capture.json").read_text())
    validate_coverage(protocol, capture)
    require(protocol["item"] == "9.1.1-desempate-determinista", "Wrong item")
    require(capture["protocol_sha256"] == digest(directory / "protocol.json"), "Protocol changed")
    require(set(protocol["sets"]) == set(capture["sets"]) == set(SETS), "Missing set")
    require(protocol["parameters"] == {"top_k": 10, "min_score": 0.1, "rerank": False,
                                     "two_hop": False, "replicas": 3}, "Protocol parameters changed")
    require(protocol["test_sha256"] == tree_digest(ROOT, ["tests/RagEngine.Core.Tests"]), "Tests changed")
    require(protocol["instrument_sha256"] == instrument_digest(), "Instrument changed")
    require(set(protocol["arms"]) == set(BASES), "Missing arm")
    for arm, base in BASES.items():
        spec = protocol["arms"][arm]
        require(spec["base_commit"] == base, "Wrong base")
        require(digest(directory / spec["patch"]) == spec["patch_sha256"], "Patch changed")
        require(spec["source_sha256"] == capture["arms_after"][arm], "Measured source changed")
    require(protocol["arms"]["B"]["source_sha256"] == tree_digest(ROOT, ["src", "config"]),
            "Current product differs from measured B")
    require(protocol["arms"]["A"]["policy"] == protocol["arms"]["B"]["policy"], "Policy differs across arms")
    for model in protocol["models"].values():
        require(digest(model["path"]) == model["sha256"], "Model changed")
    require(capture["models_after"] == protocol["models"], "Model changed during measurement")
    summaries = {}
    for name, collection in SETS.items():
        spec = protocol["sets"][name]
        evidence = capture["sets"][name]
        require(spec["collection"] == collection, "Wrong collection")
        dataset = ROOT / f"docs/eval/{name}.eval-set.json"
        require(digest(dataset) == spec["dataset_sha256"], "Dataset changed")
        items = json.loads(dataset.read_text())
        require(len(items) == spec["n"], "Wrong cohort")
        require(len(evidence["index_checks"]) == 7 and
                all(snapshot == spec["before"] for snapshot in evidence["index_checks"]),
                "Index changed/missing per-replica fingerprints")
        runs = {}
        for arm in BASES:
            require(set(evidence[arm]) == {"1", "2", "3"}, "Missing/extra replica")
            runs[arm] = []
            for replica in range(1, 4):
                path = directory / f"{name}.{arm}{replica}.json"
                require(digest(path) == evidence[arm][str(replica)], "Capture changed")
                run = json.loads(path.read_text())
                provenance = run["provenance"]
                require(BASES[arm].startswith(provenance["git_commit"]), "Wrong capture base")
                require(provenance["git_dirty"] is True, "Expected base + frozen full diff")
                runs[arm].append(run)
            compare_replicas(items, runs[arm], collection, spec["dataset_sha256"])
        summary = compare_arms(items, runs["A"][0], runs["B"][0], collection, spec["dataset_sha256"])
        summary["historical_deltas"] = historical_deltas(name, runs)
        summary["cost"] = {}
        for arm, replicas in runs.items():
            rows = [r for run in replicas for r in run["results"]]
            summary["cost"][arm] = {
                "queries": sum(r["vector_queries"] for r in rows),
                "candidates_returned": sum(r["candidates_returned"] for r in rows),
                "elapsed_ms": sum(r["elapsed_ms"] for r in rows),
                "max_question_ms": max(r["elapsed_ms"] for r in rows)
            }
        summaries[name] = summary
    return summaries


def instrument_digest():
    import hashlib
    return hashlib.sha256("".join(digest(HERE / name) for name in
        ("accept.py", "measure.py", "test_accept.py")).encode()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-tests", action="store_true")
    args = parser.parse_args()
    summaries = verify()
    contracts = run_contracts(HERE) if args.run_tests else None
    if args.run_tests:
        write_json(HERE / "verification.json", {
            "passed": True, "protocol_sha256": digest(HERE / "protocol.json"),
            "sets": summaries, "contracts": contracts
        })
    print(f"PASS 9.1.1: 4 sets, A/A + B/B + A/B, 3 replicas/arm; contracts={contracts}")


if __name__ == "__main__":
    main()
