#!/usr/bin/env python3
"""Huellas de índice/corpus/config/modelos para el experimento 15.2.2.

Reutiliza capture.py y prepare.py SELLADOS en lugar de reimplementar su JSON canónico:
una segunda implementación del digest en C# sería una fuente silenciosa de "drift"
falso o, peor, de coincidencias falsas. Sólo lee; nunca escribe en la colección.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import time

HERE = Path(__file__).resolve().parent
BUNDLE = HERE.parent
sys.path.insert(0, str(BUNDLE))

from capture import collection_config, digest, scroll  # noqa: E402
from prepare import sha, write  # noqa: E402

# Campos de payload que consumen los brazos. Se vuelca sólo esto (no vectores) para que
# el runner no tenga que repetir el scroll ni manipular el índice.
PAYLOAD_FIELDS = ("relative_path", "class_name", "method_name", "namespace",
                  "content", "content_hash", "defined_symbols", "consumed_symbols",
                  "start_line", "end_line", "chunk_type")


def corpus_manifest_sha256(corpus, tmp):
    revision = subprocess.check_output(["git", "-C", str(corpus), "rev-parse", "HEAD"],
                                       text=True).strip()
    tracked = subprocess.check_output(["git", "-C", str(corpus), "ls-files", "-z"]).decode().split("\0")
    files = {name: sha(corpus / name) for name in tracked if name}
    manifest = {"revision": revision, "files": files,
                "untracked_policy": "Ignored, not ingested or changed by this item"}
    write(tmp, manifest)
    return revision, sha(tmp), len(files)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", type=Path, required=True)
    parser.add_argument("--models", type=Path, default=Path.home() / "models")
    parser.add_argument("--dump-payloads", type=Path)
    parser.add_argument("--tmp", type=Path, required=True)
    args = parser.parse_args()

    started = time.monotonic()
    config = collection_config()
    points = scroll()

    models = {}
    for name in ("model_qint8_arm64.onnx", "sentencepiece.bpe.model"):
        path = args.models / "paraphrase-multilingual-MiniLM-L12-v2" / name
        models[name] = hashlib.sha256(path.read_bytes()).hexdigest()

    revision, corpus_sha, file_count = corpus_manifest_sha256(args.corpus, args.tmp)

    if args.dump_payloads:
        rows = [dict({"id": p["id"]},
                     **{k: p["payload"].get(k) for k in PAYLOAD_FIELDS})
                for p in points]
        args.dump_payloads.write_bytes(gzip.compress(
            json.dumps(rows, ensure_ascii=False, separators=(",", ":")).encode(), mtime=0))

    print(json.dumps({
        "index_sha256": digest(points),
        "collection_config_sha256": digest(config),
        "models": models,
        "corpus_revision": revision,
        "corpus_manifest_sha256": corpus_sha,
        "corpus_files": file_count,
        "points": len(points),
        "seconds": time.monotonic() - started,
    }))


if __name__ == "__main__":
    main()
