"""Synthetic boundary tests ONLY: none of these records is experimental evidence."""
from copy import deepcopy
from contextlib import redirect_stderr, redirect_stdout
import io
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest.mock import patch

import accept
import capture


class ComparatorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.b = accept.load_bundle(sealed=False)
        cls.index = {p["id"]: p for p in cls.b["index"]}
        all_oracle_ids = {id_ for o in cls.b["opportunities"]
                          for id_ in o["seed_ids"] + o["valid_target_ids"]}
        cls.wrong = next(id_ for id_ in cls.index if id_ not in all_oracle_ids)
        cls.jump = [q for q in cls.b["cohort"] if q["item"]["Category"] == "salto"]
        cls.outside = next(q for q in cls.b["cohort"] if q["anchored"]
                           and q["item"]["Category"] != "salto")
        cls.base = cls.experiment()

    @classmethod
    def hit(cls, id_):
        return {"id": id_, "content_hash": cls.index[id_]["content_hash"]}

    @classmethod
    def predictions(cls, n):
        result = []
        for i, o in enumerate(cls.b["opportunities"]):
            result.append({**{k: o[k] for k in ("query_id", "question", "seed_ids", "seed_version")},
                           "opportunity_id": o["id"], "succeeded": True, "elapsed_ms": 1,
                           "candidates": [cls.hit(o["valid_target_ids"][0] if i < n else cls.wrong)]})
        return result

    @classmethod
    def experiment(cls):
        b = cls.b
        arms = {}
        for arm in accept.ARMS:
            arms[arm] = {
                "implementation_sha256": "a" * 64,
                "index_before": b["preparation"]["index_sha256"],
                "index_after": b["preparation"]["index_sha256"],
                "corpus_before": b["preparation"]["corpus_manifest_sha256"],
                "corpus_after": b["preparation"]["corpus_manifest_sha256"],
                "collection_config_before": accept.digest(b["preparation"]["collection_config"]),
                "collection_config_after": accept.digest(b["preparation"]["collection_config"]),
                "models_before": deepcopy(b["preparation"]["models"]),
                "models_after": deepcopy(b["preparation"]["models"]),
                "build_seconds": 1, "update_seconds": 1, "machine_seconds": 1000,
                "precision": [] if arm == "no-expansion" else cls.predictions(27 if arm == "syntax" else 26)}
        queries = {q["id"]: q for q in b["cohort"]}
        jump_gain = {q["id"] for q in cls.jump[:5]}
        runs = []
        for key in accept.schedule(b):
            q = queries[key["query_id"]]
            hit = q["anchored"] and (q["item"]["Category"] != "salto" or
                                    (key["arm"] in accept.ALTERNATIVES and q["id"] in jump_gain))
            runs.append({**key, "succeeded": True, "elapsed_ms": 100,
                         "hits": [cls.hit(q["anchor_ids"][0])] if hit else []})
        return {"kind": "measured_experiment", "freeze_sha256": b["freeze_sha256"],
                "parameters": deepcopy(b["protocol"]["parameters"]), "runner_sha256": "b" * 64,
                "engine_commit": "c" * 40, "command": ["SYNTHETIC_TEST_ONLY_NOT_A_RUNNER"],
                "runtime": "synthetic", "hardware": "synthetic", "arms": arms, "runs": runs,
                "graph_execution": None}

    def setUp(self):
        self.e = deepcopy(self.base)

    def result(self):
        return accept.compare(self.b, self.e)

    def reject(self):
        with self.assertRaises(accept.Incomplete):
            self.result()

    def set_hits(self, arm, qid, hits):
        for row in self.e["runs"]:
            if row["arm"] == arm and row["query_id"] == qid:
                row["hits"] = deepcopy(hits)

    def enable_graph(self):
        self.e["arms"]["graph"]["precision"] = self.predictions(27)
        self.e["graph_execution"] = {
            "implementation_sha256": "a" * 64, "runner_sha256": "b" * 64,
            "seconds": 1, "command": ["SYNTHETIC_GRAPH_TEST_ONLY"],
            "observations": {s["id"]: deepcopy(s["expected"]) for s in self.b["graph-fixtures"]["scenarios"]}}

    def test_27_correct_and_five_gains_pass(self):
        self.assertEqual(self.result()["eligible"], ["syntax"])

    def test_26_correct_is_complete_null_not_missing(self):
        self.e["arms"]["syntax"]["precision"] = self.predictions(26)
        self.assertEqual(self.result()["status"], "complete_no_promotion")

    def test_four_jump_gains_do_not_pass(self):
        self.set_hits("syntax", self.jump[4]["id"], [])
        self.assertEqual(self.result()["status"], "complete_no_promotion")

    def test_jump_gain_is_net_of_individual_losses(self):
        q = self.jump[5]
        self.set_hits("no-expansion", q["id"], [self.hit(q["anchor_ids"][0])])
        self.assertEqual(self.result()["summaries"]["syntax"]["jump_net_gain"], 4)
        self.assertFalse(self.result()["summaries"]["syntax"]["eligible"])

    def test_one_outside_loss_fails_even_if_offset_elsewhere(self):
        self.set_hits("syntax", self.outside["id"], [])
        self.assertFalse(self.result()["summaries"]["syntax"]["eligible"])

    def test_jump_negative_loss_fails(self):
        q = next(q for q in self.b["cohort"] if q["item"]["Category"] == "salto-negativo")
        self.set_hits("syntax", q["id"], [])
        self.assertFalse(self.result()["summaries"]["syntax"]["eligible"])

    def test_p95_delta_exactly_150_passes(self):
        for row in self.e["runs"]:
            if row["arm"] == "syntax":
                row["elapsed_ms"] = 250
        self.assertIn("syntax", self.result()["eligible"])

    def test_p95_delta_over_150_fails(self):
        for row in self.e["runs"]:
            if row["arm"] == "syntax":
                row["elapsed_ms"] = 250.001
        self.assertNotIn("syntax", self.result()["eligible"])

    def test_percentile_is_nearest_rank_not_interpolated(self):
        self.assertEqual(accept.p95([1] * 399 + [1000] * 21), 1)
        self.assertEqual(accept.p95([1] * 398 + [1000] * 22), 1000)

    def test_rejection_is_not_correct(self):
        rows = self.predictions(27)
        rows[0]["candidates"] = []
        r = accept.precision(self.b, rows)
        self.assertEqual((r["correct"], r["denominator"], len(r["abstention_ids"])), (26, 30, 1))

    def test_all_abstentions_keep_denominator(self):
        rows = self.predictions(0)
        for row in rows:
            row["candidates"] = []
        r = accept.precision(self.b, rows)
        self.assertEqual(r["correct"], 0)
        self.assertEqual(r["denominator"], 30)
        self.assertIsNone(r["returned_precision"])

    def test_expected_second_candidate_does_not_count(self):
        rows = self.predictions(27)
        rows[0]["candidates"].insert(0, self.hit(self.wrong))
        self.assertEqual(accept.precision(self.b, rows)["correct"], 26)

    def test_returned_destination_cannot_be_reused_within_query(self):
        rows = self.predictions(30)
        first = self.b["opportunities"][4]
        second_index = next(i for i, o in enumerate(self.b["opportunities"])
                            if o["query_id"] == first["query_id"] and o["stage"] == "internal")
        target = next(id_ for id_ in first["valid_target_ids"]
                      if id_ not in self.b["opportunities"][second_index]["seed_ids"])
        rows[4]["candidates"] = [self.hit(target)]
        rows[second_index]["candidates"] = [self.hit(target)]
        r = accept.precision(self.b, rows)
        self.assertEqual(r["correct"], 29)
        self.assertEqual(r["reused_ids"], [rows[second_index]["opportunity_id"]])

    def test_missing_opportunity_fails(self):
        self.e["arms"]["syntax"]["precision"].pop()
        self.reject()

    def test_seed_hash_version_drift_fails(self):
        self.e["arms"]["syntax"]["precision"][0]["seed_version"] = "e" * 64
        self.reject()

    def test_query_id_tampering_fails(self):
        self.e["runs"][0]["query_id"] = "q99"
        self.reject()

    def test_question_tampering_fails(self):
        self.e["runs"][0]["question"] = "different question"
        self.reject()

    def test_unknown_destination_fails(self):
        self.e["arms"]["syntax"]["precision"][0]["candidates"][0]["id"] = "invented"
        self.reject()

    def test_content_hash_tampering_fails(self):
        self.e["runs"][0]["hits"][0]["content_hash"] = "e" * 64
        self.reject()

    def test_duplicate_hits_fail(self):
        self.e["runs"][0]["hits"] *= 2
        self.reject()

    def test_missing_control_fails(self):
        del self.e["arms"]["no-expansion"]
        self.reject()

    def test_missing_alternative_fails(self):
        del self.e["arms"]["semantic"]
        self.reject()

    def test_missing_negative_fails(self):
        q = next(q for q in self.b["cohort"] if not q["anchored"])
        self.e["runs"] = [r for r in self.e["runs"] if r["query_id"] != q["id"]]
        self.reject()

    def test_negative_noise_is_not_a_recall_gain(self):
        q = next(q for q in self.b["cohort"] if not q["anchored"])
        self.set_hits("syntax", q["id"], [self.hit(self.wrong)])
        self.assertFalse(self.result()["summaries"]["syntax"]["eligible"])

    def test_missing_latency_fails(self):
        del self.e["runs"][0]["elapsed_ms"]
        with self.assertRaises(KeyError):
            self.result()

    def test_invalid_latencies_fail(self):
        for value in (0, -1, float("nan"), float("inf"), True, "100"):
            with self.subTest(value=value):
                self.e["runs"][0]["elapsed_ms"] = value
                self.reject()

    def test_execution_order_altered_fails(self):
        self.e["runs"][0], self.e["runs"][1] = self.e["runs"][1], self.e["runs"][0]
        self.reject()

    def test_missing_warmup_fails(self):
        self.e["runs"] = [r for r in self.e["runs"] if r["phase"] != "warmup"]
        self.reject()

    def test_replica_drift_fails(self):
        row = next(r for r in self.e["runs"] if r["phase"] == "measure" and
                   r["replica"] == 4 and r["arm"] == "syntax" and r["hits"])
        row["hits"] = []
        self.reject()

    def test_infrastructure_failure_is_incomplete(self):
        self.e["runs"][0]["succeeded"] = False
        self.reject()

    def test_index_changed_fails(self):
        self.e["arms"]["graph"]["index_after"] = "e" * 64
        self.reject()

    def test_collection_config_changed_fails(self):
        self.e["arms"]["graph"]["collection_config_after"] = "e" * 64
        self.reject()

    def test_model_changed_fails(self):
        self.e["arms"]["semantic"]["models_after"]["model_qint8_arm64.onnx"] = "e" * 64
        self.reject()

    def test_corpus_changed_fails(self):
        self.e["arms"]["syntax"]["corpus_after"] = "e" * 64
        self.reject()

    def test_missing_cost_fails(self):
        self.e["arms"]["syntax"]["build_seconds"] = None
        self.reject()

    def test_undercounted_cost_fails(self):
        self.e["arms"]["syntax"]["machine_seconds"] = 0
        self.reject()

    def test_wrong_freeze_fails(self):
        self.e["freeze_sha256"] = "e" * 64
        self.reject()

    def test_wrong_common_config_fails(self):
        self.e["parameters"]["rerank"] = True
        self.reject()

    def test_graph_requires_real_contract_coverage(self):
        self.e["arms"]["graph"]["precision"] = self.predictions(27)
        with self.assertRaises((accept.Incomplete, TypeError)):
            self.result()

    def test_graph_complete_contract_is_eligible_but_smaller_arm_preferred(self):
        self.enable_graph()
        self.assertEqual(self.result()["eligible"], ["syntax", "graph"])
        self.assertEqual(self.result()["preferred"], "syntax")

    def test_graph_missing_resume_observation_is_incomplete(self):
        self.enable_graph()
        self.e["graph_execution"]["observations"]["lifecycle"].pop()
        self.reject()

    def test_graph_orphan_is_complete_failure_not_promotion(self):
        self.enable_graph()
        self.e["graph_execution"]["observations"]["lifecycle"][-1]["edges"] = [["a",1,"b",3,3]]
        self.assertNotIn("graph", self.result()["eligible"])

    def test_graph_stale_edge_version_fails(self):
        self.enable_graph()
        self.e["graph_execution"]["observations"]["lifecycle"][1]["edges"][0][3] = 1
        self.assertNotIn("graph", self.result()["eligible"])

    def test_graph_boolean_is_not_an_endpoint_version(self):
        self.enable_graph()
        self.e["graph_execution"]["observations"]["lifecycle"][0]["edges"][0][1] = True
        self.assertNotIn("graph", self.result()["eligible"])

    def test_graph_replay_resurrecting_deleted_node_fails(self):
        self.enable_graph()
        observations = self.e["graph_execution"]["observations"]["lifecycle"]
        observations[-1] = deepcopy(observations[4])
        self.assertNotIn("graph", self.result()["eligible"])

    def test_graph_denied_intermediate_cannot_reach_allowed_target(self):
        self.enable_graph()
        actual = self.e["graph_execution"]["observations"]["authorization"][0]
        actual["hits"].append("z")
        actual["traversed"][1].append(["b", "z"])
        self.assertNotIn("graph", self.result()["eligible"])

    def test_graph_cross_tenant_prefix_and_collection_leaks_fail(self):
        for target in ("t", "f", "c", "n"):
            with self.subTest(target=target):
                self.enable_graph()
                self.e["graph_execution"]["observations"]["authorization"][0]["hits"].append(target)
                self.assertNotIn("graph", self.result()["eligible"])

    def test_graph_resume_must_preserve_authorization(self):
        self.enable_graph()
        self.e["graph_execution"]["observations"]["authorization"][1]["hits"].append("t")
        self.assertNotIn("graph", self.result()["eligible"])

    def test_threshold_tampering_fails(self):
        b = deepcopy(self.b)
        b["protocol"]["thresholds"]["correct_destinations"] = 26
        with self.assertRaises(accept.Incomplete):
            accept.validate_bundle(b)

    def test_anchor_tampering_fails(self):
        b = deepcopy(self.b)
        b["cohort"][0]["item"]["TargetContentContains"] = ["easier anchor"]
        with self.assertRaises(accept.Incomplete):
            accept.validate_bundle(b)

    def test_destination_label_tampering_fails(self):
        b = deepcopy(self.b)
        b["opportunities"][0]["valid_target_ids"] = [self.wrong]
        with self.assertRaises(accept.Incomplete):
            accept.validate_bundle(b)

    def test_missing_source_corpus_fails(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(accept.Incomplete):
                accept.verify_sources(self.b, Path(temp) / "missing")

    def test_sealed_artifact_tampering_fails(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            for name in accept.ARTIFACTS:
                shutil.copyfile(accept.HERE / name, directory / name)
            accept.write(directory / "freeze.json", {
                "artifacts": {name: accept.sha(directory / name) for name in accept.ARTIFACTS},
                "inputs": {name: accept.sha(accept.ROOT / name) for name in accept.INPUTS}})
            accept.load_bundle(directory)
            with (directory / "opportunities.json").open("a") as stream:
                stream.write(" ")
            with self.assertRaises(accept.Incomplete):
                accept.load_bundle(directory)

    def test_capture_infrastructure_failure_is_not_empty_success(self):
        with patch("urllib.request.urlopen", side_effect=OSError("service unavailable")):
            with self.assertRaises(OSError):
                capture.scroll()

    def test_capture_drift_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "capture.gz"
            with patch.object(capture, "scroll", side_effect=[[{"id": "a"}], [{"id": "b"}]]), \
                    patch.object(capture, "collection_config", return_value={}):
                with patch("sys.argv", ["capture.py", str(output)]):
                    with self.assertRaises(ValueError):
                        capture.main()
            self.assertFalse(output.exists())

    def test_cli_complete_null_returns_zero(self):
        self.e["arms"]["syntax"]["precision"] = self.predictions(26)
        stdout = io.StringIO()
        with patch("sys.argv", ["accept.py", "--experiment", "synthetic-in-memory"]), \
                patch.object(accept, "load_bundle", return_value=self.b), \
                patch.object(accept, "read", return_value=self.e), redirect_stdout(stdout):
            self.assertEqual(accept.main(), 0)
        self.assertIn('"status": "complete_no_promotion"', stdout.getvalue())

    def test_cli_missing_evidence_returns_two(self):
        stderr = io.StringIO()
        with patch("sys.argv", ["accept.py", "--experiment", "missing"]), \
                patch.object(accept, "load_bundle", return_value=self.b), \
                patch.object(accept, "read", side_effect=FileNotFoundError("missing evidence")), \
                redirect_stderr(stderr):
            self.assertEqual(accept.main(), 2)
        self.assertIn('"status": "incomplete"', stderr.getvalue())

    def test_cli_refuses_to_overwrite_experiment(self):
        with patch("sys.argv", ["accept.py", "--experiment", "same", "--output", "same"]), \
                patch.object(accept, "load_bundle", return_value=self.b), \
                patch.object(accept, "read", return_value=self.e), redirect_stderr(io.StringIO()):
            self.assertEqual(accept.main(), 2)


if __name__ == "__main__":
    unittest.main()
