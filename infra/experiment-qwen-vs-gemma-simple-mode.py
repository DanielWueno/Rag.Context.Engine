#!/usr/bin/env python3
"""
Experimento puntual: ¿el modelo de generación configurado (Qwen2.5-Coder,
afinado a código) es intrínsecamente más propenso a filtrar sintaxis de
código en ResponseMode.Simple que un modelo no-coder (Gemma), ANTES del
filtro determinístico de Fase 1 (SimpleAnswerSanitizer)?

Esto es la "Opción C" descrita en docs/analisis-futuro/modo-respuesta-simple-codigo.md,
despriorizada en su momento por falta de evidencia, no por evidencia en contra.
Este script mide esa pregunta puntual — no reemplaza el criterio de aceptación
de Fase 1 (infra/verify-simple-mode.py), que sigue siendo la comprobación
oficial del filtro con el modelo de producción.

Reutiliza (por import, no por copia) los patrones de violación y el cargador
de preguntas históricas ya usados en infra/verify-simple-mode.py para no
duplicar el instrumento de medición y evitar que ambos scripts diverjan.

Metodología:
- Corpus: TODAS las preguntas históricas ÚNICAS (dedupe por texto exacto,
  primera ocurrencia) logueadas con Collection=bsuite-repo y ResponseMode=Simple
  en logs/rag-api-*.json — mismo corpus ya usado para aceptar Fase 1, sin
  recorte manual para favorecer un resultado.
- Única variable que cambia entre brazos: Ollama:ModelId del contenedor
  (Qwen2.5-Coder vs Gemma). Retrieval (Qdrant, embeddings ONNX, cross-encoder)
  y el resto de la config quedan idénticos entre corridas.
- RagGeneration:EnableSimpleModeSanitizer=false en AMBOS brazos: se mide el
  sesgo crudo del modelo, no el filtro (que ya se sabe que limpia lo que
  encuentra — acá se compara qué tan sucia sale la respuesta antes de filtrar).

Uso (una invocación por brazo, con el contenedor ya reconfigurado y reiniciado
para ese brazo — ver infra/README o el mensaje de este script al terminar):

    python3 infra/experiment-qwen-vs-gemma-simple-mode.py \\
        --base-url http://localhost:5080 \\
        --logs-dir logs \\
        --collection bsuite-repo \\
        --arm qwen2.5-coder \\
        --output /tmp/experimento-simple-mode-qwen.json
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import sys
import time
from pathlib import Path

_VERIFY_SCRIPT = Path(__file__).parent / "verify-simple-mode.py"
_spec = importlib.util.spec_from_file_location("verify_simple_mode", _VERIFY_SCRIPT)
verify_simple_mode = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(verify_simple_mode)


def dedupe_by_query(entries: list[dict]) -> list[dict]:
    seen: set[str] = set()
    unique: list[dict] = []
    for entry in entries:
        q = entry["Query"]
        if q in seen:
            continue
        seen.add(q)
        unique.append(entry)
    return unique


def main() -> None:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("--base-url", default="http://localhost:5080")
    parser.add_argument("--logs-dir", default="logs")
    parser.add_argument("--collection", default="bsuite-repo")
    parser.add_argument("--arm", required=True, help="Etiqueta del modelo bajo prueba en este brazo (solo para el reporte; el modelo real lo decide la config del contenedor)")
    parser.add_argument("--timeout", type=float, default=180.0)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    entries = verify_simple_mode.load_historical_questions(args.logs_dir, args.collection, "Simple")
    unique_entries = dedupe_by_query(entries)
    print(
        f"[{args.arm}] {len(entries)} entradas históricas -> {len(unique_entries)} preguntas únicas "
        f"(collection={args.collection}, ResponseMode=Simple)",
        file=sys.stderr,
    )

    results = []
    violation_count = 0
    no_evidence_count = 0
    error_count = 0
    total_violations = 0
    started = time.monotonic()

    for i, entry in enumerate(unique_entries, 1):
        query = entry["Query"]
        t0 = time.monotonic()
        try:
            answer, sources_count = verify_simple_mode.ask(args.base_url, entry, args.timeout)
        except Exception as ex:  # noqa: BLE001 - queremos capturar cualquier fallo de transporte y seguir
            error_count += 1
            print(f"[{args.arm}][{i}/{len(unique_entries)}] ERROR: {query[:60]!r} -> {ex}", file=sys.stderr)
            results.append({"query": query, "error": str(ex)})
            continue
        elapsed = time.monotonic() - t0

        violations = verify_simple_mode.find_violations(answer or "")
        no_evidence = sources_count == 0
        if no_evidence:
            no_evidence_count += 1
        if violations:
            violation_count += 1
            total_violations += len(violations)

        print(
            f"[{args.arm}][{i}/{len(unique_entries)}] "
            f"{'FAIL' if violations else 'PASS'} ({len(violations)} violaciones"
            f"{', SIN EVIDENCIA' if no_evidence else ''}, {elapsed:.1f}s) — {query[:60]!r}",
            file=sys.stderr,
        )

        results.append({
            "query": query,
            "answer": answer,
            "violations": violations,
            "sources_count": sources_count,
            "no_evidence": no_evidence,
            "elapsed_seconds": round(elapsed, 2),
        })

    total_elapsed = time.monotonic() - started
    answered = len(unique_entries) - error_count

    summary = {
        "arm": args.arm,
        "collection": args.collection,
        "total_unique_questions": len(unique_entries),
        "answered": answered,
        "errors": error_count,
        "responses_with_violations": violation_count,
        "responses_without_violations": answered - violation_count,
        "total_violations": total_violations,
        "no_evidence_count": no_evidence_count,
        "violation_rate": round(violation_count / answered, 4) if answered else None,
        "total_elapsed_seconds": round(total_elapsed, 1),
        "avg_seconds_per_question": round(total_elapsed / len(unique_entries), 2) if unique_entries else None,
    }

    print("", file=sys.stderr)
    print(f"=== [{args.arm}] {json.dumps(summary, ensure_ascii=False)} ===", file=sys.stderr)

    with open(args.output, "w", encoding="utf-8") as f:
        json.dump({"summary": summary, "results": results}, f, ensure_ascii=False, indent=2)
    print(f"Detalle completo en {args.output}", file=sys.stderr)


if __name__ == "__main__":
    main()
