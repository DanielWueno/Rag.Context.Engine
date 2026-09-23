#!/usr/bin/env python3
"""Read-only local capture for the 15.2 oracle; never runs a resolution arm."""
import argparse
from datetime import datetime, timezone
import gzip
import hashlib
import json
import os
from pathlib import Path
import time
import urllib.request


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()


def digest(value):
    return hashlib.sha256(canonical(value)).hexdigest()


def scroll():
    points, seen, offset = [], set(), None
    while True:
        body = {"limit": 256, "with_payload": True, "with_vector": True}
        if offset is not None:
            body["offset"] = offset
        request = urllib.request.Request(
            "http://localhost:6333/collections/bsuite-repo/points/scroll",
            data=canonical(body), headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(request, timeout=60) as response:
            data = json.load(response)
        if data["status"] != "ok":
            raise ValueError("Qdrant did not return ok")
        for point in data["result"]["points"]:
            if point["id"] in seen:
                raise ValueError("Repeated point")
            seen.add(point["id"])
            if not point.get("vector") or not point.get("payload"):
                raise ValueError("Missing vectors/payload")
            points.append({"id": point["id"], "payload": point["payload"],
                           "vector_sha256": digest(point["vector"])})
        offset = data["result"]["next_page_offset"]
        if offset is None:
            break
    if not points:
        raise ValueError("Empty index")
    return sorted(points, key=lambda p: p["id"])


def collection_config():
    with urllib.request.urlopen("http://localhost:6333/collections/bsuite-repo", timeout=30) as response:
        data = json.load(response)
    if data["status"] != "ok":
        raise ValueError("Collection unavailable")
    return data["result"]["config"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--models", type=Path,
                        default=Path(os.environ.get("RAG_MODELS_DIR", str(Path.home() / "models"))))
    args = parser.parse_args()
    started = time.monotonic()
    config = collection_config()
    before = scroll()
    after = scroll()
    if before != after or config != collection_config():
        raise ValueError("Index drift between full consecutive reads")
    models = {}
    for name in ("model_qint8_arm64.onnx", "sentencepiece.bpe.model"):
        path = args.models / "paraphrase-multilingual-MiniLM-L12-v2" / name
        models[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    report = {"captured_at": datetime.now(timezone.utc).isoformat(), "points": before,
              "index_sha256": digest(before), "repeated_read_equal": True,
              "collection_config": config, "models": models,
              "seconds": time.monotonic() - started}
    args.output.write_bytes(gzip.compress(canonical(report), mtime=0))
    print(json.dumps({k: v for k, v in report.items() if k != "points"}))


if __name__ == "__main__":
    main()
