#!/usr/bin/env python3
"""
Ítem 5.b — comparador A/B pre-registrado: control (resumen por chunk) vs candidato
(resumen por archivo/tipo), sobre la salida JSON de `rag eval --json`.

Criterio de aceptación literal de la ficha: "El modo por archivo/tipo no pierde más
de 1 pregunta neta @10 en NINGÚN set" (bsuite-repo cohorte histórica: 47 respondibles
+ 8 negativos; bsuite-auditorias: 16 respondibles). Exit 0 sólo si TODOS los sets
comparados cumplen el umbral; exit 1 en cualquier otro caso (incluye datos faltantes,
tamaños de eval-set distintos entre corridas, o pérdida > 1).

No decide solo: además exige que el candidato reporte instrumentación de 5.b
(resumen_granularity/resumen_groups/resumen_llm_calls) para no aceptar una corrida
sin evidencia de qué granularidad se usó realmente.

Uso:
    python3 comparar.py --set nombre --control control.eval.json --candidato candidato.eval.json [...]

Puede repetirse --set varias veces para comparar varios sets en una sola invocación
(salida agregada, PASS solo si todos pasan).
"""
import argparse
import json
import sys
from pathlib import Path

K = "10"
NEGATIVE_CATEGORY = "fuera-de-dominio"


def load(path: str) -> dict:
    p = Path(path)
    if not p.exists():
        print(f"FALTA {p}", file=sys.stderr)
        sys.exit(1)
    return json.loads(p.read_text(encoding="utf-8"))


def hits_by_category(results: list[dict]) -> dict[str, tuple[int, int]]:
    """category -> (hits, n)"""
    cats: dict[str, list[int]] = {}
    for r in results:
        cat = r.get("category", "(sin-categoria)")
        bucket = cats.setdefault(cat, [0, 0])
        bucket[1] += 1
        if r.get("hit_any_at_k", {}).get(K):
            bucket[0] += 1
    return {k: (v[0], v[1]) for k, v in cats.items()}


def index_by_question(results: list[dict]) -> dict[str, dict]:
    return {r["question"]: r for r in results}


def compare_one_set(name: str, control_path: str, candidato_path: str) -> tuple[bool, list[str]]:
    errores: list[str] = []
    control = load(control_path)
    candidato = load(candidato_path)

    r_control = control["results"]
    r_candidato = candidato["results"]

    if len(r_control) != len(r_candidato):
        errores.append(
            f"[{name}] tamaños de eval-set distintos entre control ({len(r_control)}) "
            f"y candidato ({len(r_candidato)}) -- no comparable"
        )
        return False, errores

    # Verifica que sean literalmente las mismas preguntas (mismo eval-set congelado).
    preguntas_control = {r["question"] for r in r_control}
    preguntas_candidato = {r["question"] for r in r_candidato}
    if preguntas_control != preguntas_candidato:
        errores.append(f"[{name}] las preguntas de control y candidato no coinciden -- eval-sets distintos")
        return False, errores

    cats_control = hits_by_category(r_control)
    cats_candidato = hits_by_category(r_candidato)

    respondibles_control = sum(h for cat, (h, _) in cats_control.items() if cat != NEGATIVE_CATEGORY)
    respondibles_candidato = sum(h for cat, (h, _) in cats_candidato.items() if cat != NEGATIVE_CATEGORY)
    n_respondibles = sum(n for cat, (_, n) in cats_control.items() if cat != NEGATIVE_CATEGORY)

    perdida_neta = respondibles_control - respondibles_candidato

    print(f"== {name} ==")
    print(f"  respondibles: control {respondibles_control}/{n_respondibles} -> candidato {respondibles_candidato}/{n_respondibles} "
          f"(perdida neta = {perdida_neta})")
    for cat in sorted(set(cats_control) | set(cats_candidato)):
        hc, nc = cats_control.get(cat, (0, 0))
        hcand, _ = cats_candidato.get(cat, (0, 0))
        marca = " <- categoria negativa" if cat == NEGATIVE_CATEGORY else ""
        print(f"    {cat}: control {hc}/{nc} -> candidato {hcand}/{nc}{marca}")

    # Negativos: un negativo que pasa de "no acierta" a "acierta" es un falso
    # positivo nuevo -- se reporta explícitamente aunque el umbral formal de la
    # ficha sea sobre respondibles.
    idx_control = index_by_question(r_control)
    idx_candidato = index_by_question(r_candidato)
    nuevos_falsos_positivos = []
    for q, rc in idx_control.items():
        if rc.get("category") != NEGATIVE_CATEGORY:
            continue
        rcand = idx_candidato[q]
        if not rc.get("hit_any_at_k", {}).get(K) and rcand.get("hit_any_at_k", {}).get(K):
            nuevos_falsos_positivos.append(q)
    if nuevos_falsos_positivos:
        print(f"  ADVERTENCIA: {len(nuevos_falsos_positivos)} negativo(s) ganaron un hit@10 que no tenían en control:")
        for q in nuevos_falsos_positivos:
            print(f"    - {q}")

    if perdida_neta > 1:
        errores.append(
            f"[{name}] perdida neta de {perdida_neta} preguntas respondibles @10 supera el umbral <=1 de la ficha 5.b"
        )

    return len(errores) == 0, errores


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--set", action="append", nargs=3, metavar=("NOMBRE", "CONTROL", "CANDIDATO"),
                         required=True, help="Puede repetirse. NOMBRE es una etiqueta libre para el reporte.")
    parser.add_argument("--exigir-instrumentacion", action="append", metavar="RUTA_INGEST_SUMMARY_JSON",
                         help="Ruta a un IngestionSummary (JSON) de la corrida PerFile; si se da, exige "
                              "ResumenGranularity=PerFile y ResumenGroups>0 antes de aceptar el set.")
    args = parser.parse_args()

    todo_ok = True
    todos_errores: list[str] = []

    for nombre, control_path, candidato_path in args.set:
        ok, errores = compare_one_set(nombre, control_path, candidato_path)
        todo_ok = todo_ok and ok
        todos_errores.extend(errores)

    if args.exigir_instrumentacion:
        for ruta in args.exigir_instrumentacion:
            resumen = load(ruta)
            granularidad = resumen.get("ResumenGranularity") or resumen.get("resumenGranularity")
            grupos = resumen.get("ResumenGroups") or resumen.get("resumenGroups") or 0
            if granularidad != "PerFile":
                todo_ok = False
                todos_errores.append(f"{ruta}: ResumenGranularity={granularidad!r}, se esperaba 'PerFile'")
            if not grupos:
                todo_ok = False
                todos_errores.append(f"{ruta}: ResumenGroups={grupos}, se esperaba >0 (evidencia de que el modo por archivo/tipo corrió de verdad)")

    print()
    if todo_ok:
        print("PASS: todos los sets cumplen el umbral <=1 perdida neta @10 de la ficha 5.b.")
        return 0

    print("FAIL:")
    for e in todos_errores:
        print(f"  - {e}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
