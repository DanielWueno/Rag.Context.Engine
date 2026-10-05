#!/usr/bin/env python3
"""Verificador mecanico de la integracion de la rama local feat/rag-api-selector-coleccion
(5cc44a5) en main, unidad por unidad.

Cada unidad de 10.6-integrar-rama-local-en-main anade su propio modo --unidad a este
script. Hoy solo existe --unidad 10.6.1 (preservar el historial en un bundle y
reconciliar el ledger sin portar codigo). Las unidades 10.6.2, 10.6.3 y 10.6.4 deben
extender este archivo cuando se ejecuten, no sustituirlo.

Principio general: cualquier evidencia, cobertura o infraestructura ausente hace FALLAR
el chequeo correspondiente (exit != 0). Nunca se convierte una omision en exito.

Uso:
    python3 verificar.py --unidad 10.6.1 [--ledger RUTA] [--repo-root RUTA]

--ledger permite apuntar a una copia alterada del ledger (por ejemplo, para el control
negativo del chequeo 5) sin tener que commitear nada.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path


class Falla(Exception):
    """Una condicion de fallo de un chequeo numerado."""


def repo_root_desde_script() -> Path:
    # docs/analisis-futuro/integracion-rama-local/verificar.py -> raiz del repo
    return Path(__file__).resolve().parents[3]


def ejecutar_git(repo_root: Path, *args: str, check: bool = True) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["git", "-C", str(repo_root), *args],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def cargar_ledger_json(texto: str) -> dict:
    return json.loads(texto)


def iterar_items(ledger: dict):
    for ola in ledger.get("olas", []):
        for item in ola.get("items", []):
            yield ola, item


def recolectar_ids(ledger: dict) -> list[str]:
    return [item["id"] for _, item in iterar_items(ledger)]


def recolectar_items_por_id(ledger: dict) -> dict[str, dict]:
    out: dict[str, dict] = {}
    for _, item in iterar_items(ledger):
        out[item["id"]] = item
    return out


def leer_tsv(ruta: Path) -> list[dict]:
    filas = []
    with ruta.open(encoding="utf-8") as f:
        encabezado = f.readline().rstrip("\n").split("\t")
        for linea in f:
            linea = linea.rstrip("\n")
            if not linea:
                continue
            valores = linea.split("\t")
            if len(valores) != len(encabezado):
                raise Falla(
                    f"Fila con numero de columnas distinto al encabezado en {ruta}: {valores!r}"
                )
            filas.append(dict(zip(encabezado, valores)))
    return filas


# ---------------------------------------------------------------------------
# Chequeos individuales para --unidad 10.6.1
# ---------------------------------------------------------------------------


def chequeo_1_ids_duplicados(ledger_actual: dict) -> None:
    ids = recolectar_ids(ledger_actual)
    vistos: dict[str, int] = {}
    for i in ids:
        vistos[i] = vistos.get(i, 0) + 1
    dup = {k: v for k, v in vistos.items() if v > 1}
    if dup:
        raise Falla(f"(1) IDs duplicados en el ledger: {dup}")


def chequeo_2_cobertura_tsv(repo_root: Path, filas_tsv: list[dict]) -> None:
    proc = ejecutar_git(repo_root, "show", "5cc44a5:docs/analisis-futuro/ejecucion-plan.estado.json")
    if proc.returncode != 0:
        raise Falla(
            "(2) No se pudo leer el ledger local en 5cc44a5 "
            f"(git show fallo): {proc.stderr.strip()}"
        )
    ledger_local = cargar_ledger_json(proc.stdout)
    ids_local_ledger = set(recolectar_ids(ledger_local))

    ids_tsv_lista = [f["id_local"] for f in filas_tsv]
    ids_tsv_set = set(ids_tsv_lista)

    repetidos = {i for i in ids_tsv_lista if ids_tsv_lista.count(i) > 1}
    if repetidos:
        raise Falla(f"(2) id_local repetido en el tsv: {sorted(repetidos)}")

    faltan_en_tsv = ids_local_ledger - ids_tsv_set
    sobran_en_tsv = ids_tsv_set - ids_local_ledger
    if faltan_en_tsv or sobran_en_tsv:
        raise Falla(
            "(2) El conjunto de id_local del tsv difiere del conjunto de IDs del ledger "
            f"local 5cc44a5. Faltan en el tsv: {sorted(faltan_en_tsv)}. "
            f"Sobran en el tsv (no existen en 5cc44a5): {sorted(sobran_en_tsv)}."
        )


def chequeo_3_id_integrado(filas_tsv: list[dict], ids_ledger_actual: set[str]) -> None:
    problemas = []
    for fila in filas_tsv:
        tratamiento = fila["tratamiento"]
        id_integrado = fila["id_integrado"]
        if tratamiento in ("igual", "renumerado", "historico", "absorbido"):
            if id_integrado == "-" or id_integrado not in ids_ledger_actual:
                problemas.append(
                    f"id_local={fila['id_local']!r} tratamiento={tratamiento!r} "
                    f"id_integrado={id_integrado!r} no existe en el ledger"
                )
        elif tratamiento == "diferido":
            if id_integrado != "-" and id_integrado in ids_ledger_actual:
                problemas.append(
                    f"id_local={fila['id_local']!r} tratamiento=diferido pero "
                    f"id_integrado={id_integrado!r} SI existe en el ledger (no deberia)"
                )
        else:
            problemas.append(
                f"id_local={fila['id_local']!r} tiene tratamiento desconocido: {tratamiento!r}"
            )
    if problemas:
        raise Falla("(3) Problemas de id_integrado en el tsv:\n  " + "\n  ".join(problemas))


def _parece_id_literal(valor: str) -> bool:
    """Heuristica para distinguir un ID literal (sin espacios, p. ej.
    '10.6.2-portar-perfil-servidor-ollama') de una nota narrativa en prosa
    (con espacios), un patron ya reconocido y aceptado en este ledger para
    items como 3.5-portabilidad-x64 o 8.c-formato-diagnostico-4-bloques
    ('no tienen bloqueado_por literal, nota narrativa'). Las notas narrativas
    no se exigen resolubles."""
    return " " not in valor.strip()


def chequeo_4_referencias_resolubles(ledger_actual: dict) -> None:
    ids_existentes = set(recolectar_ids(ledger_actual))
    problemas = []
    for _, item in iterar_items(ledger_actual):
        bp = item.get("bloqueado_por")
        if bp is not None and _parece_id_literal(bp) and bp not in ids_existentes:
            problemas.append(f"item {item['id']!r}: bloqueado_por={bp!r} no resuelve")
        deps = item.get("_dependencias_adicionales")
        if deps:
            for dep in deps:
                if _parece_id_literal(dep) and dep not in ids_existentes:
                    problemas.append(
                        f"item {item['id']!r}: _dependencias_adicionales incluye {dep!r} que no resuelve"
                    )
    if problemas:
        raise Falla("(4) Referencias sin resolver:\n  " + "\n  ".join(problemas))


# Excepciones permitidas para el chequeo 5: por id, que campos pueden añadirse (nuevos)
# y que campos pueden cambiar de valor respecto del snapshot de origin/main.
EXCEPCIONES_CHEQUEO_5: dict[str, dict[str, set[str]]] = {
    "3.5-portabilidad-x64": {
        "nuevos_permitidos": {"resultado"},
        "cambiados_permitidos": {"estado"},
    },
    "11.4-rebaseline-olas-5-y-6": {
        "nuevos_permitidos": {"_resultado_paralelo_local_2026_10"},
        "cambiados_permitidos": set(),
    },
    "11.5-charspertoken-por-lenguaje": {
        "nuevos_permitidos": {"_resultado_paralelo_local_2026_10"},
        "cambiados_permitidos": set(),
    },
    "18.1-contexto-efectivo-de-los-prompts-del-motor": {
        "nuevos_permitidos": {"_absorbe_17_2_local"},
        "cambiados_permitidos": set(),
    },
}


def diff_item(origen: dict, actual: dict) -> list[str]:
    problemas = []
    claves_origen = set(origen.keys())
    claves_actual = set(actual.keys())
    eliminadas = claves_origen - claves_actual
    agregadas = claves_actual - claves_origen
    comunes = claves_origen & claves_actual

    excepcion = EXCEPCIONES_CHEQUEO_5.get(origen.get("id"), {"nuevos_permitidos": set(), "cambiados_permitidos": set()})
    nuevos_permitidos = excepcion["nuevos_permitidos"]
    cambiados_permitidos = excepcion["cambiados_permitidos"]

    for clave in eliminadas:
        problemas.append(f"campo eliminado (no permitido): {clave!r}")
    for clave in agregadas:
        if clave not in nuevos_permitidos:
            problemas.append(f"campo nuevo no permitido: {clave!r}")
    for clave in comunes:
        if origen[clave] != actual[clave] and clave not in cambiados_permitidos:
            problemas.append(f"campo cambiado no permitido: {clave!r}")
    return problemas


def chequeo_5_preservacion_origin_main(repo_root: Path, ledger_actual: dict) -> None:
    proc = ejecutar_git(repo_root, "show", "origin/main:docs/analisis-futuro/ejecucion-plan.estado.json")
    if proc.returncode != 0:
        raise Falla(
            "(5) No se pudo leer el ledger de origin/main "
            f"(git show fallo, revisa que exista ese remote-tracking ref): {proc.stderr.strip()}"
        )
    ledger_origin = cargar_ledger_json(proc.stdout)
    items_origin = recolectar_items_por_id(ledger_origin)
    items_actual = recolectar_items_por_id(ledger_actual)

    problemas = []
    for id_, item_origin in items_origin.items():
        item_actual = items_actual.get(id_)
        if item_actual is None:
            problemas.append(f"item {id_!r} existia en origin/main y desaparecio del ledger actual")
            continue
        diffs = diff_item(item_origin, item_actual)
        if diffs:
            problemas.append(f"item {id_!r}: " + "; ".join(diffs))

    if problemas:
        raise Falla(
            "(5) Items de origin/main alterados fuera de las excepciones permitidas:\n  "
            + "\n  ".join(problemas)
        )


def control_negativo_chequeo_5(repo_root: Path, ledger_actual: dict) -> None:
    """Prueba que el chequeo 5 SI falla si se altera un resultado de un item de main
    que no esta en la lista de excepciones. No debe llamarse en una corrida normal de
    aceptacion; es la prueba exigida por el propio criterio de 10.6.1."""
    import copy

    mutado = copy.deepcopy(ledger_actual)
    items = recolectar_items_por_id(mutado)
    objetivo = None
    for candidato in ("12.1-cotas-y-cierre-inmediato", "12.8-secretos-y-tls", "12.11-auditoria-local-con-actor"):
        if candidato in items and "resultado" in items[candidato]:
            objetivo = candidato
            break
    if objetivo is None:
        raise Falla("(control negativo) no se encontro un item de main con 'resultado' para tamperar")
    items[objetivo]["resultado"] = items[objetivo]["resultado"] + " TAMPER-DE-PRUEBA-CONTROL-NEGATIVO"

    try:
        chequeo_5_preservacion_origin_main(repo_root, mutado)
    except Falla:
        return  # Correcto: el chequeo detecto la alteracion.
    raise Falla(
        "(control negativo) el chequeo 5 NO fallo al tamperar "
        f"el resultado de {objetivo!r}; el control negativo exigido no se cumple"
    )


def chequeo_6_bundle(repo_root: Path, ledger_actual: dict) -> None:
    nota = ledger_actual.get("_bundle_historial_local_2026_10_05")
    if not nota:
        raise Falla("(6) Falta la nota _bundle_historial_local_2026_10_05 en el ledger")

    match_ruta = re.search(r"([A-Za-z]:\\[^\"]*?\.bundle)", nota)
    match_sha = re.search(r"\b([0-9a-f]{64})\b", nota)
    if not match_ruta or not match_sha:
        raise Falla(
            "(6) No se pudo extraer ruta (.bundle) y/o sha256 (64 hex) de la nota del bundle"
        )
    ruta_bundle = Path(match_ruta.group(1))
    sha_registrado = match_sha.group(1).lower()

    if not ruta_bundle.exists():
        raise Falla(f"(6) El archivo de bundle registrado no existe: {ruta_bundle}")

    hasher = hashlib.sha256()
    with ruta_bundle.open("rb") as f:
        for bloque in iter(lambda: f.read(1024 * 1024), b""):
            hasher.update(bloque)
    sha_actual = hasher.hexdigest().lower()

    if sha_actual != sha_registrado:
        raise Falla(
            f"(6) El sha256 del bundle no coincide: registrado={sha_registrado} actual={sha_actual}"
        )

    proc = ejecutar_git(repo_root, "bundle", "verify", str(ruta_bundle))
    if proc.returncode != 0:
        raise Falla(f"(6) 'git bundle verify' fallo:\n{proc.stdout}\n{proc.stderr}")


ARCHIVOS_PERMITIDOS_10_6_1 = {
    "docs/analisis-futuro/ejecucion-plan.estado.json",
    "docs/analisis-futuro/integracion-rama-local-2026-10-05.ids.tsv",
    "docs/analisis-futuro/integracion-rama-local/verificar.py",
    "docs/referencias-tecnologicas.md",
    "README.md",
    "docs/README.md",
    # Procedencia ya escrita (preparacion previa a este item); el campo "archivos" del
    # propio 10.6.1 la lista explicitamente, aunque esta unidad no la edite.
    "docs/analisis-futuro/integracion-rama-local-2026-10-05.md",
}


def chequeo_7_diff_archivos(repo_root: Path) -> None:
    # Se compara origin/main contra el arbol de trabajo actual (en vez de origin/main..HEAD)
    # para que el chequeo funcione tanto antes como despues de commitear esta unidad: una vez
    # commiteado, el arbol de trabajo coincide con HEAD y el resultado es identico.
    proc = ejecutar_git(repo_root, "diff", "--name-only", "origin/main")
    if proc.returncode != 0:
        raise Falla(f"(7) 'git diff --name-only origin/main' fallo: {proc.stderr.strip()}")
    archivos = [linea.strip() for linea in proc.stdout.splitlines() if linea.strip()]

    # Tambien hay que incluir archivos NUEVOS sin seguimiento (untracked) que ya pertenecen
    # a este diff logico (git diff no los ve hasta que se hace git add).
    proc_untracked = ejecutar_git(
        repo_root, "ls-files", "--others", "--exclude-standard"
    )
    if proc_untracked.returncode != 0:
        raise Falla(f"(7) 'git ls-files --others' fallo: {proc_untracked.stderr.strip()}")
    untracked = [linea.strip() for linea in proc_untracked.stdout.splitlines() if linea.strip()]

    todos = set(archivos) | set(untracked)
    fuera_de_lista = sorted(a for a in todos if a not in ARCHIVOS_PERMITIDOS_10_6_1)
    if fuera_de_lista:
        raise Falla(
            "(7) git diff/untracked contra origin/main incluye archivos fuera de la lista "
            f"de esta unidad: {fuera_de_lista}"
        )


def ejecutar_unidad_10_6_1(repo_root: Path, ruta_ledger: Path) -> None:
    ledger_actual = cargar_ledger_json(ruta_ledger.read_text(encoding="utf-8"))
    ruta_tsv = repo_root / "docs/analisis-futuro/integracion-rama-local-2026-10-05.ids.tsv"
    if not ruta_tsv.exists():
        raise Falla(f"No existe el tsv esperado: {ruta_tsv}")
    filas_tsv = leer_tsv(ruta_tsv)

    chequeo_1_ids_duplicados(ledger_actual)
    print("(1) OK: sin IDs duplicados en el ledger.")

    chequeo_2_cobertura_tsv(repo_root, filas_tsv)
    print("(2) OK: el tsv cubre exactamente los IDs del ledger local 5cc44a5, sin repetidos.")

    ids_ledger_actual = set(recolectar_ids(ledger_actual))
    chequeo_3_id_integrado(filas_tsv, ids_ledger_actual)
    print("(3) OK: cada id_integrado resuelve segun su tratamiento (igual/renumerado/historico/absorbido existen; diferido no existe).")

    chequeo_4_referencias_resolubles(ledger_actual)
    print("(4) OK: todo bloqueado_por y _dependencias_adicionales resuelve a un ID existente.")

    chequeo_5_preservacion_origin_main(repo_root, ledger_actual)
    print("(5) OK: ningun item de origin/main cambio fuera de las excepciones permitidas.")
    control_negativo_chequeo_5(repo_root, ledger_actual)
    print("(5-control-negativo) OK: tamperar un resultado de main hace fallar el chequeo 5, como exige el criterio.")

    chequeo_6_bundle(repo_root, ledger_actual)
    print("(6) OK: sha256 del bundle coincide y 'git bundle verify' acepta el archivo.")

    chequeo_7_diff_archivos(repo_root)
    print("(7) OK: el diff contra origin/main no toca archivos fuera de la lista de esta unidad.")


UNIDADES_IMPLEMENTADAS = {
    "10.6.1": ejecutar_unidad_10_6_1,
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unidad", required=True, help="Unidad a verificar, p. ej. 10.6.1")
    parser.add_argument(
        "--ledger",
        default=None,
        help="Ruta alternativa al ledger JSON (para control negativo); por defecto, el del repo.",
    )
    parser.add_argument("--repo-root", default=None, help="Raiz del repo; por defecto se autodetecta.")
    args = parser.parse_args()

    repo_root = Path(args.repo_root) if args.repo_root else repo_root_desde_script()
    ruta_ledger = (
        Path(args.ledger)
        if args.ledger
        else repo_root / "docs/analisis-futuro/ejecucion-plan.estado.json"
    )

    funcion = UNIDADES_IMPLEMENTADAS.get(args.unidad)
    if funcion is None:
        print(
            f"ERROR: --unidad {args.unidad!r} no esta implementada todavia en este script "
            f"(unidades disponibles: {sorted(UNIDADES_IMPLEMENTADAS)}). "
            "No se convierte esta omision en exito.",
            file=sys.stderr,
        )
        return 1

    try:
        funcion(repo_root, ruta_ledger)
    except Falla as exc:
        print(f"FALLO: {exc}", file=sys.stderr)
        return 1
    except Exception as exc:  # defensivo: cualquier error inesperado tambien es fallo
        print(f"ERROR INESPERADO: {exc!r}", file=sys.stderr)
        return 1

    print(f"RESULTADO FINAL: PASA (--unidad {args.unidad})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
