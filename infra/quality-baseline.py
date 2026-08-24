#!/usr/bin/env python3
"""
Línea base de CALIDAD DE RESPUESTA, no de recall.

Por qué existe: `rag eval` mide si el chunk correcto aparece en el top-K. Eso no
dice nada sobre lo que el usuario percibe: si la respuesta se inventa cosas fuera
del corpus, si es tan corta que no explica nada, o si sigue siendo técnica
cuando se pidió lenguaje simple. Esas tres cosas se venían juzgando a ojo.

Este script re-corre el bundle de preguntas reales de
`replicate-env/data/questions/*.json` (427 registros, 143 únicas tras deduplicar)
contra la API real y guarda las respuestas completas junto a métricas mecánicas.
La generación es la parte cara (~11 s por pregunta); las respuestas quedan
guardadas para que puntuarlas después —con otra heurística, con un juez LLM o a
mano— no cueste una segunda corrida.

Uso:
    python3 infra/quality-baseline.py --collection innovapp-docs --limit 20
    python3 infra/quality-baseline.py --all --out docs/eval/quality/baseline.json

Para un A/B de una opción de configuración, levantar una segunda API con la
variable de entorno correspondiente y pasar --base-url con su puerto:
    RagGeneration__EnableSimpleModeResumenContext=false \\
      dotnet run --project src/RagEngine.Api   # con ASPNETCORE_URLS en otro puerto
"""
from __future__ import annotations

import argparse, glob, json, os, re, statistics, sys, time
import urllib.request

# ── Heurísticas de "sigue siendo técnico" ────────────────────────────────────
# Mismas formas que persigue SimpleAnswerSanitizer.Sanitize. Si algo
# de esto sobrevive en una respuesta de modo Simple, el filtro no alcanzó.
PATRONES_TECNICOS = {
    "bloque_cercado":   re.compile(r"```"),
    "span_backtick":    re.compile(r"`[^`\n]+`"),
    "atributo":         re.compile(r"\[[A-Z][A-Za-z0-9]*(\(|\])"),
    "dotted_pascal":    re.compile(r"\b[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*){1,}\b"),
    "snake_case":       re.compile(r"\b[a-z][a-z0-9]*(?:_[a-z0-9]+){1,}\b"),
    "pascal_compuesto": re.compile(r"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*){1,}\b"),
}

# Tres estados distintos, no dos. La distincion importa: el motor casi nunca
# fabrica, pero su banda baja de confianza avisa que no hay coincidencia clara y
# ACTO SEGUIDO entrega contenido igual. Eso se lee como "responde fuera del
# corpus" aunque tecnicamente sea un aviso honesto, y es la categoria que hay que
# contar aparte para poder ajustarla.
#
#   RECHAZO_PLENO  declina y no entrega contenido del corpus
#   BANDA_BAJA     avisa que la coincidencia es debil y aun asi relata contenido
#   DIRECTO        responde sin reservas
MARCAS_RECHAZO_PLENO = [
    "no tengo la capacidad", "no tengo acceso", "no dispongo",
    "no puedo responder", "no tengo información", "no tengo informacion",
    "fuera del alcance", "no forma parte del corpus", "solo puedo responder",
    # Formas que produce el prompt tras el ajuste del 2026-08-21. Sin estas, un
    # rechazo correcto quedaba contado como respuesta directa, y la metrica
    # reportaba una regresion de +10 puntos donde en realidad habia una mejora.
    "no tiene contenido relevante", "no hay contenido relevante",
    "no hay nada relevante", "no tiene información relevante",
    "no tiene informacion relevante",
]
# Solo la plantilla REAL de la banda baja. Las marcas genericas ("no encontre",
# "no aparece en") producian falsos positivos: una respuesta legitima que explica
# "si no se encuentra en las variables de entorno, entonces..." quedaba clasificada
# como banda baja aunque no tuviera ninguna reserva. Se prefiere perder algun caso
# antes que inflar la metrica que se va a usar para decidir.
MARCAS_BANDA_BAJA = [
    "no encontré una coincidencia clara", "no encontre una coincidencia clara",
    "el fragmento más cercano", "el fragmento mas cercano",
    "no encontré una coincidencia exacta", "no encontre una coincidencia exacta",
]

