#!/usr/bin/env python3
"""Item 6.b: harness de latencia emparejado OFF/ON para /api/search.

Mide el p95 de /api/search sobre las 29 preguntas fijadas en 6.c (20 'salto' +
9 'salto-negativo', las mismas que 6.a usó como control) contra la colección
bsuite-repo en vivo, con TopK/MinScore/Rerank iguales a los defaults reales del
endpoint (no los defaults de 'rag eval'), porque el criterio de la ficha es
sobre /api/search, no sobre el comando de evaluación.

Uso:
    python3 infra/bench-6b-twohop-latencia.py --label off
    python3 infra/bench-6b-twohop-latencia.py --label on

Cada corrida hace WARMUP_REPS de calentamiento (descartadas) + REPS repeticiones
secuenciales de las 29 preguntas (mismo orden, misma concurrencia = 1, sin
solapamiento) y guarda las latencias crudas + el p95 en un JSON versionado.
"""
import argparse
import json
import statistics
import subprocess
import sys
import time
import urllib.request

API_URL = "http://localhost:5080/api/search"
COLLECTION = "bsuite-repo"
EVAL_SET = "docs/eval/bsuite-repo.eval-set.json"
WARMUP_REPS = 1
REPS = 5


def load_questions():
    with open(EVAL_SET, encoding="utf-8") as f:
        data = json.load(f)
    return [q["Question"] for q in data if q.get("Category") in ("salto", "salto-negativo")]


def call_search(question):
    payload = json.dumps({"query": question, "collection": COLLECTION, "topK": 10}).encode("utf-8")
    req = urllib.request.Request(
        API_URL, data=payload, headers={"Content-Type": "application/json"}, method="POST"
    )
    start = time.perf_counter()
    with urllib.request.urlopen(req, timeout=60) as resp:
        resp.read()
    elapsed_ms = (time.perf_counter() - start) * 1000.0
    return elapsed_ms


def p95(values):
    values = sorted(values)
    if not values:
        return None
    idx = max(0, min(len(values) - 1, int(round(0.95 * (len(values) - 1)))))
    return values[idx]


def git_commit():
    try:
        return subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd="/Users/DevStudio/Documents/Projects/Rag.Context.Engine"
        ).decode().strip()
    except Exception:
        return "unknown"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--label", required=True, help="Etiqueta de la corrida (off/on/...)")
    parser.add_argument("--out", default=None, help="Ruta de salida JSON")
    args = parser.parse_args()

    questions = load_questions()
    if len(questions) != 29:
        print(f"ADVERTENCIA: se esperaban 29 preguntas (salto+salto-negativo), hay {len(questions)}", file=sys.stderr)

    print(f"[{args.label}] {len(questions)} preguntas, {WARMUP_REPS} rep(s) de calentamiento + {REPS} rep(s) medidas", file=sys.stderr)

    for w in range(WARMUP_REPS):
        for q in questions:
            call_search(q)

    all_latencies = []
    per_question = {}
    for r in range(REPS):
        for q in questions:
            ms = call_search(q)
            all_latencies.append(ms)
            per_question.setdefault(q, []).append(ms)

    result = {
        "label": args.label,
        "commit": git_commit(),
        "collection": COLLECTION,
        "n_preguntas": len(questions),
        "reps": REPS,
        "warmup_reps": WARMUP_REPS,
        "n_muestras": len(all_latencies),
        "p95_ms": p95(all_latencies),
        "p50_ms": statistics.median(all_latencies),
        "media_ms": statistics.mean(all_latencies),
        "min_ms": min(all_latencies),
        "max_ms": max(all_latencies),
        "latencias_ms": all_latencies,
    }

    out_path = args.out or f"docs/eval/baselines/bsuite-repo.6b-twohop-latencia.{args.label}.json"
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=2)

    print(f"p95={result['p95_ms']:.1f}ms p50={result['p50_ms']:.1f}ms media={result['media_ms']:.1f}ms -> {out_path}")


if __name__ == "__main__":
    main()
