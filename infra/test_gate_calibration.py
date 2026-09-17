import copy
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from gate_calibration import compare, sha256

ROOT = Path(__file__).resolve().parents[1]
LABELED = (ROOT / "docs/eval/gate-bandas.labeled-set.json").read_bytes()
FROZEN_HASH = "8f5b03af69d0b2c5ec0c90b175aa49df908718851d6ba6c7397684f19b266912"


def fixture():
    identity = {
        "model_sha256": "a" * 64, "tokenizer_sha256": "b" * 64,
        "binary": "fixture.onnx", "architecture": "arm64",
        "stable_gate_score": True, "max_sequence_length": 512, "batch_size": 8,
    }
    rows = []
    for row in json.loads(LABELED):
        present = row["etiqueta"] == "presente"
        answer = "Respuesta sintetica correcta." if present else "No consta en el corpus."
        rows.append(dict(row, score=.9 if present else .01, result_count=1,
                         cross_encoder=copy.deepcopy(identity),
                         retrieval_succeeded=True, answer=answer,
                         answer_label="correcta" if present else "abstencion",
                         reviewed_answer_sha256=sha256(answer.encode()),
                         judgement="Fixture de contrato, no respuesta medida ni calibracion real."))
    return {
        "schema_version": 1, "kind": "fixture", "labeled_set_sha256": sha256(LABELED),
        "cross_encoder": identity,
        "calibration": {"cross_encoder": copy.deepcopy(identity),
                        "low_confidence_threshold": .05, "high_confidence_threshold": .6},
        "controls": {k: "d" * 64 for k in
                     ("corpus_sha256", "retrieval_config_sha256", "prompts_sha256", "generation_config_sha256")},
        "rows": rows,
    }


class GateCalibrationComparisonTests(unittest.TestCase):
    def setUp(self):
        self.control = fixture()
        self.candidate = copy.deepcopy(self.control)

    def compare(self):
        return compare(LABELED, self.control, self.candidate, allow_fixtures=True)

    def test_frozen_set_counts_and_identical_control(self):
        self.assertEqual(FROZEN_HASH, sha256(LABELED))
        result = self.compare()
        self.assertEqual((65, 38, 27), (result["n"], result["present"], result["absent"]))
        self.assertTrue(result["accepted"])
        self.assertEqual(38, result["correct_candidate"])
        self.assertEqual(27, result["abstentions_candidate"])
        self.assertEqual(65, len(result["pairs"]))
        self.candidate["rows"].reverse()
        self.assertEqual(result, self.compare())

    def test_scores_cannot_invent_answer_labels_and_crossings_are_reported(self):
        self.candidate["rows"][0]["score"] = .01
        result = self.compare()
        self.assertTrue(result["accepted"])
        self.assertTrue(result["pairs"][0]["crossed_band"])
        self.assertEqual("sin_grounding", result["pairs"][0]["candidate_band"])
        self.assertEqual("correcta", result["pairs"][0]["candidate_label"])

    def test_new_individual_fabrication_fails_even_when_total_is_unchanged(self):
        self.control["rows"][0]["answer_label"] = "fabricacion"
        self.candidate["rows"][1]["answer_label"] = "fabricacion"
        result = self.compare()
        self.assertFalse(result["accepted"])
        self.assertEqual(1, result["new_fabrications"])

    def test_loss_of_correct_answers_fails_and_abstentions_are_visible(self):
        self.candidate["rows"][0]["answer_label"] = "abstencion"
        result = self.compare()
        self.assertFalse(result["accepted"])
        self.assertEqual(37, result["correct_candidate"])
        self.assertEqual(28, result["abstentions_candidate"])
        self.assertTrue(result["pairs"][0]["lost_correct"])

    def test_missing_duplicate_stale_or_unreviewed_evidence_fails(self):
        mutations = [
            lambda a: a["rows"].pop(),
            lambda a: a["rows"].append(a["rows"][0]),
            lambda a: a["rows"][0].pop("answer_label"),
            lambda a: a["rows"][0].update(answer="changed after review"),
            lambda a: a["rows"][0].update(judgement=""),
            lambda a: a["rows"][0].update(retrieval_succeeded=False),
            lambda a: a["rows"][0].update(score=float("nan")),
            lambda a: a["rows"][0].update(score=float("inf")),
            lambda a: a["rows"][0].update(result_count=0),
            lambda a: a["rows"][0].update(etiqueta="ausente"),
            lambda a: a.update(labeled_set_sha256="e" * 64),
            lambda a: a["controls"].update(corpus_sha256="e" * 64),
            lambda a: a["cross_encoder"].update(model_sha256="e" * 64),
            lambda a: a["cross_encoder"].pop("stable_gate_score"),
            lambda a: a["rows"][0].pop("cross_encoder"),
            lambda a: a["rows"][0]["cross_encoder"].update(model_sha256="e" * 64),
            lambda a: a["calibration"].update(low_confidence_threshold=None),
            lambda a: a["calibration"].update(high_confidence_threshold=.01),
            lambda a: a["calibration"].update(high_confidence_threshold=float("nan")),
        ]
        for mutation in mutations:
            with self.subTest(mutation=mutations.index(mutation)):
                self.candidate = copy.deepcopy(self.control)
                mutation(self.candidate)
                with self.assertRaises(ValueError):
                    self.compare()

    def test_no_results_is_explicit_not_missing_measurement(self):
        self.candidate["rows"][0].update(score=0, result_count=0)
        self.assertEqual("sin_grounding", self.compare()["pairs"][0]["candidate_band"])

    def test_fixture_is_never_a_measured_calibration(self):
        with self.assertRaisesRegex(ValueError, "Measured evidence required"):
            compare(LABELED, self.control, self.candidate)

    def test_cli_exit_codes_for_regression_and_missing_evidence(self):
        with tempfile.TemporaryDirectory(prefix="rag-gate-comparison-") as directory:
            control, candidate = Path(directory) / "control.json", Path(directory) / "candidate.json"
            control.write_text(json.dumps(self.control))
            command = [sys.executable, str(ROOT / "infra/gate-bandas-barrido.py"),
                       "--comparar", str(control), str(candidate), "--fixtures"]
            candidate.write_text(json.dumps(self.candidate))
            good = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(0, good.returncode, good.stderr)
            self.assertTrue(json.loads(good.stdout)["accepted"])
            self.candidate["rows"][0]["answer_label"] = "fabricacion"
            candidate.write_text(json.dumps(self.candidate))
            bad = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(1, bad.returncode, bad.stderr)
            self.assertFalse(json.loads(bad.stdout)["accepted"])
            candidate.unlink()
            missing = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(1, missing.returncode)
            self.assertIn("ERROR:", missing.stderr)

    def test_score_sweep_rejects_cli_failure_and_keeps_empty_distinct(self):
        spec = importlib.util.spec_from_file_location("gate_sweep", ROOT / "infra/gate-bandas-barrido.py")
        sweep = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(sweep)
        for code, output in [(1, "[]"), (0, "not json")]:
            with patch.object(sweep.subprocess, "run", return_value=subprocess.CompletedProcess([], code, output, "error")):
                with self.assertRaises(ValueError):
                    sweep.buscar("q", "c", True)
        with patch.object(sweep.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "banner\n[]", "")):
            self.assertEqual({"score": 0.0, "resultados": 0, "retrieval_succeeded": True},
                             sweep.buscar("q", "c", True))


if __name__ == "__main__":
    unittest.main()
