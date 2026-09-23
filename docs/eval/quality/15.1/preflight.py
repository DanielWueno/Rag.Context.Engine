#!/usr/bin/env python3
"""Census of syntactic join candidates, NOT verified relations or A/B acceptance."""

import argparse
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request


FIELDS = [
    "relative_path", "class_name", "content_hash", "tenant_id",
    "defined_symbols", "consumed_symbols",
]


def digest(value):
    return hashlib.sha256(
        json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()


def scroll_all(base_url, collection):
    offset = None
    seen_offsets = set()
    points = []
    while True:
        body = {"limit": 500, "with_payload": FIELDS, "with_vector": False}
        if offset is not None:
            body["offset"] = offset
        request = urllib.request.Request(
            f"{base_url}/collections/{urllib.parse.quote(collection, safe='')}/points/scroll",
            data=json.dumps(body).encode(),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(request, timeout=30) as response:
            data = json.load(response)
        if data.get("status") != "ok":
            raise ValueError(f"{collection}: Qdrant status is not ok")
        result = data["result"]
        if not isinstance(result["points"], list):
            raise ValueError(f"{collection}: invalid points")
        points.extend(result["points"])
        offset = result["next_page_offset"]
        if offset is None:
            return points
        key = json.dumps(offset)
        if key in seen_offsets:
            raise ValueError(f"{collection}: repeated scroll offset")
        seen_offsets.add(key)


def census(points):
    if not points:
        raise ValueError("Empty collection: no evidence for a census")
    rows = []
    seen_ids = set()
    excluded = 0
    missing_symbols = 0
    missing_hashes = 0
    for point in points:
        point_id = str(point["id"])
        if point_id in seen_ids:
            raise ValueError(f"Duplicate point ID: {point_id}")
        seen_ids.add(point_id)
        payload = point["payload"]
        if not isinstance(payload, dict):
            raise ValueError(f"{point_id}: invalid payload")
        if not payload.get("relative_path"):
            excluded += 1
            continue
        if not all(isinstance(payload.get(key), str)
                   for key in ("relative_path",)):
            raise ValueError(f"{point_id}: invalid relative_path")
        for key in ("class_name", "tenant_id", "content_hash"):
            if key in payload and not isinstance(payload[key], str):
                raise ValueError(f"{point_id}: invalid {key}")
        if any(key not in payload for key in ("defined_symbols", "consumed_symbols")):
            missing_symbols += 1
        if not payload.get("content_hash"):
            missing_hashes += 1
        symbols = {}
        for key in ("defined_symbols", "consumed_symbols"):
            value = payload.get(key, [])
            if not isinstance(value, list) or any(
                not isinstance(symbol, str) or not symbol for symbol in value
            ):
                raise ValueError(f"{point_id}: invalid {key}")
            symbols[key] = sorted(set(value))
        rows.append({
            "id": point_id,
            **{key: payload.get(key, "") for key in FIELDS if key not in symbols},
            **symbols,
        })
    if not rows:
        raise ValueError("No content points: no evidence for a census")
    rows.sort(key=lambda row: row["id"])
    definitions = defaultdict(set)
    for row in rows:
        for symbol in row["defined_symbols"]:
            definitions[(row["tenant_id"], symbol)].add(row["id"])
    pair_count = 0
    sources_with_candidates = 0
    for row in rows:
        destinations = set()
        for symbol in row["consumed_symbols"]:
            destinations.update(definitions.get((row["tenant_id"], symbol), ()))
        destinations.discard(row["id"])
        pair_count += len(destinations)
        sources_with_candidates += bool(destinations)
    return {
        "points": len(points),
        "content_points": len(rows),
        "excluded_without_relative_path": excluded,
        "content_points_missing_symbol_fields": missing_symbols,
        "content_points_missing_content_hash": missing_hashes,
        "file_type_groups": len({
            (row["tenant_id"], row["relative_path"], row["class_name"]) for row in rows
        }),
        "sources_with_join_candidates": sources_with_candidates,
        "unique_directed_chunk_pairs": pair_count,
        "ambiguous_defined_names": sum(len(ids) > 1 for ids in definitions.values()),
        "metadata_sha256": digest(rows),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("collections", nargs="+")
    parser.add_argument("--base-url", default="http://localhost:6333")
    args = parser.parse_args()
    base_url = args.base_url.rstrip("/")
    url = urllib.parse.urlsplit(base_url)
    if (url.scheme != "http" or url.hostname not in ("localhost", "127.0.0.1", "::1")
            or url.username or url.password or url.path or url.query or url.fragment):
        parser.error("Only a local HTTP Qdrant origin is supported")
    if len(set(args.collections)) != len(args.collections):
        parser.error("Duplicate collections")
    started = time.monotonic()
    try:
        collections = []
        for name in args.collections:
            before = census(scroll_all(base_url, name))
            after = census(scroll_all(base_url, name))
            if before != after:
                raise ValueError(f"{name}: metadata changed between consecutive reads")
            collections.append({"collection": name, **before, "repeated_read_equal": True})
        report = {
            "schema_version": 1,
            "item": "15.1-descripciones-de-relaciones",
            "kind": "preflight_only",
            "captured_at_utc": datetime.now(timezone.utc).isoformat(),
            "engine_commit": subprocess.check_output(
                ["git", "rev-parse", "HEAD"], text=True
            ).strip(),
            "script_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "qdrant_origin": base_url,
            "collections": collections,
            "candidate_definition": (
                "Distinct ordered chunk IDs (source,target) within the same collection "
                "and tenant_id, source != target, with at least one shared "
                "source.consumed_symbols / target.defined_symbols. Missing tenant_id "
                "groups only with missing/empty tenant_id. NOT resolved entity relations; "
                "homonyms remain. Multiple shared names count once per ordered pair."
            ),
            "relation_pairs": None,
            "relation_cache_misses": None,
            "estimated_generation_hours": None,
            "cost_limitations": (
                "Cannot infer LLM calls or cache misses from syntactic candidates. "
                "Need frozen extraction policy, evidence, prompt/model version and "
                "isolated relation cache. No descriptions generated; no cache opened."
            ),
            "llm_calls_executed": 0,
            "writes_to_qdrant": 0,
            "elapsed_seconds": round(time.monotonic() - started, 3),
            "limitations": (
                "Repeated metadata reads, not an atomic snapshot or vector hash. "
                "Not an acceptance command: no labeled dataset, calibration, "
                "truth judgement, retrieval comparison or promotion decision."
            ),
        }
        print(json.dumps(report, ensure_ascii=False, indent=2))
    except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as error:
        print(f"Preflight failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
