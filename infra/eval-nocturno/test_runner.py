import copy
import json
import socket
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import runner

ROOT = Path(__file__).resolve().parents[2]


def fixture(n=47, negatives=8):
    dataset = [
        {"Question": f"question-{i}", "Category": "simbolo" if i < 3 else "literal",
         "SourceFile": "source.cs" if i < n else None,
         "TargetContentContains": ["anchor"] if i < n else []}
        for i in range(n + negatives)
    ]
    data = runner.canonical(dataset)
    provenance = {
        "git_commit": "fixture", "git_dirty": False,
        "eval_set_hash": runner.digest(data)[:12],
        "embedding_model": "fixture.onnx", "embedding_dimensions": 384,
        "embedding_max_sequence_length": 256, "cross_encoder_model": None,
        "cross_encoder": None, "gate_calibration": None,
        "weight_codigo": 1, "weight_sparse": 1.3, "weight_resumen": 2.5, "rrf_k": 60,
        "chunking_contract_version": 3, "index_short_type_declarations": False,
        "resumen_prompt_version": "fixture",
    }
    result = {
        "collection": "fixture", "top_k": 10, "rerank": False, "min_score": 0.1,
        "provenance": provenance,
        "nightly_context": {
            "schema_version": 1, "models": {key: "a" * 64 for key in runner.MODEL_KEYS},
            "configuration": {"appsettings_sha256": "b" * 64, "shared_sha256": "d" * 64,
                              "environment_policy": 1, "architecture": "fixture"},
            "collection": {"points": 20, "sha256": "c" * 64, "vectors": {"dense": {"size": 384}},
                           "sparse_vectors": {"sparse-code": {}}},
        },
        "results": [
            {"question": r["Question"], "category": r["Category"], "source_file": r["SourceFile"],
             "retrieval_succeeded": True,
             "hit_any_at_k": {str(k): True for k in (1, 3, 5, 10)} if i < n else {},
             "hit_full_at_k": {str(k): True for k in (1, 3, 5, 10)} if i < n else {}}
            for i, r in enumerate(dataset)
        ],
    }
    return data, result


def set_hit(result, index, value):
    for field in ("hit_any_at_k", "hit_full_at_k"):
        result["results"][index][field] = {str(k): value for k in (1, 3, 5, 10)}


