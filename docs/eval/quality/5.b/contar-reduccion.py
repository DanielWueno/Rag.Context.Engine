#!/usr/bin/env python3
"""
Ítem 5.b — cuenta, sobre una colección Qdrant YA ingestada (modo por chunk), cuántas
llamadas al LLM costó realmente la Fase 2 (chunks con resumen_pending=false, es decir,
que pasaron por Ollama o por el centinela SIN_CONTENIDO_DE_NEGOCIO) frente a cuántas
costaría bajo la granularidad experimental por archivo/tipo (grupos únicos de
relative_path + class_name). Es la medición que la ficha exige ANTES de gastar cómputo
real de Ollama regenerando resúmenes con la nueva granularidad: la reducción "÷~10" es
una hipótesis en la ficha, no un hecho — este script la contrasta con datos reales de
un corpus ya indexado.

Uso:
    python3 contar-reduccion.py <collection> [--base-url http://localhost:6333]

Salida: JSON con total de chunks, grupos únicos archivo/tipo, y el factor de reducción
observado, más el desglose de chunks sin archivo o sin agrupar (Namespace/Markdown/etc.)
"""
import argparse
import json
import sys
import urllib.request


def scroll_all(base_url: str, collection: str):
    offset = None
    points = []
    while True:
        body = {
            "limit": 500,
            "with_payload": ["relative_path", "class_name", "chunk_type"],
            "with_vector": False,
        }
        if offset is not None:
            body["offset"] = offset
        req = urllib.request.Request(
            f"{base_url}/collections/{collection}/points/scroll",
            data=json.dumps(body).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(req) as resp:
            data = json.load(resp)
        result = data["result"]
        points.extend(result["points"])
        offset = result.get("next_page_offset")
        if offset is None:
            break
    return points


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("collection")
    parser.add_argument("--base-url", default="http://localhost:6333")
    args = parser.parse_args()

    points = scroll_all(args.base_url, args.collection)

    # El manifiesto (__manifest__, ítem 5.f) y otros puntos especiales no llevan
    # relative_path — se excluyen del conteo de contenido indexable.
    content_points = [p for p in points if "relative_path" in p.get("payload", {})]
    skipped = len(points) - len(content_points)

    groups = set()
    for p in content_points:
        payload = p["payload"]
        key = (payload["relative_path"], payload.get("class_name") or "")
        groups.add(key)

    total_chunks = len(content_points)
    total_groups = len(groups)
    factor = round(total_chunks / total_groups, 2) if total_groups else None

    report = {
        "collection": args.collection,
        "total_puntos": len(points),
        "puntos_sin_relative_path_excluidos": skipped,
        "total_chunks_indexables": total_chunks,
        "grupos_unicos_archivo_tipo": total_groups,
        "factor_de_reduccion_observado": factor,
        "nota": (
            "factor = chunks_indexables / grupos_unicos. Es el numero real de llamadas "
            "LLM que el modo PerChunk hizo/haria vs las que el modo PerFile haria para "
            "ESTE corpus, asumiendo cache fria en ambos casos. NO es una medicion de "
            "calidad/recall -- eso lo decide el comparador A/B, no este script."
        ),
    }
    print(json.dumps(report, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
