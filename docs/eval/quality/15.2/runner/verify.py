#!/usr/bin/env python3
"""Puerta de aceptacion del item 15.2.2.

Reejecuta la comprobacion real del experimento y FALLA si falta evidencia, si el
comparador no la acepta como completa, si el contrato del grafo no se ejecuto, o si la
conclusion archivada no coincide con la que sale de recalcularla. Una omision nunca sale
por aqui como exito.

No necesita Qdrant: el juicio se recalcula sobre la evidencia persistida. Si necesita el
corpus, y falta, falla — verificar el instrumento sin fuente seria verificar nada.
"""
import gzip
import hashlib
import json
from pathlib import Path
import subprocess
import sys

BUNDLE = Path(__file__).resolve().parent.parent
ROOT = BUNDLE.parents[3]
PHASE2 = BUNDLE / "phase2"
EVIDENCE = PHASE2 / "experiment-2026-09-22.json.gz"
VERDICT = PHASE2 / "verdict-2026-09-22.json"
CORPUS = ROOT.parent / "BusinessSuite.Xaf"

# Conclusion registrada en el ledger. Si una reejecucion diera otra cosa, esta puerta debe
# ponerse roja en vez de dejar que el resultado del item envejezca en silencio.
EXPECTED_STATUS = "complete_no_promotion"
EXPECTED_ELIGIBLE = []
EXPECTED_PRECISION = {"name-join": 5, "syntax": 17, "semantic": 22, "graph": 22}
EXPECTED_NET_GAIN = {"name-join": 0, "syntax": 0, "semantic": 2, "graph": 2}


def fail(message):
    print(f"FALLO 15.2.2: {message}", file=sys.stderr)
    sys.exit(1)


def run(*arguments):
    return subprocess.run([sys.executable, str(BUNDLE / "accept.py"), *arguments],
                          capture_output=True, text=True,
                          env={"PYTHONDONTWRITEBYTECODE": "1", "PATH": "/usr/bin:/bin"})


def main():
    if not CORPUS.is_dir():
        fail(f"corpus ausente en {CORPUS}; sin fuente no hay verificacion del instrumento")
    if not EVIDENCE.is_file():
        fail(f"evidencia ausente: {EVIDENCE}")
    if not VERDICT.is_file():
        fail(f"veredicto archivado ausente: {VERDICT}")

    instrument = run("--verify-instrument", "--corpus", str(CORPUS))
    if instrument.returncode != 0:
        fail(f"instrumento congelado no verifica: {instrument.stderr.strip()}")
    instrument_report = json.loads(instrument.stdout)
    if instrument_report.get("skipped"):
        fail(f"{instrument_report['skipped']} pruebas omitidas del instrumento")

    experiment = run("--experiment", str(EVIDENCE))
    if experiment.returncode != 0:
        fail(f"el comparador rechaza la evidencia: {experiment.stderr.strip()}")
    report = json.loads(experiment.stdout)

    if report["status"] != EXPECTED_STATUS:
        fail(f"status {report['status']} != {EXPECTED_STATUS}")
    if report["eligible"] != EXPECTED_ELIGIBLE:
        fail(f"elegibles {report['eligible']} != {EXPECTED_ELIGIBLE}")

    summaries = report["summaries"]
    for arm, expected in EXPECTED_PRECISION.items():
        actual = summaries[arm]["precision"]["correct"]
        if actual != expected:
            fail(f"precision de {arm}: {actual}/30, registrado {expected}/30")
    for arm, expected in EXPECTED_NET_GAIN.items():
        actual = summaries[arm]["jump_net_gain"]
        if actual != expected:
            fail(f"ganancia neta de salto de {arm}: {actual}, registrado {expected}")

    # El contrato del grafo se EJECUTO y salio limpio: su no-promocion es por metricas, no
    # por evidencia ausente. Sin observaciones, no se puede afirmar ninguna de las dos cosas.
    evidence = json.loads(gzip.decompress(EVIDENCE.read_bytes()))
    if "graph_execution" not in evidence:
        fail("sin ejecucion del contrato del grafo: el resultado nulo quedaria sin cubrir el grafo")
    scenarios = {s["id"] for s in json.loads((BUNDLE / "graph-fixtures.json").read_text())["scenarios"]}
    if set(evidence["graph_execution"]["observations"]) != scenarios:
        fail("faltan escenarios del contrato del grafo")
    if summaries["graph"]["graph_contract_errors"]:
        fail(f"contrato del grafo con errores: {summaries['graph']['graph_contract_errors']}")

    if len(evidence["runs"]) != 2520:
        fail(f"cobertura incompleta: {len(evidence['runs'])} llamadas, se exigen 2520")

    archived = json.loads(VERDICT.read_text())
    if json.dumps(archived["summaries"], sort_keys=True) != json.dumps(summaries, sort_keys=True):
        fail("el veredicto archivado no coincide con el recalculado sobre la evidencia")

    print(json.dumps({
        "item": "15.2.2-comparar-alternativas-locales",
        "status": report["status"],
        "eligible": report["eligible"],
        "runs": len(evidence["runs"]),
        "instrument_tests": instrument_report["tests_run"],
        "graph_contract_errors": summaries["graph"]["graph_contract_errors"],
        "evidence_sha256": hashlib.sha256(gzip.decompress(EVIDENCE.read_bytes())).hexdigest(),
        "precision": {a: summaries[a]["precision"]["correct"] for a in EXPECTED_PRECISION},
        "jump_net_gain": {a: summaries[a]["jump_net_gain"] for a in EXPECTED_NET_GAIN},
        "p95_delta_ms": {a: round(summaries[a]["p95_delta_ms"], 2) for a in summaries},
        "meaning": "Experimento completo sin alternativa elegible. No es un build verde: "
                   "recalcula precision, recall, latencia y contrato sobre la evidencia medida.",
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
