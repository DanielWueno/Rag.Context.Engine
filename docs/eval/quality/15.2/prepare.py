#!/usr/bin/env python3
"""Build source-grounded fixtures from an immutable read-only capture, before arms."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import gzip
import hashlib
import json
from pathlib import Path
import re
import subprocess
import time

from capture import digest

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    data = json.dumps(value, ensure_ascii=False, indent=2).encode() + b"\n"
    path.write_bytes(gzip.compress(data, mtime=0) if path.suffix == ".gz" else data)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def source_evidence(root, point):
    payload = point["payload"]
    path = root / payload["relative_path"]
    text = path.read_text(encoding="utf-8-sig")
    positions = [i for i, c in enumerate(text) if not c.isspace()]
    normalized = "".join(text[i] for i in positions)
    needle = "".join(payload["content"].split())
    start = normalized.find(needle)
    require(start >= 0 and normalized.find(needle, start + 1) < 0,
            f"Source content absent/ambiguous: {point['id']}")
    first, last = positions[start], positions[start + len(needle) - 1]
    line_start, line_end = text.count("\n", 0, first) + 1, text.count("\n", 0, last) + 1
    return {"file": payload["relative_path"], "file_sha256": sha(path),
            "start_line": line_start, "end_line": line_end,
            "text": "\n".join(text.splitlines()[line_start - 1:line_end])}


def build(snapshot, corpus):
    started = time.monotonic()
    protocol = json.loads((HERE / "protocol.json").read_text())
    review = json.loads((HERE / "review.json").read_text())
    revision = subprocess.check_output(["git", "-C", str(corpus), "rev-parse", "HEAD"],
                                       text=True).strip()
    require(revision == review["corpus_revision"], "Corpus revision changed")
    dataset = json.loads((ROOT / protocol["dataset"]).read_text())
    require(len(dataset) == 84, "Wrong cohort")
    jump = [(i, q) for i, q in enumerate(dataset) if q["Category"] == "salto"]
    points = snapshot["points"]
    require(snapshot["repeated_read_equal"] and digest(points) == snapshot["index_sha256"],
            "Invalid snapshot")
    by_file = {}
    for p in points:
        by_file.setdefault(Path(p["payload"]["relative_path"]).name, []).append(p)
    opportunities, selected = [], {}
    primaries = {}

    def keep(point):
        if point["id"] not in selected:
            p = point["payload"]
            selected[point["id"]] = {
                **point, "indexed_content_sha256": hashlib.sha256(p["content"].encode()).hexdigest(),
                "source_evidence": source_evidence(corpus, point)}
        return point["id"]

    def add(spec, seed, targets, stage):
        q_index, q = jump[spec["query"]]
        require(targets, f"Missing target: {spec}")
        target_ids = [keep(p) for p in targets]
        seed_id = keep(seed)
        require(seed_id not in target_ids, "Self expansion")
        opportunities.append({
            "id": f"q{q_index:02d}-{stage}", "query_id": f"q{q_index:02d}",
            "question": q["Question"], "stage": stage, "seed_ids": [seed_id],
            "seed_version": snapshot["index_sha256"], "method": spec["method"],
            "valid_target_ids": target_ids, "reason": spec["reason"]})

    for spec in review["primary"]:
        _, q = jump[spec["query"]]
        source_name = re.search(r": (\w+\.cs)", q["Note"])[1]
        method = spec["method"]
        seeds = [p for p in by_file[source_name]
                 if method in p["payload"].get("consumed_symbols", [])
                 and re.search(r"\." + method + r"\s*\(", p["payload"]["content"])]
        targets = sorted([p for p in by_file[q["SourceFile"]]
                          if p["payload"].get("method_name") == method],
                         key=lambda p: (p["payload"]["start_line"], p["id"]))
        require(len(seeds) == 1, f"Ambiguous primary seed: {spec}")
        require(len({p["payload"]["relative_path"] for p in targets}) == 1,
                f"Ambiguous primary destination: {spec}")
        add(spec, seeds[0], targets, "entry")
        primaries[spec["query"]] = targets
    for spec in review["secondary"]:
        targets = primaries[spec["query"]]
        method = spec["method"]
        seeds = [p for p in targets if re.search(r"(?<![\w.])" + method + r"\s*\(",
                                               p["payload"]["content"])]
        require(seeds, f"Missing secondary call: {spec}")
        seed = seeds[0]
        destinations = sorted([p for p in points
                               if p["payload"]["relative_path"] == seed["payload"]["relative_path"]
                               and p["payload"].get("class_name") == seed["payload"].get("class_name")
                               and p["payload"].get("method_name") == method],
                              key=lambda p: (p["payload"]["start_line"], p["id"]))
        add(spec, seed, destinations, "internal")
    require(len(opportunities) == 30, "Exactly 30 required")
    require(len({o["id"] for o in opportunities}) == 30, "Duplicate opportunity")
    tracked = subprocess.check_output(["git", "-C", str(corpus), "ls-files", "-z"]).decode().split("\0")
    files = {name: sha(corpus / name) for name in tracked if name}
    type_evidence = []
    for spec in review["type_evidence"]:
        matches = []
        for name in files:
            if Path(name).name == spec["file"]:
                text = (corpus / name).read_text(encoding="utf-8-sig")
                for number, line in enumerate(text.splitlines(), 1):
                    if spec["needle"] in line:
                        matches.append({"file": name, "file_sha256": files[name],
                                        "line": number, "text": line})
        require(len(matches) == 1, f"Missing/ambiguous receiver proof: {spec}")
        type_evidence.extend(matches)
    cohort = []
    for i, q in enumerate(dataset):
        names = q.get("SourceFiles") or ([q["SourceFile"]] if q.get("SourceFile") else [])
        anchors = q.get("TargetContentContains") or []
        matches = [p["id"] for p in points
                   if Path(p["payload"]["relative_path"]).name.lower() in [n.lower() for n in names]
                   and any(a in p["payload"]["content"] for a in anchors)]
        cohort.append({"id": f"q{i:02d}", "item": q, "anchor_ids": matches,
                       "anchored": bool(names and anchors)})
    index = [{"id": p["id"], "content_hash": p["payload"]["content_hash"],
              "payload_sha256": digest(p["payload"]), "vector_sha256": p["vector_sha256"]}
             for p in points]
    corpus_manifest = {"revision": revision, "files": files,
                       "untracked_policy": "Ignored, not ingested or changed by this item"}
    write(HERE / "corpus-manifest.json.gz", corpus_manifest)
    write(HERE / "index-manifest.json.gz", index)
    write(HERE / "cohort.json", cohort)
    write(HERE / "opportunities.json", opportunities)
    write(HERE / "source-evidence.json", {"chunks": selected, "receiver_types": type_evidence})
    report = {
        "captured_at": snapshot["captured_at"], "prepared_at": datetime.now(timezone.utc).isoformat(),
        "engine_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip(),
        "index_sha256": snapshot["index_sha256"], "index_manifest_digest": digest(index),
        "collection_config": snapshot["collection_config"], "models": snapshot["models"],
        "corpus_revision": revision, "corpus_manifest_sha256": sha(HERE / "corpus-manifest.json.gz"),
        "points": len(points), "source_files": len(files),
        "categories": dict(Counter(q["Category"] for q in dataset)),
        "capture_seconds": snapshot["seconds"], "prepare_seconds": time.monotonic() - started,
        "payload_line_drift": [id_ for id_, p in selected.items()
                              if (p["payload"]["start_line"], p["payload"]["end_line"]) !=
                              (p["source_evidence"]["start_line"], p["source_evidence"]["end_line"])],
        "unmatched_anchors": [q["id"] for q in cohort if q["anchored"] and not q["anchor_ids"]],
        "llm_calls": 0, "index_writes": 0, "arms_executed": []}
    write(HERE / "preparation.json", report)
    print(json.dumps(report, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--corpus", type=Path, required=True)
    args = parser.parse_args()
    require(not (HERE / "freeze.json").exists(), "Already frozen; do not overwrite an instrument")
    build(json.loads(gzip.decompress(args.capture.read_bytes())), args.corpus)


if __name__ == "__main__":
    main()
