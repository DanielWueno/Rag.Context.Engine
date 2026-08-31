#!/usr/bin/env python3
"""Verifica que un eval-set siga siendo medible contra una colección indexada.

Un ancla que existe en el archivo fuente pero no sobrevive al chunker mide ruido,
no recall: la pregunta falla siempre y nadie sabe por qué. Este script comprueba,
ancla por ancla, las tres cosas de las que depende que un hit sea posible:

  1. el literal está en algún archivo del repositorio con ese nombre;
  2. ese archivo está indexado en la colección;
  3. algún chunk indexado de ese archivo contiene el literal (comparación Ordinal,
     la misma que hace `rag eval`).

Y que los negativos sigan siendo negativos: sin SourceFile y sin anclas.

Córrelo después de cada re-ingesta que cambie `chunking_contract_version`: el
chunker normaliza el texto (por ejemplo `class X : Base` se indexa como
`class X: Base`), así que un cambio de corte puede dejar anclas huérfanas sin que
nada más lo denuncie.

Uso:
  python3 infra/verificar-anclas-eval.py \
      --eval-set docs/eval/bsuite-repo.eval-set.json \
      --collection bsuite-repo \
      --repo ~/Documents/Projects/BusinessSuite.Xaf

Sale con código 1 si algo no se sostiene.
"""
import argparse
import collections
import json
import os
import sys
import urllib.request

EXCLUIDOS = {"obj", "bin", ".git", ".vs", "node_modules", "packages"}


def cargar_indice(qdrant, coleccion):
    """{nombre de archivo -> [payloads]} de toda la colección."""
    url = f"{qdrant}/collections/{coleccion}/points/scroll"
    por_archivo = collections.defaultdict(list)
    offset = None
    while True:
        cuerpo = {
            "limit": 1000,
            "with_vector": False,
            "with_payload": ["content", "relative_path", "chunk_type",
                             "class_name", "method_name", "start_line", "end_line"],
        }
        if offset:
            cuerpo["offset"] = offset
        peticion = urllib.request.Request(
            url, data=json.dumps(cuerpo).encode(),
            headers={"Content-Type": "application/json"})
        resultado = json.load(urllib.request.urlopen(peticion))["result"]
        for punto in resultado["points"]:
            payload = punto["payload"]
            por_archivo[os.path.basename(payload["relative_path"])].append(payload)
        offset = resultado.get("next_page_offset")
        if not offset:
            return por_archivo


def archivos_en_disco(raiz, nombre):
    encontrados = []
    for directorio, subdirs, archivos in os.walk(raiz):
        subdirs[:] = [d for d in subdirs if d not in EXCLUIDOS]
        if nombre in archivos:
            encontrados.append(os.path.join(directorio, nombre))
    return encontrados


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--eval-set", required=True)
    parser.add_argument("--collection", required=True)
    parser.add_argument("--repo", required=True, help="raíz del repositorio ingestado")
    parser.add_argument("--qdrant", default="http://localhost:6333")
    parser.add_argument("-v", "--verbose", action="store_true",
                        help="imprime también las anclas que sí verifican")
    args = parser.parse_args()

    raiz = os.path.expanduser(args.repo)
    indice = cargar_indice(args.qdrant, args.collection)
    preguntas = json.load(open(args.eval_set, encoding="utf-8"))

    problemas = 0
    for numero, pregunta in enumerate(preguntas, 1):
        objetivos = pregunta.get("SourceFiles") or (
            [pregunta["SourceFile"]] if pregunta.get("SourceFile") else [])
        anclas = pregunta.get("TargetContentContains") or []
        etiqueta = pregunta["Question"][:70]

        if not objetivos:
            if anclas:
                print(f"[{numero}] NEGATIVO CON ANCLAS   {etiqueta}")
                problemas += 1
            elif args.verbose:
                print(f"[{numero}] ok negativo           {etiqueta}")
            continue

        if not anclas:
            print(f"[{numero}] ANCLADA SIN ANCLAS    {etiqueta}")
            problemas += 1
            continue

        for ancla in anclas:
            en_disco = [
                ruta
                for objetivo in objetivos
                for ruta in archivos_en_disco(raiz, objetivo)
                if ancla in open(ruta, encoding="utf-8-sig", errors="replace").read()
            ]
            chunks = [c for objetivo in objetivos
                      for c in indice.get(objetivo, []) if ancla in c["content"]]

            if not en_disco:
                print(f"[{numero}] ANCLA AUSENTE DEL ARCHIVO  {objetivos} :: {ancla[:70]!r}")
                problemas += 1
            elif not chunks:
                print(f"[{numero}] ANCLA NO INDEXADA          {objetivos} :: {ancla[:70]!r}")
                problemas += 1
            elif args.verbose:
                c = chunks[0]
                sitio = (f"{c['chunk_type']} "
                         f"{c.get('method_name') or c.get('class_name') or ''} "
                         f"L{c['start_line']}-{c['end_line']}")
                print(f"[{numero}] ok  {objetivos[0]:38s} {len(chunks)} chunk(s)  {sitio}")

    if problemas:
        print(f"\n{problemas} problema(s) en {len(preguntas)} preguntas")
        return 1
    print(f"\nTodo verificado — {len(preguntas)} preguntas")
    return 0


if __name__ == "__main__":
    sys.exit(main())
