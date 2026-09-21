import copy
import io
import json
import unittest
from unittest.mock import patch
import urllib.error

import preflight


def point(point_id, defined=(), consumed=(), tenant=""):
    return {
        "id": point_id,
        "payload": {
            "relative_path": f"{point_id}.cs",
            "class_name": point_id,
            "content_hash": point_id * 64,
            "tenant_id": tenant,
            "defined_symbols": list(defined),
            "consumed_symbols": list(consumed),
        },
    }


class CensusTests(unittest.TestCase):
    def test_counts_ordered_pairs_once_excludes_self_and_tenant_crossing(self):
        points = [
            point("a", ["A"], ["A", "B", "C"], "local"),
            point("b", ["B", "C"], ["A"], "local"),
            point("c", ["B"], [], "local"),
            point("d", ["B"], [], "other"),
        ]
        result = preflight.census(points)
        self.assertEqual(result["unique_directed_chunk_pairs"], 3)
        self.assertEqual(result["sources_with_join_candidates"], 2)
        self.assertEqual(result["ambiguous_defined_names"], 1)

    def test_missing_symbols_are_reported_not_fabricated(self):
        source = point("a")
        del source["payload"]["consumed_symbols"]
        del source["payload"]["defined_symbols"]
        del source["payload"]["content_hash"]
        result = preflight.census([source, {"id": "manifest", "payload": {}}])
        self.assertEqual(result["unique_directed_chunk_pairs"], 0)
        self.assertEqual(result["content_points_missing_symbol_fields"], 1)
        self.assertEqual(result["content_points_missing_content_hash"], 1)
        self.assertEqual(result["excluded_without_relative_path"], 1)

    def test_homonyms_are_candidates_not_deduplicated_as_one_entity(self):
        result = preflight.census([
            point("a", [], ["Master"]),
            point("b", ["Master"]),
            point("c", ["Master"]),
        ])
        self.assertEqual(result["unique_directed_chunk_pairs"], 2)
        self.assertNotIn("verified_relations", result)

    def test_hash_is_order_invariant_but_tracks_content_change(self):
        points = [point("a", ["Z", "A"], ["B"]), point("b", ["B"])]
        expected = preflight.census(points)["metadata_sha256"]
        reordered = copy.deepcopy(points[::-1])
        reordered[1]["payload"]["defined_symbols"].reverse()
        self.assertEqual(preflight.census(reordered)["metadata_sha256"], expected)
        reordered[1]["payload"]["content_hash"] = "changed"
        self.assertNotEqual(preflight.census(reordered)["metadata_sha256"], expected)

    def test_empty_duplicate_or_malformed_evidence_fails(self):
        malformed = point("a")
        malformed["payload"]["consumed_symbols"] = "NotAnArray"
        for points in ([], [point("a"), point("a")], [malformed],
                       [{"id": "manifest", "payload": {}}]):
            with self.subTest(points=points), self.assertRaises(ValueError):
                preflight.census(points)

    def test_missing_tenant_does_not_match_an_explicit_tenant(self):
        source = point("a", [], ["B"])
        del source["payload"]["tenant_id"]
        result = preflight.census([source, point("b", ["B"], tenant="local")])
        self.assertEqual(result["unique_directed_chunk_pairs"], 0)


class TransportTests(unittest.TestCase):
    @staticmethod
    def response(points, offset=None):
        return io.BytesIO(json.dumps({
            "status": "ok",
            "result": {"points": points, "next_page_offset": offset},
        }).encode())

    def test_scroll_reads_all_pages_without_vectors_or_writes(self):
        responses = [
            self.response([point("a")], "a"),
            self.response([point("b")]),
        ]
        with patch("preflight.urllib.request.urlopen", side_effect=responses) as request:
            rows = preflight.scroll_all("http://localhost:6333", "test")
        self.assertEqual(len(rows), 2)
        self.assertEqual(request.call_count, 2)
        for call in request.call_args_list:
            req = call.args[0]
            self.assertTrue(req.full_url.endswith("/points/scroll"))
            self.assertIs(json.loads(req.data)["with_vector"], False)
            self.assertEqual(call.kwargs["timeout"], 30)
        self.assertEqual(json.loads(request.call_args.args[0].data)["offset"], "a")

    def test_repeated_offset_fails(self):
        with patch("preflight.urllib.request.urlopen", side_effect=[
            self.response([point("a")], "a"), self.response([point("b")], "a"),
        ]), self.assertRaisesRegex(ValueError, "repeated"):
            preflight.scroll_all("http://localhost:6333", "test")

    def test_infrastructure_failure_is_not_empty_success(self):
        with patch("sys.argv", ["preflight.py", "test"]), \
                patch("preflight.urllib.request.urlopen",
                      side_effect=urllib.error.URLError("offline")), \
                patch("sys.stdout", new_callable=io.StringIO) as output, \
                patch("sys.stderr", new_callable=io.StringIO) as errors:
            self.assertEqual(preflight.main(), 1)
        self.assertEqual(output.getvalue(), "")
        self.assertIn("offline", errors.getvalue())

    def test_drift_is_not_a_stable_census(self):
        with patch("sys.argv", ["preflight.py", "test"]), \
                patch("preflight.scroll_all", side_effect=[[point("a")], [point("b")]]), \
                patch("sys.stderr", new_callable=io.StringIO) as errors:
            self.assertEqual(preflight.main(), 1)
        self.assertIn("changed", errors.getvalue())

    def test_refuses_external_origin(self):
        with patch("sys.argv", ["preflight.py", "test", "--base-url", "http://example.com"]), \
                patch("sys.stderr", new_callable=io.StringIO), \
                self.assertRaises(SystemExit) as error:
            preflight.main()
        self.assertEqual(error.exception.code, 2)


if __name__ == "__main__":
    unittest.main()