class ComparatorTests(unittest.TestCase):
    def setUp(self):
        self.data, self.baseline = fixture()
        self.candidate = copy.deepcopy(self.baseline)

    def compare(self):
        return runner.compare(self.data, self.baseline, self.candidate)

    def test_control_and_one_net_loss_pass_two_fail_with_ids_and_categories(self):
        for losses, expected in ((0, "ok"), (1, "ok"), (2, "regression")):
            with self.subTest(losses=losses):
                self.candidate = copy.deepcopy(self.baseline)
                for i in range(losses):
                    set_hit(self.candidate, i, False)
                result = self.compare()
                self.assertEqual(expected, result["status"])
                self.assertEqual(losses, result["net_lost"])
                self.assertEqual(losses, len(result["lost"]))
                self.assertEqual(47, result["answerable"])
                self.assertEqual(8, result["negative"])
                self.assertAlmostEqual(100 * losses / 47, result["lost_percentage_points"])
                self.assertTrue(all(r["id"] and r["category"] == "simbolo" for r in result["lost"]))

    def test_net_losses_do_not_hide_individual_losses(self):
        set_hit(self.baseline, 4, False)
        set_hit(self.candidate, 0, False)
        set_hit(self.candidate, 1, False)
        result = self.compare()
        self.assertEqual("ok", result["status"])
        self.assertEqual(1, result["net_lost"])
        self.assertEqual(2, len(result["lost"]))
        self.assertEqual(1, len(result["gained"]))

    def test_shuffled_results_and_new_commit_are_comparable(self):
        self.candidate["results"].reverse()
        self.candidate["provenance"].update(git_commit="new", git_dirty=True)
        self.assertEqual("ok", self.compare()["status"])

    def test_dataset_mutation_is_incomparable_not_regression(self):
        dataset = json.loads(self.data)
        dataset[0]["TargetContentContains"] = ["different"]
        self.data = runner.canonical(dataset)
        result = self.compare()
        self.assertEqual("incomparable", result["status"])
        self.assertNotIn("net_lost", result)

    def test_every_comparability_key_is_checked(self):
        for key in runner.PROVENANCE_KEYS:
            with self.subTest(key=key):
                self.candidate = copy.deepcopy(self.baseline)
                self.candidate["provenance"][key] = "changed"
                self.assertEqual("incomparable", self.compare()["status"])

    def test_missing_provenance_fails_even_in_both_arms(self):
        del self.baseline["provenance"]["cross_encoder"]
        del self.candidate["provenance"]["cross_encoder"]
        self.assertEqual("incomparable", self.compare()["status"])

    def test_historical_baseline_is_not_silently_upgraded(self):
        del self.baseline["nightly_context"]
        self.assertEqual("incomparable", self.compare()["status"])

    def test_equally_incomplete_contexts_never_pass(self):
        for key in ("models", "configuration", "collection"):
            with self.subTest(key=key):
                baseline, candidate = copy.deepcopy(self.baseline), copy.deepcopy(self.candidate)
                baseline["nightly_context"][key] = {"unrelated": True}
                candidate["nightly_context"][key] = {"unrelated": True}
                self.assertEqual("incomparable", runner.compare(self.data, baseline, candidate)["status"])

    def test_settings_model_config_and_index_changes_are_incomparable(self):
        for key, value in (("collection", "other"), ("top_k", 5),
                           ("rerank", True), ("min_score", 0.2)):
            with self.subTest(key=key):
                self.candidate = copy.deepcopy(self.baseline)
                self.candidate[key] = value
                self.assertEqual("incomparable", self.compare()["status"])
        for key in ("models", "configuration", "collection"):
            with self.subTest(key=key):
                self.candidate = copy.deepcopy(self.baseline)
                self.candidate["nightly_context"][key]["changed"] = True
                self.assertEqual("incomparable", self.compare()["status"])

    def test_missing_extra_duplicate_and_relabelled_questions_are_incomparable(self):
        mutations = [
            lambda rows: rows.pop(),
            lambda rows: rows.append(copy.deepcopy(rows[0])),
            lambda rows: rows.__setitem__(1, copy.deepcopy(rows[0])),
            lambda rows: rows[0].update(category="wrong"),
            lambda rows: rows[0].update(source_file="wrong"),
            lambda rows: rows[0].update(retrieval_succeeded=False),
            lambda rows: rows[0].update(hit_any_at_k={"10": "false"}),
            lambda rows: rows[0].update(hit_any_at_k={"1": True, "3": False, "5": True, "10": True}),
            lambda rows: rows[-1].update(hit_any_at_k={"10": True}),
        ]
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                self.candidate = copy.deepcopy(self.baseline)
                mutate(self.candidate["results"])
                self.assertEqual("incomparable", self.compare()["status"])

    def test_current_denominator_is_not_hardcoded_to_47(self):
        data, baseline = fixture(76, 8)
        candidate = copy.deepcopy(baseline)
        for i in (0, 1):
            set_hit(candidate, i, False)
        result = runner.compare(data, baseline, candidate)
        self.assertEqual("regression", result["status"])
        self.assertAlmostEqual(200 / 76, result["lost_percentage_points"])

    def test_sourcefiles_oracle_matches_eval_command(self):
        rows = [{"Question": "q", "Category": "c", "SourceFile": None,
                 "SourceFiles": ["a.cs", "b.cs"], "TargetContentContains": ["x"]}]
        self.assertTrue(runner.cohort(rows)["q"]["answerable"])


