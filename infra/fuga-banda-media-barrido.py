#!/usr/bin/env python3
"""
Fuga del ejemplo negativo en la banda media del gate (ítem 4.4 del plan).

Qué mide: si la respuesta que ve el usuario contiene la frase que
`LowConfidencePrompt.Addendum` llevaba como ejemplo de "lo que NO hay que hacer" y
que el modelo copiaba literalmente — "...pero el fragmento más cercano dice que el
contexto proporcionado no tiene una relevancia alta para la pregunta".

Por qué este instrumento y no `infra/quality-baseline.py`: aquel corre el bundle de
preguntas reales sin distinguir bandas, y la fuga sólo puede ocurrir en la banda
media, que es la única que recibe el addendum. Aquí las consultas se seleccionan por
la banda que les da su score estable ya medido en `docs/eval/gate-bandas.scores.json`
(ítem 4.3), así que 22 de las 65 caen en banda media sin gastar una medición nueva.

Por qué el CLI y no la API: `rag ask --rerank --no-stream` recorre el mismo
ConfidenceGate y el mismo SystemPromptComposer, y no obliga a levantar la API ni a
reiniciarla entre variantes del prompt.

Uso:
    python3 infra/fuga-banda-media-barrido.py --medir media --out docs/eval/quality/4.4-antes-media.json
    python3 infra/fuga-banda-media-barrido.py --medir media,alta,sin-grounding --out docs/eval/quality/4.4-despues-todas.json
    python3 infra/fuga-banda-media-barrido.py --analizar docs/eval/quality/4.4-*.json
"""
from __future__ import annotations

import argparse, json, os, re, subprocess, sys, time, unicodedata

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI = os.path.join(REPO, "src/RagEngine.Cli/bin/Release/net10.0/RagEngine.Cli.dll")
SCORES = os.path.join(REPO, "docs/eval/gate-bandas.scores.json")

# Los mismos umbrales que RagGeneration en appsettings; si se recalibran, aquí también.
LOW, HIGH = 0.05, 0.60

ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")


def normalizar(t: str) -> str:
    t = unicodedata.normalize("NFKD", t.lower())
    t = "".join(c for c in t if not unicodedata.combining(c))
    return re.sub(r"\s+", " ", t)


# La frase fugada, literal, y la forma general de la que era un caso: relevar un
# enunciado SOBRE el contexto en vez de contenido. La segunda es la que importa —
# quitar la cita del prompt sin quitar la forma no arreglaría nada.
FUGA_LITERAL = normalizar(
    "el fragmento mas cercano dice que el contexto proporcionado no tiene una "
    "relevancia alta para la pregunta")
FUGA_FORMA = re.compile(normalizar(
    r"fragmento mas cercano dice que el contexto"
    r"|contexto proporcionado no tiene"
    r"|no tiene una relevancia alta"
    r"|contexto (proporcionado )?(no )?(es|tiene) (poco |muy )?relevan"))


def banda(score: float) -> str:
    return "sin-grounding" if score < LOW else ("media" if score < HIGH else "alta")


def desempaquetar(salida: str) -> str:
    """Extrae el texto del panel de Spectre y rejunta las líneas envueltas."""
    limpio = ANSI.sub("", salida)
    lineas, dentro = [], False
    for l in limpio.splitlines():
        s = l.strip()
        if s.startswith("╭"):
            dentro = True
        elif s.startswith("╰"):
            dentro = False
        elif dentro and s.startswith("│"):
            lineas.append(s.strip("│").rstrip())
    if not lineas:
        return limpio          # camino sin panel (mensaje fijo o error)
    parrafos, actual = [], []
    for l in lineas:
        if not l.strip():
            if actual:
                parrafos.append(" ".join(x.strip() for x in actual))
                actual = []
            parrafos.append("")
        elif re.match(r"^\s*(\d+\.|[-*•])\s", l):
            if actual:
                parrafos.append(" ".join(x.strip() for x in actual))
            actual = [l]
        else:
            actual.append(l)
    if actual:
        parrafos.append(" ".join(x.strip() for x in actual))
    return "\n".join(parrafos).strip()


def medir(bandas: set[str], destino: str) -> None:
    consultas = [c for c in json.load(open(SCORES, encoding="utf-8"))
                 if banda(c["estable"]["score"]) in bandas]
    print(f"{len(consultas)} consultas en bandas {sorted(bandas)}", file=sys.stderr)
    salida = []
    for i, c in enumerate(consultas, 1):
        t0 = time.time()
        p = subprocess.run(
            ["dotnet", CLI, "ask", c["pregunta"], "-c", c["coleccion"],
             "-k", "10", "--rerank", "--no-stream"],
            capture_output=True, text=True, cwd=REPO, timeout=300)
        respuesta = desempaquetar(p.stdout)
        salida.append({
            "pregunta": c["pregunta"], "coleccion": c["coleccion"],
            "etiqueta_corpus": c["etiqueta"], "categoria": c["categoria"],
            "score_estable": c["estable"]["score"],
            "banda": banda(c["estable"]["score"]),
            "respuesta": respuesta, "segundos": round(time.time() - t0, 1),
            "returncode": p.returncode,
        })
        print(f"[{i}/{len(consultas)}] {salida[-1]['banda']:13s} "
              f"{c['estable']['score']:.3f} {c['pregunta'][:52]!r} "
              f"-> {len(respuesta)} chars {salida[-1]['segundos']}s", file=sys.stderr)
    json.dump(salida, open(destino, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print("escrito", destino, file=sys.stderr)


def analizar(rutas: list[str]) -> int:
    fugas_totales = 0
    for ruta in rutas:
        datos = json.load(open(ruta, encoding="utf-8"))
        print(f"\n── {os.path.basename(ruta)}")
        for b in ("media", "alta", "sin-grounding"):
            xs = [x for x in datos if x["banda"] == b]
            if not xs:
                continue
            lit = [x for x in xs if FUGA_LITERAL in normalizar(x["respuesta"])]
            forma = [x for x in xs if FUGA_FORMA.search(normalizar(x["respuesta"]))]
            fugas_totales += len(forma)
            print(f"   {b:14s} n={len(xs):3d}  fuga_literal={len(lit):3d}  "
                  f"forma_meta_contexto={len(forma):3d}")
            for x in forma:
                print(f"      * {x['score_estable']:.3f} {x['pregunta'][:60]}")
    return fugas_totales


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--medir", help="bandas a medir, separadas por coma")
    ap.add_argument("--out", help="destino del JSON de la medición")
    ap.add_argument("--analizar", nargs="*", default=[], help="JSON(s) ya medidos")
    a = ap.parse_args()
    if a.medir:
        if not a.out:
            ap.error("--medir necesita --out")
        medir(set(a.medir.split(",")), a.out)
    if a.analizar:
        # Sale 1 si queda alguna fuga: sirve como puerta en el criterio del ítem.
        return 1 if analizar(a.analizar) else 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
