#!/usr/bin/env python3
"""Freeze base+diff arms, then capture every replica without touching served indexes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time

from accept import (BASES, HERE, ROOT, SETS, digest, tree_digest, require, write_json,
                    instrument_digest)
from capture import fingerprint


def policy(root):
    paths = [
        "src/RagEngine.Core/Diagnostics/RankingDiagnostics.cs",
        "src/RagEngine.Core/Domain/RankingOrder.cs",
        "src/RagEngine.Core/Domain/RetrievalScoreScale.cs",
        "src/RagEngine.Core/Abstractions/IReRanker.cs",
        "src/RagEngine.Core/Infrastructure/Reranking/OnnxCrossEncoderReRanker.cs",
        "src/RagEngine.Core/Infrastructure/VectorStore/DeterministicVectorQuery.cs",
    ]
    result = {path: digest(root / path) for path in paths}
    retriever = (root / "src/RagEngine.Core/Infrastructure/VectorStore/QdrantSemanticRetriever.cs").read_text()
    for start, end in (("    private sealed record RankedPoint", "    private static Filter? BuildFilter"),
                       ("            if (!hasSummaryVector)", "            // 5. Map to domain entities")):
        text = retriever[retriever.index(start):retriever.index(end)]
        result[start.strip()] = hashlib.sha256(text.encode()).hexdigest()
    evaluator = (root / "src/RagEngine.Cli/Commands/EvalCommand.cs").read_text()
    evaluator = evaluator.replace("            Context = RetrievalContext.Local,\n", "")
    result["EvalCommand (only Context line differs)"] = hashlib.sha256(evaluator.encode()).hexdigest()
    model = (root / "src/RagEngine.Core/Domain/RetrievalResult.cs").read_text().split(
        "public sealed record RetrievalOptions")[0]
    result["RetrievalResult model"] = hashlib.sha256(model.encode()).hexdigest()
    return result


def execute(command, root, env, log):
    started = time.monotonic()
    result = subprocess.run(command, cwd=root, env=env, capture_output=True, text=True, timeout=600)
    log.write_text(result.stdout + result.stderr)
    require(result.returncode == 0, f"Command failed: {log}\n{result.stdout[-3000:]}{result.stderr[-3000:]}")
    return result, time.monotonic() - started


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arm-a", type=Path, required=True)
    parser.add_argument("--arm-b", type=Path, required=True)
    args = parser.parse_args()
    arms = {"A": args.arm_a.resolve(), "B": args.arm_b.resolve()}
    require(not (HERE / "protocol.json").exists(), "Never overwrite a frozen/partial experiment")
    model_root = Path(os.environ.get("RAG_MODELS_DIR", "~/models")).expanduser().resolve()
    config = json.loads((ROOT / "src/RagEngine.Cli/appsettings.json").read_text())
    models = {}
    for section in ("OnnxBrain", "CrossEncoder"):
        for key in ("ModelPath", "VocabPath"):
            path = Path(config[section][key].replace("${RAG_MODELS_DIR}", str(model_root)))
            models[f"{section}.{key}"] = {"path": str(path), "sha256": digest(path)}
    historical_protocol = json.loads((HERE.parent / "9.1/protocol.json").read_text())
    require(models == historical_protocol["models"], "Models differ from frozen cohort")
    protocol = {
        "item": "9.1.1-desempate-determinista", "arms": {}, "models": models,
        "parameters": {"top_k": 10, "min_score": .1, "rerank": False, "two_hop": False, "replicas": 3},
        "test_sha256": tree_digest(ROOT, ["tests/RagEngine.Core.Tests"]),
        "instrument_sha256": instrument_digest(), "sets": {},
        "policy": "exact score descending, canonical lowercase UUID D ordinal ascending before each cutoff; "
                  "exact prefix doubling from limit+1, fail at unresolved 32768; native f32 RRF k=2/zero-based",
        "adaptations": "A retains concrete store and pre-9.1 unfiltered mapping. No Context/module/ports backport. "
                       "B retains authorization guards. Shared ranking helpers, formulas, reranker and eval instrument "
                       "are byte-identical; only the explicit local Context line differs in EvalCommand.",
    }
    for arm, root in arms.items():
        require(subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip() == BASES[arm],
                "Wrong base worktree")
        new_files = subprocess.check_output(
            ["git", "ls-files", "--others", "--exclude-standard", "--", "src"], cwd=root, text=True).splitlines()
        if new_files:
            subprocess.run(["git", "add", "-N", "--", *new_files], cwd=root, check=True)
        patch = subprocess.check_output(["git", "diff", "--binary", "HEAD", "--", "src"], cwd=root)
        patch_name = "control.patch" if arm == "A" else "candidate.patch"
        (HERE / patch_name).write_bytes(patch)
        require(tree_digest(root, ["config"]) == tree_digest(ROOT, ["config"]), "Shared config differs")
        require(digest(root / "src/RagEngine.Cli/appsettings.json") ==
                digest(ROOT / "src/RagEngine.Cli/appsettings.json"), "CLI config differs")
        protocol["arms"][arm] = {
            "base_commit": BASES[arm], "patch": patch_name, "patch_sha256": digest(HERE / patch_name),
            "source_sha256": tree_digest(root, ["src", "config"]), "policy": policy(root)
        }
    require(protocol["arms"]["A"]["policy"] == protocol["arms"]["B"]["policy"], "Ranking policy not identical")
    require(protocol["arms"]["B"]["source_sha256"] == tree_digest(ROOT, ["src", "config"]), "B != current product")
    for name, collection in SETS.items():
        dataset = ROOT / f"docs/eval/{name}.eval-set.json"
        require(digest(dataset) == historical_protocol["sets"][name]["dataset_sha256"], "Dataset changed")
        print(f"Freezing {name} (read only)...", flush=True)
        before = fingerprint(collection)
        require(before == historical_protocol["sets"][name]["before"], "Index changed since historical controls")
        protocol["sets"][name] = {
            "collection": collection, "dataset_sha256": digest(dataset),
            "n": len(json.loads(dataset.read_text())), "before": before
        }
    write_json(HERE / "protocol.json", protocol)
    capture = {"protocol_sha256": digest(HERE / "protocol.json"), "sets": {
        name: {"A": {}, "B": {}, "index_checks": [spec["before"]]} for name, spec in protocol["sets"].items()
    }}
    start = time.monotonic()
    with tempfile.TemporaryDirectory(prefix="rag-9.1.1-eval-") as temp:
        temp = Path(temp)
        env = {k: v for k, v in os.environ.items() if k in {
            "PATH", "HOME", "TMPDIR", "DOTNET_ROOT", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "LANG"}}
        env.update({"RAG_MODELS_DIR": str(model_root), "DOTNET_ENVIRONMENT": "Production",
                    "Ingestion__ResumenCachePath": str(temp / "cache.sqlite3"),
                    "RAG_LOGS_DIR": str(temp / "logs"), "TwoHop__Enabled": "false"})
        for arm, root in arms.items():
            build = subprocess.run(["dotnet", "build", "src/RagEngine.Cli", "--no-restore", "--nologo", "-warnaserror"],
                                   cwd=root, env=env, capture_output=True, text=True)
            if build.returncode:
                require("NETSDK1004" in build.stdout, "Build failure other than missing assets:\n" + build.stdout)
                execute(["dotnet", "restore", "src/RagEngine.Cli", "--nologo"], root, env, temp / f"{arm}.restore.log")
            execute(["dotnet", "build", "src/RagEngine.Cli", "--no-restore", "--nologo", "-warnaserror"],
                    root, env, temp / f"{arm}.build.log")
        for replica in range(1, 4):
            for arm, root in arms.items():
                for name, collection in SETS.items():
                    print(f"Measuring {arm}{replica}/{name}...", flush=True)
                    result, seconds = execute([
                        "dotnet", "src/RagEngine.Cli/bin/Debug/net10.0/RagEngine.Cli.dll",
                        "eval", "--eval-set", f"docs/eval/{name}.eval-set.json", "--collection", collection,
                        "--top-k", "10", "--min-score", ".1", "--dump-hits", "--json"
                    ], root, env, temp / f"{name}.{arm}{replica}.log")
                    require("Falló la búsqueda" not in result.stderr, "Absorbed retrieval error")
                    path = HERE / f"{name}.{arm}{replica}.json"
                    write_json(path, json.loads(result.stdout))
                    capture["sets"][name][arm][str(replica)] = digest(path)
                    capture["sets"][name]["index_checks"].append(fingerprint(collection))
                    print(f"  {seconds:.1f}s", flush=True)
        capture["arms_after"] = {arm: tree_digest(root, ["src", "config"]) for arm, root in arms.items()}
        capture["models_after"] = {
            key: {"path": spec["path"], "sha256": digest(spec["path"])} for key, spec in models.items()}
        capture["elapsed_seconds_including_build_and_fingerprints"] = time.monotonic() - start
        write_json(HERE / "capture.json", capture)
    print("All 24 captures preserved. Run accept.py separately.", flush=True)


if __name__ == "__main__":
    main()
