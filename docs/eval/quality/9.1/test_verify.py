import copy
import unittest

from verify import compare


class ComparatorTests(unittest.TestCase):
    def setUp(self):
        self.items = [{"Question": "q", "Category": "literal", "SourceFile": "A.cs", "TargetContentContains": ["ancla"]}]
        self.run = {
            "collection": "fixture", "top_k": 10, "min_score": 0.1, "rerank": False,
            "provenance": {"eval_set_hash": "abcdef123456", "rrf_k": 60},
            "results": [{"question": "q", "category": "literal", "source_file": "A.cs", "top_score": 0.5,
                         "hit_any_at_k": {str(k): True for k in (1, 3, 5, 10)},
                         "hit_full_at_k": {str(k): True for k in (1, 3, 5, 10)},
                         "hits": [{"rank": 1, "score": 0.5, "is_target_file": True, "matches_anchor": True}]}],
        }

    def check(self, candidate):
        return compare(self.items, self.run, candidate, "fixture", "abcdef123456")

    def test_control_positivo(self):
        self.assertEqual([], self.check(copy.deepcopy(self.run))["differences"])

    def test_rechaza_mutaciones(self):
        def set_hit(run):
            run["results"][0]["hit_any_at_k"]["10"] = False

        mutations = [
            set_hit,
            lambda run: run.update(results=[]),
            lambda run: run["results"].append(copy.deepcopy(run["results"][0])),
            lambda run: run["results"][0].update(hits=[]),
            lambda run: run["results"][0]["hit_full_at_k"].pop("3"),
            lambda run: run["provenance"].update(eval_set_hash="hash-distinto"),
            lambda run: run["provenance"].update(rrf_k=61),
            lambda run: run["results"][0].update(top_score=float("nan")),
        ]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                candidate = copy.deepcopy(self.run)
                mutation(candidate)
                with self.assertRaises(ValueError):
                    self.check(candidate)

    def test_negativo_no_es_un_hit_fabricado(self):
        self.items[0].update(SourceFile=None, TargetContentContains=[], Category="fuera-de-dominio")
        self.run["results"][0].update(source_file=None, category="fuera-de-dominio",
                                      hit_any_at_k={}, hit_full_at_k={})
        self.run["results"][0]["hits"][0].update(is_target_file=False, matches_anchor=False)
        self.assertEqual(1, self.check(copy.deepcopy(self.run))["unanchored"])
        changed = copy.deepcopy(self.run)
        changed["results"][0]["hits"][0]["score"] = 0.9
        with self.assertRaises(ValueError):
            self.check(changed)

    def test_no_acepta_dataset_vacio(self):
        with self.assertRaises(ValueError):
            compare([], self.run, self.run, "fixture", "abcdef123456")


if __name__ == "__main__":
    unittest.main()
