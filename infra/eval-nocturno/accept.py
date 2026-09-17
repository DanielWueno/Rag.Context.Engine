#!/usr/bin/env python3
"""14.1 acceptance: fixtures AND all five real evals, no skipped dependencies."""
import argparse
import copy
import json
import subprocess
import sys
import time
import unittest
from pathlib import Path

import runner
from test_runner import set_hit

ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "docs/eval/quality/14.1/verification.json"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--record", action="store_true")
    args = parser.parse_args()
    runner.require(not args.record or not EVIDENCE.exists(), "Conservar evidencia previa; no sobrescribir")
    runner.require(args.record or EVIDENCE.exists(), "Falta evidencia persistente: ejecutar --record")
    suite = unittest.defaultTestLoader.discover(str(Path(__file__).parent), pattern="test_runner.py")
    tests = unittest.TextTestRunner(verbosity=1).run(suite)
    runner.require(tests.wasSuccessful() and tests.testsRun >= 18 and not tests.skipped,
                   "Faltan controles o hay tests fallidos/omitidos")
    output = ROOT / "logs/eval-nocturno" / f"accept-{time.time_ns()}"
    process = subprocess.run([sys.executable, str(Path(__file__).with_name("runner.py")),
                              "run", "--root", str(ROOT), "--output", str(output)],
                             cwd=ROOT, capture_output=True, text=True, timeout=1800)
    report = runner.load(output / "report.json")
    runner.require(process.returncode in (0, 1, 2), f"Infraestructura/runner fallo: {process.stderr}")
    runner.require(len(report["profiles"]) == 5 and report.get("collections_unchanged"),
                   "Falta inventario real completo o conservacion de indices")
    controls = []
    for profile, measured in zip(runner.inventory(ROOT), report["profiles"]):
        runner.require(profile["id"] == measured["id"] and measured.get("executed") is True,
                       "Un perfil no ejecuto rag eval")
        result_path = output / f"{profile['id']}.result.json"
        result = runner.load(result_path)
        data = (ROOT / profile["dataset"]).read_bytes()
        runner.require(runner.compare(data, result, result)["status"] == "ok", "Control real no comparable")
        hits = [i for i, r in enumerate(result["results"]) if r["hit_any_at_k"].get("10")]
        runner.require(len(hits) >= 2, f"Insuficientes aciertos para mutacion real: {profile['id']}")
        mutated = copy.deepcopy(result)
        for index in hits[:2]:
            set_hit(mutated, index, False)
        regression = runner.compare(data, result, mutated)
        runner.require(regression["status"] == "regression" and regression["net_lost"] == 2
                       and len(regression["lost"]) == 2, "Mutacion de dos perdidas no alarma")
        changed_data = json.loads(data)
        changed_data[0]["Note"] = "14.1 controlled dataset mutation"
        runner.require(runner.compare(runner.canonical(changed_data), result, mutated)["status"] == "incomparable",
                       "Dataset cambiado produce falsa regresion")
        changed_config = copy.deepcopy(result)
        changed_config["provenance"]["weight_sparse"] += 0.5
        runner.require(runner.compare(data, result, changed_config)["status"] == "incomparable",
                       "Config cambiada no alarma")
        controls.append({"id": profile["id"], "dataset_sha256": profile["dataset_sha256"],
                         "result_sha256": runner.file_hash(result_path), "historical_status": measured["status"],
                         "answerable": measured["answerable"], "negative": measured["negative"],
                         "historical_reason": measured.get("reason"), "two_losses": regression,
                         "provenance": result["provenance"]})
    files = ["runner.py", "test_runner.py", "accept.py", "inventory.json"]
    evidence = {
        "item": "14.1-eval-nocturno", "accepted": True,
        "tests_passed": tests.testsRun, "source": report["source"],
        "source_sha256": {name: runner.file_hash(Path(__file__).parent / name) for name in files},
        "elapsed_seconds": report["elapsed_seconds"], "model_sha256": report["model_sha256"],
        "binary_sha256": report["binary_sha256"], "collections": report["collections"],
        "collections_unchanged": True, "profiles": controls,
        "limits": "Historical baselines preserved, never promoted automatically. Missing provenance is an "
                  "incomparable alarm, not quality acceptance. Real outputs and logs stay local under "
                  "logs/eval-nocturno. Fixture/mutation tests validate alarms, not retrieval improvements. "
                  "No ingestion, generation or shared summary-cache writer. Schedule installed separately "
                  "after committing, pinned to that clean reviewed commit.",
    }
    if args.record:
        EVIDENCE.parent.mkdir(parents=True, exist_ok=True)
        runner.save(EVIDENCE, evidence)
    else:
        recorded = runner.load(EVIDENCE)
        runner.require(recorded.get("accepted") and recorded.get("source_sha256") == evidence["source_sha256"],
                       "Evidencia persistente no corresponde al gate actual")
    print(f"14.1 aceptado: {tests.testsRun} controles, cinco evals reales y mutaciones. "
          f"Status historico: {report['status']}. Evidencia: {EVIDENCE.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
