"""Verificador de la Tool de Open WebUI (ficha 24.1). Sale != 0 ante cualquier fallo o cero casos."""

import argparse
import importlib.util
import json
import pathlib
import re
import sys
import urllib.request

HERE = pathlib.Path(__file__).parent

# El criterio literal de 24.1 es "no producen respuesta afirmativa inventada (se
# respeta el gate de confianza de la API)", no "cero fuentes": /api/ask siempre
# devuelve el top-K recuperado como contexto aunque el LLM decline honestamente
# (confirmado 2026-10-13: la query ES fuera de corpus trae 10 fuentes de bajo
# score pero el texto de la respuesta declina sin inventar datos; la EN si trae
# cero fuentes para el mismo tipo de pregunta). Por eso el oraculo verifica el
# texto de la respuesta, no solo el conteo de fuentes.
_DECLINE_PATTERN = re.compile(
    r"no (puedo|pude|hay|existe|tengo|encontr\w*)|no contiene informaci|lo siento"
    r"|cannot find|could not find|no information|not (contain|have) (enough|sufficient)"
    r"|i don'?t have",
    re.IGNORECASE,
)


def _declines_honestly(body: dict) -> bool:
    """Sin fuentes es declinar; con fuentes, el texto debe declinar explicitamente."""
    if len(body["sources"]) == 0:
        return True
    return bool(_DECLINE_PATTERN.search(body["answer"]))


def load_tool():
    spec = importlib.util.spec_from_file_location("rag_engine_tool", HERE / "rag_engine_tool.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--collection", required=True)
    parser.add_argument("--fixture", required=True)
    parser.add_argument("--api-url", required=True, help="API a verificar; sin valor por defecto para no apuntar a la maquina local por accidente")
    parser.add_argument("--timeout", type=float, default=300.0)
    args = parser.parse_args()

    fixture_path = pathlib.Path(args.fixture)
    if not fixture_path.is_file():
        print(f"FALLO: no existe el fixture {fixture_path}")
        return 2
    cases = json.loads(fixture_path.read_text(encoding="utf-8"))
    tool = load_tool()
    results = []

    def record(case_id, ok, detail):
        results.append(ok)
        print(f"{'OK   ' if ok else 'FALLO'} {case_id}: {detail}")

    try:
        with urllib.request.urlopen(args.api_url.rstrip("/") + "/api/collections", timeout=30) as r:
            available = json.loads(r.read().decode("utf-8")).get("collections", [])
    except Exception as ex:
        print(f"FALLO: API no disponible en {args.api_url}: {ex}")
        return 2
    if args.collection not in available:
        print(f"FALLO: la coleccion {args.collection} no existe en la API")
        return 2

    for case in cases["in_corpus"]:
        try:
            body = tool.rag_ask(args.api_url, args.collection, case["query"], args.timeout)
            cited = [s.get("file") or "" for s in body["sources"]]
            ok = bool(body["answer"].strip()) and any(case["expected_file_contains"] in f for f in cited)
            record(case["id"], ok, f"citas={len(cited)} esperado~{case['expected_file_contains']}")
        except tool.RagToolError as ex:
            record(case["id"], False, str(ex))

    for case in cases["out_of_corpus"]:
        try:
            body = tool.rag_ask(args.api_url, args.collection, case["query"], args.timeout)
            ok = _declines_honestly(body)
            record(
                case["id"], ok,
                f"fuentes={len(body['sources'])}, declina={'si' if ok else 'no'}: {body['answer'][:80]!r}",
            )
        except tool.RagToolError as ex:
            record(case["id"], False, str(ex))

    try:
        tool.rag_ask(args.api_url, cases["errors"]["missing_collection"], "hola", args.timeout)
        record("ERR-COLECCION", False, "coleccion inexistente no produjo error")
    except tool.RagToolError as ex:
        record("ERR-COLECCION", True, f"error explicito: {ex}")

    try:
        tool.rag_ask("http://127.0.0.1:9", args.collection, "hola", 5)
        record("ERR-API-CAIDA", False, "API caida no produjo error")
    except tool.RagToolError as ex:
        record("ERR-API-CAIDA", True, f"error explicito: {ex}")

    if not results:
        print("FALLO: cero casos ejecutados")
        return 2
    print(f"{sum(results)}/{len(results)} casos correctos")
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
