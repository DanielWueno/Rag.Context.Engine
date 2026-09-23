#!/usr/bin/env python3
"""Acceptance for 12.10: real local contracts plus adversarial paired-comparator fixtures."""
import argparse
import json
import os
import subprocess
import tempfile
import time
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

from gate_calibration import compare, sha256, validate_identity
from test_gate_calibration import LABELED, fixture

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "docs/eval/quality/12.10/verification.json"


def run_tests(project, selector, directory, name, environment, required_cases):
    command = ["dotnet", "test", project, "--no-restore", "--filter", selector,
               "-v", "quiet", "--logger", f"trx;LogFileName={name}.trx",
               "--results-directory", str(directory)]
    subprocess.run(command, cwd=ROOT, env=environment, check=True, timeout=300)
    tree = ET.parse(directory / f"{name}.trx")
    results = tree.findall(".//{*}UnitTestResult")
    if not results or any(row.get("outcome") != "Passed" for row in results):
        raise ValueError(f"{name}: zero tests, missing infrastructure, skipped or failed cases")
    cases = [row.get("testName", "") for row in results]
    for required in required_cases:
        if not any(required in case for case in cases):
            raise ValueError(f"{name}: missing required coverage: {required}")
    return {"command": command[:command.index("--results-directory")],
            "passed": len(results), "cases": cases}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--record", action="store_true", help="record first acceptance; refuses to overwrite evidence")
    args = parser.parse_args()
    if args.record and OUTPUT.exists():
        raise ValueError(f"Preserve existing evidence before recording a new acceptance: {OUTPUT}")
    if not args.record and not OUTPUT.exists():
        raise ValueError("Missing persistent evidence; run once with --record before acceptance")
    start = time.monotonic()
    suite = unittest.defaultTestLoader.discover(str(ROOT / "infra"), pattern="test_gate_calibration.py")
    result = unittest.TextTestRunner(verbosity=1).run(suite)
    if not result.wasSuccessful() or result.testsRun == 0 or result.skipped:
        raise ValueError("Paired-comparator tests missing, failed or skipped")
    runs = []
    with tempfile.TemporaryDirectory(prefix="rag-gate-acceptance-") as temp:
        directory = Path(temp)
        identity_file = directory / "identity.json"
        environment = dict(os.environ, RAG_GATE_IDENTITY_REPORT=str(identity_file))
        runs.append(run_tests("tests/RagEngine.Core.Tests",
            "FullyQualifiedName~GateCalibration|FullyQualifiedName~ConfidenceGateBandTests|"
            "FullyQualifiedName~CrossEncoderStableGateScoreTests|FullyQualifiedName~RetrievalProfileTests|"
            "FullyQualifiedName~RetrievalScoreContractTests",
            directory, "core", environment, [
                "GateCalibration_identity_describes_loaded_bytes",
                "Profile_binding_keeps_identity_and_thresholds_together",
                "Incompatible_loaded_identity_is_rejected_even_with_same_filename",
                "Missing_identity_and_inconsistent_score_scale_are_rejected",
                "Invalid_or_incomplete_thresholds_are_rejected",
                "Profile_threshold_boundaries_do_not_mutate_global_gate_or_ranking",
                "Unprofiled_empty_and_nonreranked_paths_preserve_baseline_without_model",
                "Incompatible_calibration_fails_before_sources_or_generation",
                "Shared_generation_uses_profile_snapshot_and_preserves_unprofiled_control",
                "Retrieval_reads_manifest_profile_and_rejects_mismatch_without_changing_nonrerank",
            ]))
        identity = json.loads(identity_file.read_bytes())
        validate_identity(identity)
        runs.append(run_tests("tests/RagEngine.Architecture.Tests",
            "FullyQualifiedName~GateCalibrationEvalTests", directory, "cli", environment, [
                "Eval_serializes_effective_identity_and_profile_not_configured_filename",
                "Eval_without_rerank_needs_no_model_and_keeps_identity_absent",
                "Eval_rejects_mixed_binary_provenance_in_one_run",
            ]))
    measured_sources = [
        "src/RagEngine.Core/Domain/GateCalibration.cs",
        "src/RagEngine.Core/Domain/RetrievalResult.cs",
        "src/RagEngine.Core/Domain/RetrievalProfile.cs",
        "src/RagEngine.Core/Utilities/ContentHasher.cs",
        "src/RagEngine.Core/Infrastructure/Reranking/OnnxCrossEncoderReRanker.cs",
        "src/RagEngine.Core/Infrastructure/VectorStore/QdrantSemanticRetriever.cs",
        "src/RagEngine.Core/Services/Generation/ConfidenceGate.cs",
        "src/RagEngine.Cli/Commands/EvalCommand.cs",
        "src/RagEngine.Cli/Infrastructure/EvalProvenance.cs",
        "infra/gate-bandas-barrido.py", "infra/gate_calibration.py", "infra/test_gate_calibration.py",
        "infra/verify-gate-calibration.py",
        "tests/RagEngine.Core.Tests/GateCalibrationTests.cs",
        "tests/RagEngine.Core.Tests/GateCalibrationPipelineTests.cs",
        "tests/RagEngine.Core.Tests/CrossEncoderStableGateScoreTests.cs",
        "tests/RagEngine.Core.Tests/GenerationHttpHarnessTests.cs",
        "tests/RagEngine.Architecture.Tests/GateCalibrationEvalTests.cs",
    ]
    report = {
        "item": "12.10-recalibrar-el-gate-por-binario", "accepted_local_contract": True,
        "binary_change_proposed": False, "new_binary_calibrated": False,
        "elapsed_seconds": round(time.monotonic() - start, 2),
        "base_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "source_sha256": {path: sha256((ROOT / path).read_bytes()) for path in measured_sources},
        "observed_loaded_identity": identity,
        "python_tests_passed": result.testsRun, "dotnet": runs,
        "paired_comparator_fixture": compare(LABELED, fixture(), fixture(), allow_fixtures=True),
        "limits": "Fixture labels prove comparator behavior, not response quality or binary calibration. "
                  "No production profile, served collection, historical sweep or global threshold changed. "
                  "Qdrant fixture collection and copied ONNX binary removed by tests.",
    }
    if args.record:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        with OUTPUT.open("x") as output:
            json.dump(report, output, ensure_ascii=False, indent=2)
            output.write("\n")
    else:
        recorded = json.loads(OUTPUT.read_bytes())
        for field in ("accepted_local_contract", "binary_change_proposed", "new_binary_calibrated",
                      "source_sha256", "observed_loaded_identity", "paired_comparator_fixture"):
            if recorded.get(field) != report[field]:
                raise ValueError(f"Persistent evidence differs: {field}; preserve history and record the justified change")
    print(f"Accepted local contract; NOT a new binary calibration. Evidence: {OUTPUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
