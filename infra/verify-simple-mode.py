#!/usr/bin/env python3
"""
Fase 1 de docs/analisis-futuro/modo-respuesta-simple-codigo.md — verificación
cuantitativa del filtro determinístico de ResponseMode.Simple.

Re-corre las preguntas históricas de un log de rag-api-*.json (por defecto,
las de la colección bsuite-repo en modo Simple) contra un contenedor real ya
reconstruido, y marca cada respuesta con el mismo tipo de heurística que usa
RagGenerationService.SanitizeSimpleAnswer (bloques ```, backticks, atributos
[Xxx(...)], identificadores Dotted.PascalCase, snake_case, y PascalCase
compuesto tipo GenerarPlanAuditoria).

Esto NO es un detector independiente del filtro real (server-side ya aplicó
la misma lógica en C# antes de responder) — es la comprobación de extremo a
extremo pedida en la Fase 1: confirma que el contenedor reconstruido de
verdad tiene el sanitizer activo y corriendo, y deja los pares
pregunta/respuesta completos en --output para la revisión manual de falsos
positivos (prosa legítima destrozada) mencionada en el plan.

Uso:
    python3 infra/verify-simple-mode.py \\
        --base-url http://localhost:5080 \\
        --logs-dir logs \\
        --collection bsuite-repo \\
        --output /tmp/verify-simple-mode-results.json

Reutilizable para Fase 2 pasando --collection wiki-solis o --collection rag-engine.
"""
from __future__ import annotations

import argparse
import glob
import json
import re
import sys
import urllib.error
import urllib.request

# Mirror exacto de los patrones en
# src/RagEngine.Core/Services/Generation/RagGenerationService.cs
# (FencedCodeBlockPattern, InlineCodeSpanPattern, AttributeDecorationPattern,
# DottedIdentifierPattern, SnakeCaseIdentifierPattern, CamelHumpIdentifierPattern).
# Si esos regex cambian ahí, deben actualizarse acá también.
PATTERNS = {
    "fenced_code_block": re.compile(r"```[^\n]*\n?([\s\S]*?)```"),
    "inline_code_span": re.compile(r"`[^`\n]+`"),
    "attribute_decoration": re.compile(r"\[[A-Z][A-Za-z0-9]*\([^\]\n]*\)\]"),
    "dotted_identifier": re.compile(r"\b[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*){1,}\b"),
    "snake_case_identifier": re.compile(r"\b[a-z][a-z0-9]*(?:_[a-z0-9]+){1,}\b"),
    "camel_hump_identifier": re.compile(r"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*){1,}\b"),
}


def find_violations(answer: str) -> list[dict]:
    hits = []
    for name, pattern in PATTERNS.items():
        for m in pattern.finditer(answer):
            if name == "fenced_code_block" and not m.group(1).strip():
                continue  # empty fence is not a violation — mirrors the C# sanitizer
            hits.append({"pattern": name, "match": m.group(0)[:120]})
    return hits


def load_historical_questions(
    logs_dir: str, collection: str, response_mode: str | None
) -> list[dict]:
    """
    response_mode=None reissues every historical question for `collection`
    regardless of what ResponseMode was logged at the time — needed for Fase 2
    (docs/analisis-futuro/modo-respuesta-simple-codigo.md) collections like
    wiki-solis/rag-engine that have little or no history actually logged under
    ResponseMode=Simple (the toggle is recent; most of their entries predate it
    and were logged with ResponseMode=None). `ask()` always forces
    responseMode=simple on the outgoing request regardless of this filter, so
    this only changes which historical queries get reissued, not the mode they
    run in now.
    """
    entries = []
    for path in sorted(glob.glob(f"{logs_dir}/rag-api-*.json")):
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                entry = obj.get("Entry")
                if not entry or entry.get("Type") != "ask":
                    continue
                if entry.get("Collection") != collection:
                    continue
                if response_mode is not None and entry.get("ResponseMode") != response_mode:
                    continue
                entries.append(entry)
    return entries