class OperationalTests(unittest.TestCase):
    def test_compare_command_exit_codes_and_visible_alarms(self):
        data, baseline = fixture()
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "dataset.json").write_bytes(data)
            runner.save(path / "baseline.json", baseline)
            for code, status in ((0, "ok"), (1, "regression"), (2, "incomparable")):
                candidate = copy.deepcopy(baseline)
                if code:
                    for index in (0, 1):
                        set_hit(candidate, index, False)
                if code == 2:
                    candidate["provenance"]["weight_sparse"] = 7
                file = path / f"candidate-{code}.json"
                runner.save(file, candidate)
                process = subprocess.run(
                    [sys.executable, str(ROOT / "infra/eval-nocturno/runner.py"), "compare",
                     "--dataset", str(path / "dataset.json"), "--baseline", str(path / "baseline.json"),
                     "--candidate", str(file)], capture_output=True, text=True, timeout=20)
                self.assertEqual(code, process.returncode, process.stderr)
                self.assertEqual(status, json.loads(process.stdout)["status"])
                if code:
                    self.assertIn("ALARMA", process.stderr)

    def test_frozen_inventory_covers_all_five_profiles(self):
        profiles = runner.inventory(ROOT)
        self.assertEqual(5, len(profiles))
        self.assertEqual(4, len({p["collection"] for p in profiles}))
        self.assertEqual(1, sum(p["rerank"] for p in profiles))

    def test_absent_model_is_infrastructure_with_persistent_alarm_and_nonzero_exit(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "run"
            process = subprocess.run(
                [sys.executable, str(ROOT / "infra/eval-nocturno/runner.py"), "run",
                 "--root", str(ROOT), "--output", str(output),
                 "--models-dir", str(Path(directory) / "missing")],
                capture_output=True, text=True, timeout=20)
            self.assertEqual(3, process.returncode, process.stderr)
            report = runner.load(output / "report.json")
            self.assertEqual("infrastructure", report["status"])
            self.assertIn("ausente", report["reason"])
            self.assertIn("ALARMA", process.stderr)

    def test_absent_qdrant_is_not_zero_recall(self):
        with socket.socket() as reserved:
            reserved.bind(("127.0.0.1", 0))
            with self.assertRaises(runner.GateError) as caught:
                runner.snapshot(reserved.getsockname()[1], "missing")
        self.assertEqual("infrastructure", caught.exception.status)
        self.assertIn("Qdrant", str(caught.exception))

    def test_nonzero_command_is_infrastructure(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(runner.GateError) as caught:
                runner.execute([sys.executable, "-c", "raise SystemExit(7)"], ROOT, {},
                               Path(directory), "failed", 10)
            self.assertEqual("infrastructure", caught.exception.status)
            self.assertIn("exit 7", str(caught.exception))

    def test_changed_or_dirty_checkout_is_rejected_before_build(self):
        for commit, dirty in (("wrong", ""), ("expected", " M src/file.cs")):
            with self.subTest(commit=commit, dirty=dirty):
                with patch.object(runner, "git", side_effect=[commit, dirty]):
                    with self.assertRaises(runner.GateError) as caught:
                        runner.check_trust(ROOT, "expected")
                self.assertEqual("infrastructure", caught.exception.status)

    def test_schedule_uses_installed_runner_pinned_commit_no_checkout_script(self):
        directory = Path("/local/trusted")
        value = runner.schedule(ROOT, directory, "/usr/bin/python3", "/usr/bin/dotnet",
                                "frozen", Path("/models"), 3, 0)
        arguments = value["ProgramArguments"]
        self.assertEqual(str(directory / "runner.py"), arguments[1])
        self.assertEqual("frozen", arguments[arguments.index("--expected-commit") + 1])
        self.assertEqual({"Hour": 3, "Minute": 0}, value["StartCalendarInterval"])
        self.assertNotIn("KeepAlive", value)
        self.assertNotIn("RunAtLoad", value)


if __name__ == "__main__":
    unittest.main()
