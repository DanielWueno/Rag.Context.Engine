#!/usr/bin/env python3
"""
Calibración de las bandas del gate de confianza (ítem 4.3 del plan).

Qué hace: corre el conjunto etiquetado de `docs/eval/gate-bandas.labeled-set.json`
—cada consulta marcada como "el corpus SÍ contiene la respuesta" / "no la contiene",
con negativos adversariales— contra el CLI, registra el score del resultado #1 con
`CrossEncoder:StableGateScore` encendida y apagada, y barre los pares
(LowConfidenceThreshold, HighConfidenceThreshold) sobre esa distribución.

Por qué el CLI y no la API: el score del #1 es lo único que se necesita y el CLI lo
devuelve sin pagar la generación (~11 s por pregunta). El efecto de los umbrales sobre
la respuesta que ve el usuario se mide aparte con `infra/quality-baseline.py`.

Definiciones del barrido, que son las del criterio del ítem:
  respuesta correcta entregada = positivo con score >= alto, es decir respondido SIN el
      matiz de banda media. El matiz es justamente lo que convierte una respuesta buena
      en inservible (ver el hallazgo de origen), así que contarlo como entrega sería
      contar el problema como solución.
  fabricación = ausente con score >= alto: se responde sin reservas algo que el corpus
      no contiene.

Uso:
    python3 infra/gate-bandas-barrido.py --medir --salida NUEVA.json
    python3 infra/gate-bandas-barrido.py              # sólo re-analiza lo ya medido
    python3 infra/gate-bandas-barrido.py --comparar CONTROL.json CANDIDATO.json

El análisis histórico usa proxies de score, NO etiquetas reales de respuesta.
--comparar exige respuestas adjudicadas y procedencia completa; ver docs/eval/quality/README.md.
"""
from __future__ import annotations

import argparse, itertools, json, os, statistics, subprocess, sys, time
from pathlib import Path
from gate_calibration import compare_files, sha256, validate_identity

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI = os.path.join(REPO, "src/RagEngine.Cli/bin/Release/net10.0/RagEngine.Cli.dll")
ETIQUETADO = os.path.join(REPO, "docs/eval/gate-bandas.labeled-set.json")
MEDICIONES = os.path.join(REPO, "docs/eval/gate-bandas.scores.json")


# ── Medición ────────────────────────────────────────────────────────────────
def buscar(pregunta: str, coleccion: str, estable: bool, k: int = 10) -> dict:
    env = dict(os.environ, CrossEncoder__StableGateScore="true" if estable else "false")
    p = subprocess.run(
        ["dotnet", CLI, "search", pregunta, "-c", coleccion, "-k", str(k),
         "-s", "0.10", "-r", "-o", "json"],
        capture_output=True, text=True, cwd=REPO, env=env, timeout=180)
    # El CLI imprime un banner antes del JSON; el array empieza en el primer "[\n".
    if p.returncode != 0:
        raise ValueError(f"CLI failed ({p.returncode}) for {coleccion}: {p.stderr}\n{p.stdout}")
    i = p.stdout.find("[\n")
    if i < 0 and p.stdout.rstrip().endswith("[]"):
        return {"score": 0.0, "resultados": 0, "retrieval_succeeded": True}
    if i < 0:
        raise ValueError(f"Missing CLI JSON for {coleccion}: {p.stdout}")
    datos = json.loads(p.stdout[i:])
    if not datos:
        return {"score": 0.0, "resultados": 0, "retrieval_succeeded": True}
    identity = datos[0].get("cross_encoder")
    validate_identity(identity)
    if identity["stable_gate_score"] != estable:
        raise ValueError("CLI ignored CrossEncoder__StableGateScore")
    md = datos[0].get("metadata") or {}
    return {"score": datos[0]["similarity_score"], "cross_encoder": identity,
            "retrieval_succeeded": True,
            "chunk": datos[0]["content_hash"][:12],
            "seccion": md.get("method_name"),
            "archivo": md.get("relative_file_path"),
            "resultados": len(datos)}


def medir(labeled_path: str, output: str) -> list[dict]:
    labeled_bytes = Path(labeled_path).read_bytes()
    filas = json.loads(labeled_bytes)
    salida, t0 = [], time.monotonic()
    for i, f in enumerate(filas, 1):
        fila = dict(f)
        fila["estable"] = buscar(f["pregunta"], f["coleccion"], estable=True)
        fila["lotes"] = buscar(f["pregunta"], f["coleccion"], estable=False)
        e = (fila["estable"] or {}).get("score")
        print(f"[{i:2}/{len(filas)}] {f['etiqueta']:<8} {f['categoria']:<18} "
              f"estable={e if e is None else round(e, 4):<8} {f['pregunta'][:52]}",
              file=sys.stderr, flush=True)
        salida.append(fila)
    # Never overwrite the historical control or pretend scores adjudicate answers.
    with open(output, "x", encoding="utf-8") as stream:
        json.dump({"schema_version": 1, "kind": "scores_only",
                   "labeled_set_sha256": sha256(labeled_bytes), "rows": salida},
                  stream, ensure_ascii=False, indent=1)
    print(f"\n{len(salida)} consultas en {time.monotonic() - t0:.0f}s → {output}",
          file=sys.stderr)
    return salida


# ── Análisis ────────────────────────────────────────────────────────────────
def sc(f, modo):
    return (f[modo] or {}).get("score", 0.0)