def ask(base_url: str, entry: dict, timeout: float) -> tuple[str | None, int]:
    """Returns (answer, sources_count). sources_count == 0 is the API's own
    signal that this turn hit the no-grounding gate (or a meta-intent match) —
    see ShouldSuppressSources in Program.cs — used by Fase 2 to detect
    "respuesta filtrada" -> "sin evidencia" regressions."""
    payload = {
        "query": entry["Query"],
        "collection": entry.get("Collection"),
        "topK": entry.get("TopK"),
        "minScore": entry.get("MinScore"),
        "rerank": entry.get("Rerank"),
        "responseMode": "simple",
    }
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        f"{base_url}/api/ask",
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        body = json.loads(resp.read().decode("utf-8"))
    return body.get("answer"), len(body.get("sources") or [])


def main() -> None:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("--base-url", default="http://localhost:5080")
    parser.add_argument("--logs-dir", default="logs")
    parser.add_argument("--collection", default="bsuite-repo")
    parser.add_argument(
        "--response-mode",
        default="Simple",
        help='Filtra el historial por ResponseMode logueado. Pasar "" (vacío) para '
        "reissuar TODAS las preguntas históricas de la colección sin importar el modo "
        "con que se loguearon originalmente (necesario para wiki-solis/rag-engine, que "
        "casi no tienen historial logueado como Simple — Fase 2).",
    )
    parser.add_argument("--timeout", type=float, default=90.0)
    parser.add_argument(
        "--output", default=None, help="Ruta para volcar los pares pregunta/respuesta completos (revisión manual)"
    )
    args = parser.parse_args()
    response_mode_filter = args.response_mode or None

    entries = load_historical_questions(args.logs_dir, args.collection, response_mode_filter)
    if not entries:
        print(
            f"No se encontraron preguntas históricas para collection={args.collection} "
            f"responseMode={response_mode_filter!r} en {args.logs_dir}/rag-api-*.json",
            file=sys.stderr,
        )
        sys.exit(1)

    print(f"Reproduciendo {len(entries)} preguntas históricas contra {args.base_url} ...", file=sys.stderr)

    results = []
    violation_count = 0
    no_evidence_count = 0
    error_count = 0
    for i, entry in enumerate(entries, 1):
        query = entry["Query"]
        try:
            answer, sources_count = ask(args.base_url, entry, args.timeout)
        except (urllib.error.URLError, OSError, ValueError) as ex:
            error_count += 1
            print(f"[{i}/{len(entries)}] ERROR: {query[:70]!r} -> {ex}", file=sys.stderr)
            results.append({"query": query, "error": str(ex)})
            continue

        violations = find_violations(answer or "")
        no_evidence = sources_count == 0
        if no_evidence:
            no_evidence_count += 1
        status = "FAIL" if violations else "PASS"
        if violations:
            violation_count += 1
        print(
            f"[{i}/{len(entries)}] {status} ({len(violations)} violaciones"
            f"{', SIN EVIDENCIA' if no_evidence else ''}) — {query[:70]!r}",
            file=sys.stderr,
        )

        results.append({
            "query": query,
            "answer": answer,
            "violations": violations,
            "sources_count": sources_count,
            "no_evidence": no_evidence,
        })

    answered = len(entries) - error_count
    print("", file=sys.stderr)
    print(
        f"=== Resultado: {answered - violation_count}/{answered} respuestas sin violaciones "
        f"(criterio de la Fase 1: {answered}/{answered}); {no_evidence_count}/{answered} "
        f"cayeron en 'sin evidencia' (sources vacío); {error_count} errores de transporte ===",
        file=sys.stderr,
    )
    if violation_count:
        print(
            "Revisar el detalle de 'violations' en --output para decidir si son falsos "
            "negativos reales del filtro o preguntas que ya fallaban en modo Simple antes de Fase 1.",
            file=sys.stderr,
        )

    if args.output:
        with open(args.output, "w", encoding="utf-8") as f:
            json.dump(results, f, ensure_ascii=False, indent=2)
        print(
            f"Resultados completos guardados en {args.output} — revisar a mano las respuestas "
            "PASS para confirmar que el filtro no destrozó prosa legítima (falsos positivos).",
            file=sys.stderr,
        )

    sys.exit(1 if (violation_count or error_count) else 0)


if __name__ == "__main__":
    main()
