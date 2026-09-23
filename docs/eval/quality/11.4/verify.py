#!/usr/bin/env python3
"""Verificacion de aceptacion para 11.4-rebaseline-olas-5-y-6.

Falla (exit != 0) si:
  - las dos corridas archivadas de reproducibilidad no coinciden exactamente
    en hit_any_at_k/hit_full_at_k para las 55 preguntas del eval-set, o
  - el recall@10 medido no coincide con la linea base historica registrada
    en docs/eval/bsuite-repo.umbral-de-decision.md (total 19/47, simbolo 2/12),
    o
  - el documento de umbrales no contiene las secciones que este item debia
    fijar (control post-5.h/pre-simbolos, ratificacion de 11.2 nulo, formula
    de umbral de salto en preguntas netas).

No re-ejecuta `rag eval` contra Qdrant: verifica los artefactos ya congelados
en este directorio. Regenerarlos requiere la coleccion bsuite-repo viva.
"""
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parents[3]
DOC_PATH = REPO_ROOT / "docs" / "eval" / "bsuite-repo.umbral-de-decision.md"

EXPECTED_TOTAL_HITS = 19
EXPECTED_TOTAL_N = 47
EXPECTED_SIMBOLO_HITS = 2
EXPECTED_SIMBOLO_N = 12


def load(name: str):
    path = HERE / name
    if not path.exists():
        print(f"FALTA {path}", file=sys.stderr)
        sys.exit(1)
    return json.loads(path.read_text(encoding="utf-8"))


def recall_by_category(results):
    cats: dict[str, list[int]] = {}
    for r in results:
        cat = r.get("category")
        bucket = cats.setdefault(cat, [0, 0])
        bucket[0] += 1
        if r.get("hit_any_at_k", {}).get("10"):
            bucket[1] += 1
    return cats


def main() -> int:
    errors: list[str] = []

    run1 = load("repro-1.eval.json")
    run2 = load("repro-2.eval.json")

    r1 = run1["results"]
    r2 = run2["results"]
    if len(r1) != 55 or len(r2) != 55:
        errors.append(f"se esperaban 55 preguntas por corrida, hay {len(r1)}/{len(r2)}")

    diffs = 0
    for a, b in zip(r1, r2):
        if (a.get("hit_any_at_k"), a.get("hit_full_at_k")) != (
            b.get("hit_any_at_k"),
            b.get("hit_full_at_k"),
        ):
            diffs += 1
    if diffs != 0:
        errors.append(f"las dos corridas difieren en {diffs} preguntas; se esperaba reproducibilidad exacta")

    cats = recall_by_category(r1)
    total_n = sum(v[0] for k, v in cats.items() if k != "fuera-de-dominio")
    total_hits = sum(v[1] for k, v in cats.items() if k != "fuera-de-dominio")
    if (total_n, total_hits) != (EXPECTED_TOTAL_N, EXPECTED_TOTAL_HITS):
        errors.append(
            f"recall@10 total observado {total_hits}/{total_n}, esperado {EXPECTED_TOTAL_HITS}/{EXPECTED_TOTAL_N}"
        )

    simbolo = cats.get("simbolo", [0, 0])
    if tuple(simbolo) != (EXPECTED_SIMBOLO_N, EXPECTED_SIMBOLO_HITS):
        errors.append(
            f"recall@10 simbolo observado {simbolo[1]}/{simbolo[0]}, esperado {EXPECTED_SIMBOLO_HITS}/{EXPECTED_SIMBOLO_N}"
        )

    if not DOC_PATH.exists():
        errors.append(f"falta {DOC_PATH}")
    else:
        doc = DOC_PATH.read_text(encoding="utf-8")
        required_markers = [
            "Control post-5.h y pre-símbolos, y ratificación de 11.2",
            "sin fabricar un cierre de",
            "no significa que `bsuite-repo` fue",
            "umbral = max(4, ceil(0.25 * n))",
        ]
        for marker in required_markers:
            if marker not in doc:
                errors.append(f"falta el marcador requerido en el documento de umbrales: {marker!r}")

    if errors:
        print("VERIFICACION FALLIDA:", file=sys.stderr)
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1

    print("OK: reproducibilidad exacta (0 diffs / 55 preguntas), recall total 19/47 y simbolo 2/12 "
          "identicos a la linea base historica, documento de umbrales actualizado con control "
          "post-5.h/pre-simbolos, ratificacion de 11.2 nulo y formula de umbral de salto.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