def evaluar(pos, neg, bajo, alto, modo):
    return {
        "bajo": bajo, "alto": alto,
        "entregadas": sum(1 for f in pos if sc(f, modo) >= alto),
        "fabricaciones": sum(1 for f in neg if sc(f, modo) >= alto),
        "matiz_pos": sum(1 for f in pos if bajo <= sc(f, modo) < alto),
        "matiz_neg": sum(1 for f in neg if bajo <= sc(f, modo) < alto),
        "positivos_perdidos": sum(1 for f in pos if sc(f, modo) < bajo),
        "negativos_rechazados": sum(1 for f in neg if sc(f, modo) < bajo),
    }


def auc(pos, neg, modo):
    p = [sc(f, modo) for f in pos]
    n = [sc(f, modo) for f in neg]
    ganadas = sum(1 if a > b else 0.5 if a == b else 0 for a, b in itertools.product(p, n))
    return ganadas / (len(p) * len(n))


def analizar(filas, bajo_actual=0.05, alto_actual=0.60):
    pos = [f for f in filas if f["etiqueta"] == "presente"]
    neg = [f for f in filas if f["etiqueta"] == "ausente"]
    print(f"positivos={len(pos)}  negativos={len(neg)}\n")

    for modo in ("lotes", "estable"):
        print(f"── distribución {modo} — AUC={auc(pos, neg, modo):.3f}")
        for nombre, grupo in (("presente", pos), ("ausente", neg)):
            v = sorted(sc(f, modo) for f in grupo)
            print(f"   {nombre:<9} min={v[0]:.4f} mediana={statistics.median(v):.4f} max={v[-1]:.4f}")
        solape_min = min(sc(f, modo) for f in pos)
        solape_max = max(sc(f, modo) for f in neg)
        dentro_p = sum(1 for f in pos if solape_min <= sc(f, modo) <= solape_max)
        dentro_n = sum(1 for f in neg if solape_min <= sc(f, modo) <= solape_max)
        print(f"   solape [{solape_min:.4f}, {solape_max:.4f}]: {dentro_p}/{len(pos)} positivos "
              f"y {dentro_n}/{len(neg)} negativos conviven en él\n")

    base = evaluar(pos, neg, bajo_actual, alto_actual, "lotes")
    print(f"LÍNEA BASE {bajo_actual}/{alto_actual} sobre score por lotes: {base}\n")

    print("── curva del umbral alto sobre el score estable, con el bajo fijo en 0,05")
    print(f"{'alto':>6} | {'entregadas':>10} {'fabricac':>9} | {'matizPOS':>8} {'matizNEG':>8}")
    for alto in (0.30, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.68, 0.70, 0.75, 0.80, 0.90):
        r = evaluar(pos, neg, 0.05, alto, "estable")
        print(f"{alto:>6.2f} | {r['entregadas']:>10} {r['fabricaciones']:>9} | "
              f"{r['matiz_pos']:>8} {r['matiz_neg']:>8}")

    print("\n── efecto de mover sólo el umbral bajo (score estable)")
    print(f"{'bajo':>6} | {'positivos perdidos':>18} {'negativos rechazados':>21}")
    for bajo in (0.0, 0.01, 0.02, 0.03, 0.05, 0.08, 0.10, 0.15, 0.20):
        print(f"{bajo:>6.2f} | {sum(1 for f in pos if sc(f,'estable') < bajo):>18} "
              f"{sum(1 for f in neg if sc(f,'estable') < bajo):>21}")

    print("\n── pares que cumplen el criterio del ítem "
          "(más entregadas que la línea base, sin más fabricaciones)")
    cumplen = []
    for bajo, alto in itertools.product(
            (0.0, 0.01, 0.02, 0.03, 0.04, 0.05, 0.06, 0.08, 0.10, 0.15, 0.20),
            (0.30, 0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.90)):
        if bajo >= alto:
            continue
        r = evaluar(pos, neg, bajo, alto, "estable")
        if (r["entregadas"] > base["entregadas"]
                and r["fabricaciones"] <= base["fabricaciones"]):
            cumplen.append(r)
    print(f"   {len(cumplen)}")
    for r in cumplen:
        print("   ", r)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--medir", action="store_true",
                    help="vuelve a correr el CLI en vez de leer la medición guardada")
    ap.add_argument("--labeled-set", default=ETIQUETADO)
    ap.add_argument("--scores", default=MEDICIONES, help="mediciones a reanalizar")
    ap.add_argument("--salida", help="archivo NUEVO para --medir o informe A/B")
    ap.add_argument("--comparar", nargs=2, metavar=("CONTROL", "CANDIDATO"),
                    help="compara scores y respuestas adjudicadas; falla ante regresion/evidencia incompleta")
    ap.add_argument("--fixtures", action="store_true", help="solo probar contrato, NO calibrar un binario")
    args = ap.parse_args()
    if args.comparar:
        if args.medir:
            ap.error("--comparar no admite --medir")
        report = compare_files(args.labeled_set, *args.comparar, allow_fixtures=args.fixtures)
        text = json.dumps(report, ensure_ascii=False, indent=2)
        if args.salida:
            with open(args.salida, "x", encoding="utf-8") as output:
                output.write(text + "\n")
        print(text)
        return 0 if report["accepted"] else 1
    if args.medir:
        if not args.salida or os.path.exists(args.salida):
            ap.error("--medir exige --salida con una ruta nueva; conservar ambos barridos")
        filas = medir(args.labeled_set, args.salida)
    else:
        if not os.path.exists(args.scores):
            print(f"no existe {args.scores}; corre con --medir --salida NUEVA", file=sys.stderr)
            return 1
        filas = json.load(open(args.scores, encoding="utf-8"))
        if isinstance(filas, dict):
            if filas.get("labeled_set_sha256") != sha256(Path(args.labeled_set).read_bytes()):
                raise ValueError("Labeled-set changed since measurement")
            filas = filas["rows"]
    analizar(filas)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, KeyError, TypeError, OSError, subprocess.SubprocessError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(1)
