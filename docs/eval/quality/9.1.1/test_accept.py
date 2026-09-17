import copy
import unittest

from accept import SETS, BASES, historical, compare_replicas, compare_arms, validate_coverage


class ComparatorTests(unittest.TestCase):
    def setUp(self):
        self.items = [{"Question": "q", "Category": "literal", "SourceFile": "a.cs",
                       "TargetContentContains": ["anchor"]}]
        hits = [{"rank": i, "score": .5, "chunk_id": f"{i:08x}-1111-4111-8111-111111111111",
                 "ranking_score": .5, "ranking_score_scale": "RankFusionNative",
                 "is_target_file": i == 1, "matches_anchor": i == 1} for i in range(1, 11)]
        self.run = {
            "collection": "fixture", "top_k": 10, "min_score": .1, "rerank": False,
            "provenance": {"eval_set_hash": "a" * 12},
            "results": [{"question": "q", "category": "literal", "source_file": "a.cs",
                         "top_score": .5, "hit_any_at_k": {k: True for k in historical.CUTOFFS},
                         "hit_full_at_k": {k: True for k in historical.CUTOFFS},
                         "hits": hits, "retrieval_succeeded": True, "elapsed_ms": 1,
                         "vector_queries": 2, "candidates_returned": 80}]
        }

    def check(self, runs):
        return compare_replicas(self.items, runs, "fixture", "a" * 64)

    def test_valid_three_replicas(self):
        self.check([copy.deepcopy(self.run) for _ in range(3)])

    def test_rejects_each_missing_replica(self):
        for count in (0, 1, 2, 4):
            with self.subTest(count=count), self.assertRaises(ValueError):
                self.check([copy.deepcopy(self.run) for _ in range(count)])

    def test_rejects_missing_invalid_or_changed_evidence(self):
        mutations = {
            "failed": lambda r: r["results"][0].update(retrieval_succeeded=False),
            "empty": lambda r: r["results"][0].update(hits=[]),
            "omitted_question": lambda r: r.update(results=[]),
            "duplicate_question": lambda r: r["results"].append(r["results"][0]),
            "order": lambda r: r["results"][0]["hits"].reverse(),
            "id": lambda r: r["results"][0]["hits"][0].update(chunk_id="ffffffff-1111-4111-8111-111111111111"),
            "bad_id": lambda r: r["results"][0]["hits"][0].update(chunk_id="not-uuid"),
            "duplicate_id": lambda r: r["results"][0]["hits"][1].update(
                chunk_id=r["results"][0]["hits"][0]["chunk_id"]),
            "score": lambda r: r["results"][0]["hits"][0].update(ranking_score=.50000001),
            "nonfinite": lambda r: r["results"][0]["hits"][0].update(ranking_score=float("nan")),
            "hash": lambda r: r["provenance"].update(eval_set_hash="b" * 12),
            "missing_cutoff": lambda r: r["results"][0]["hit_any_at_k"].pop("3"),
            "no_queries": lambda r: r["results"][0].update(vector_queries=0),
        }
        for name, mutate in mutations.items():
            with self.subTest(name=name), self.assertRaises((ValueError, KeyError)):
                runs = [copy.deepcopy(self.run) for _ in range(3)]
                mutate(runs[2])
                self.check(runs)

    def test_ab_rejects_hit_changes_even_if_top10_unchanged(self):
        other = copy.deepcopy(self.run)
        other["results"][0]["hit_any_at_k"]["1"] = False
        with self.assertRaises(ValueError):
            compare_arms(self.items, self.run, other, "fixture", "a" * 64)

    def test_unanchored_compares_observable_scores_and_ids(self):
        self.items[0].update(SourceFile=None, TargetContentContains=[])
        self.run["results"][0].update(source_file=None, hit_any_at_k={}, hit_full_at_k={})
        other = copy.deepcopy(self.run)
        other["results"][0]["hits"][0]["chunk_id"] = "ffffffff-1111-4111-8111-111111111111"
        with self.assertRaises(ValueError):
            compare_arms(self.items, self.run, other, "fixture", "a" * 64)

    def test_manifest_requires_every_set_arm_replica_and_index_check(self):
        protocol = {"sets": {name: {"before": "same-index"} for name in SETS},
                    "arms": {arm: {} for arm in BASES}}
        capture = {"sets": {name: {"index_checks": ["same-index"] * 7,
                                   "A": {str(i): "hash" for i in range(1, 4)},
                                   "B": {str(i): "hash" for i in range(1, 4)}} for name in SETS}}
        validate_coverage(protocol, capture)
        for name in SETS:
            bad = copy.deepcopy(capture)
            del bad["sets"][name]
            with self.assertRaises(ValueError):
                validate_coverage(protocol, bad)
            for replica in ("1", "2", "3"):
                bad = copy.deepcopy(capture)
                del bad["sets"][name]["B"][replica]
                with self.assertRaises(ValueError):
                    validate_coverage(protocol, bad)
            for snapshots in ([], ["same-index"] * 6, ["changed"] + ["same-index"] * 6):
                bad = copy.deepcopy(capture)
                bad["sets"][name]["index_checks"] = snapshots
                with self.assertRaises(ValueError):
                    validate_coverage(protocol, bad)
        for arm in BASES:
            bad_protocol = copy.deepcopy(protocol)
            del bad_protocol["arms"][arm]
            with self.assertRaises(ValueError):
                validate_coverage(bad_protocol, capture)


if __name__ == "__main__":
    unittest.main()