# Léxico de exceso de certeza y de vaguedad. No pretende ser un juez: son
# contadores que señalan candidatos para revisión manual.
LEXICO_OPTIMISTA = ["sin duda", "siempre", "nunca", "garantiza", "asegura que",
                    "definitivamente", "por supuesto", "obviamente"]
LEXICO_VAGO = ["depende", "en general", "podría", "podria", "posiblemente",
               "probablemente", "suele", "normalmente", "algunos casos",
               "puede variar", "entre otros"]


def cargar_bundle(directorio: str) -> dict[str, list[dict]]:
    """Preguntas únicas por colección, preservando los parámetros de la primera
    aparición: interesa reproducir cómo se preguntó de verdad, no un default."""
    por_coleccion: dict[str, list[dict]] = {}
    for ruta in sorted(glob.glob(os.path.join(directorio, "*.json"))):
        coleccion = os.path.basename(ruta).replace(".json", "")
        vistas, unicas = set(), []
        for item in json.load(open(ruta, encoding="utf-8")):
            q = (item.get("query") or "").strip()
            if not q or q in vistas:
                continue
            vistas.add(q)
            unicas.append(item)
        if unicas:
            por_coleccion[coleccion] = unicas
    return por_coleccion


def preguntar(base_url: str, coleccion: str, item: dict, timeout: int) -> dict:
    cuerpo = {
        "query": item["query"],
        "collection": coleccion,
        "topK": item.get("topK") or 10,
        "minScore": item.get("minScore") if item.get("minScore") is not None else 0.10,
        "rerank": item.get("rerank") if item.get("rerank") is not None else True,
    }
    modo = item.get("_forzar_modo") or item.get("responseMode")
    if modo:
        cuerpo["responseMode"] = modo

    req = urllib.request.Request(
        f"{base_url}/api/ask",
        data=json.dumps(cuerpo).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST")
    inicio = time.monotonic()
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        datos = json.load(resp)
    return {"peticion": cuerpo, "respuesta": datos,
            "duracion_s": round(time.monotonic() - inicio, 2)}


def medir(registro: dict) -> dict:
    respuesta = (registro["respuesta"].get("answer") or "")
    fuentes = registro["respuesta"].get("sources") or []
    bajo = respuesta.lower()
    modo = registro["peticion"].get("responseMode") or "Technical"

    tecnicos = {k: len(p.findall(respuesta)) for k, p in PATRONES_TECNICOS.items()}
    scores = [s.get("score") or 0.0 for s in fuentes]

    rechazo_pleno = any(m in bajo for m in MARCAS_RECHAZO_PLENO)
    banda_baja = (not rechazo_pleno) and any(m in bajo for m in MARCAS_BANDA_BAJA)
    postura = "rechazo_pleno" if rechazo_pleno else ("banda_baja" if banda_baja else "directo")

    return {
        "modo": modo,
        "caracteres": len(respuesta),
        "palabras": len(respuesta.split()),
        "postura": postura,
        "fuentes": len(fuentes),
        "score_top": round(max(scores), 4) if scores else None,
        "tecnicos_total": sum(tecnicos.values()),
        "tecnicos": {k: v for k, v in tecnicos.items() if v},
        "optimista": sum(bajo.count(t) for t in LEXICO_OPTIMISTA),
        "vago": sum(bajo.count(t) for t in LEXICO_VAGO),
        "duracion_s": registro["duracion_s"],
    }


def resumir(filas: list[dict]) -> dict:
    def prom(clave):
        vals = [f["metricas"][clave] for f in filas if f["metricas"].get(clave) is not None]
        return round(statistics.mean(vals), 1) if vals else None

    def mediana(clave):
        vals = [f["metricas"][clave] for f in filas if f["metricas"].get(clave) is not None]
        return round(statistics.median(vals), 1) if vals else None

    total = len(filas)
    simples = [f for f in filas if f["metricas"]["modo"] == "Simple"]
    sucias = [f for f in simples if f["metricas"]["tecnicos_total"] > 0]
    cortas = [f for f in filas if f["metricas"]["palabras"] < 40]

    def con_postura(p):
        return [f for f in filas if f["metricas"]["postura"] == p]

    # Contestar sin reservas con evidencia debil es el proxy mas cercano a
    # "responde fuera del corpus": ni rechazo, ni aviso de banda baja.
    riesgo = [f for f in con_postura("directo")
              if (f["metricas"]["score_top"] or 0) < 0.60]

    return {
        "preguntas": total,
        "palabras_promedio": prom("palabras"),
        "palabras_mediana": mediana("palabras"),
        "respuestas_cortas_pct": round(100 * len(cortas) / total, 1) if total else 0,
        "rechazo_pleno_pct": round(100 * len(con_postura("rechazo_pleno")) / total, 1) if total else 0,
        "banda_baja_pct": round(100 * len(con_postura("banda_baja")) / total, 1) if total else 0,
        "directo_pct": round(100 * len(con_postura("directo")) / total, 1) if total else 0,
        "directo_con_score_bajo_pct": round(100 * len(riesgo) / total, 1) if total else 0,
        "simple_con_tecnicismos_pct": round(100 * len(sucias) / len(simples), 1) if simples else None,
        "simple_total": len(simples),
        "optimista_promedio": prom("optimista"),
        "vago_promedio": prom("vago"),
        "duracion_promedio_s": prom("duracion_s"),
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base-url", default="http://localhost:5080")
    ap.add_argument("--questions-dir", default="replicate-env/data/questions")
    ap.add_argument("--collection", action="append",
                    help="colección a correr (repetible). Por defecto, todas las que tengan preguntas.")
    ap.add_argument("--limit", type=int, help="máximo de preguntas por colección")
    ap.add_argument("--timeout", type=int, default=180)
    ap.add_argument("--force-mode", choices=["Simple", "Technical"],
                    help="fuerza el responseMode en vez de usar el registrado en el bundle")
    ap.add_argument("--etiqueta", default="baseline", help="nombre de esta corrida, va al archivo de salida")
    ap.add_argument("--out", help="ruta del JSON de salida")
    args = ap.parse_args()

    bundle = cargar_bundle(args.questions_dir)
    if args.collection:
        bundle = {k: v for k, v in bundle.items() if k in set(args.collection)}
    if not bundle:
        print("no hay preguntas que correr", file=sys.stderr)
        return 1

    filas: list[dict] = []
    for coleccion, items in bundle.items():
        seleccion = items[: args.limit] if args.limit else items
        print(f"\n=== {coleccion}: {len(seleccion)} preguntas ===", file=sys.stderr)
        for i, item in enumerate(seleccion, 1):
            if args.force_mode:
                item = {**item, "_forzar_modo": args.force_mode}
            try:
                registro = preguntar(args.base_url, coleccion, item, args.timeout)
            except Exception as ex:
                print(f"  [{i}/{len(seleccion)}] ERROR: {ex}", file=sys.stderr)
                continue
            fila = {"coleccion": coleccion, "pregunta": item["query"],
                    "respuesta": registro["respuesta"].get("answer"),
                    "metricas": medir(registro)}
            filas.append(fila)
            m = fila["metricas"]
            print(f"  [{i}/{len(seleccion)}] {m['palabras']:>4}p "
                  f"tec={m['tecnicos_total']:>2} {m['postura'][:12]:<12} "
                  f"score={m['score_top']} {item['query'][:52]}", file=sys.stderr)

    resumen = resumir(filas)
    salida = {"etiqueta": args.etiqueta, "base_url": args.base_url,
              "resumen": resumen, "detalle": filas}

    destino = args.out or f"docs/eval/quality/{args.etiqueta}.json"
    os.makedirs(os.path.dirname(destino), exist_ok=True)
    with open(destino, "w", encoding="utf-8") as fh:
        json.dump(salida, fh, ensure_ascii=False, indent=1)

    print("\n" + json.dumps(resumen, ensure_ascii=False, indent=1))
    print(f"\nrespuestas completas en: {destino}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
