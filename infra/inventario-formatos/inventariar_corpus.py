#!/usr/bin/env python3
"""Inventariador de solo lectura para el item 15.3 del ledger.

Censa, por extension/MIME, los archivos de las raices autorizadas (los
repositorios de negocio que 04-ingest-collections.sh realmente ingesta como
colecciones de Qdrant) y los clasifica en:

  - admitido_estructural: extension con chunker dedicado (Roslyn/TS/Markdown).
  - admitido_fallback:    extension que el scanner acepta pero cae al
                          FallbackChunkingStrategy (ventana deslizante).
  - no_admitido:          extension fuera de ScanProfile.DotNetEnterprise;
                          el scanner ni siquiera la ve.

Las reglas de admision NO se copian a mano: se leen en tiempo de ejecucion de
src/RagEngine.Core/Domain/ScanProfile.cs (extensiones permitidas + patrones de
exclusion) para que este inventario no pueda desincronizarse en silencio del
scanner real. Solo el conjunto de "lenguajes con chunker dedicado" queda fijo
en este archivo porque ChunkingStrategyRouter lo arma por reflexion en tiempo
de ejecucion de .NET, no por texto estatico parseable.

Es de solo lectura: nunca escribe ni borra nada bajo las raices escaneadas.
Reejecutarlo sobre el mismo corpus (sin cambios) produce los mismos conteos.

Uso:
    python3 inventariar_corpus.py --config raices_autorizadas.json --out informe.json
    python3 inventariar_corpus.py --config raices_autorizadas.json --markdown
"""
from __future__ import annotations

import argparse
import hashlib
import json
import mimetypes
import re
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable

REPO_ROOT = Path(__file__).resolve().parents[2]
SCAN_PROFILE_CS = REPO_ROOT / "src/RagEngine.Core/Domain/ScanProfile.cs"

# Lenguajes con IChunkingStrategy dedicado (ver ChunkingStrategyRouter: indexa
# por TargetLanguage != Unknown). Todo lo demas que el scanner admite cae al
# FallbackChunkingStrategy (ventana deslizante sobre texto plano).
LANGUAGES_CON_CHUNKER_DEDICADO = {"CSharp", "TypeScript", "Markdown"}

# Extension -> SourceLanguage, copiado del ExtensionMap de
# FileSystemIngestionScanner.cs. A diferencia de ScanProfile (parseado), este
# mapa vive en un Dictionary<string, SourceLanguage> C# con sintaxis mas
# irregular; se fija aqui con referencia expresa a la fuente para que un
# cambio futuro sea una discrepancia detectable a simple vista, no una
# suposicion oculta.
EXTENSION_A_LENGUAJE = {
    ".cs": "CSharp",
    ".ts": "TypeScript",
    ".tsx": "TypeScript",
    ".js": "JavaScript",
    ".jsx": "JavaScript",
    ".xaml": "Xaml",
    ".sql": "Sql",
    ".md": "Markdown",
    ".txt": "PlainText",
    ".json": "PlainText",
    ".xml": "PlainText",
    ".csproj": "PlainText",
}

# Formatos de interes explicito para el criterio de entrada (LightRAG-Anything
# §5.3): PDF/Office/imagenes/tablas/ecuaciones/audio/video, mas XAML y SQL que
# ya tienen ítems condicionales propios (15.6, 15.7).
FAMILIAS_DE_INTERES = {
    "pdf": {".pdf"},
    "office": {".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".rtf", ".odt", ".ods", ".odp"},
    "imagen": {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".tiff", ".heic"},
    "tabla_hoja_calculo": {".xlsx", ".xls", ".csv", ".ods"},
    "audio": {".mp3", ".wav", ".m4a", ".flac", ".ogg"},
    "video": {".mp4", ".mov", ".avi", ".mkv", ".webm"},
    "xaml": {".xaml"},
    "sql": {".sql"},
}


def _parse_scan_profile(path: Path) -> tuple[set[str], list[str]]:
    """Extrae AllowedExtensions y ExcludePatterns de DotNetEnterprise desde el
    C# fuente, para no duplicar a mano la lista que ya vive en el producto."""
    if not path.exists():
        raise FileNotFoundError(f"No se encontro ScanProfile.cs en {path}")
    text = path.read_text(encoding="utf-8")

    block_match = re.search(
        r"DotNetEnterprise\s*=>.*?AllowedExtensions\s*=\s*new HashSet<string>.*?\{(.*?)\}.*?ExcludePatterns\s*=\s*\[(.*?)\]",
        text,
        re.DOTALL,
    )
    if not block_match:
        raise ValueError("No se pudo parsear DotNetEnterprise en ScanProfile.cs (formato cambio)")

    ext_raw, excl_raw = block_match.groups()
    extensiones = set(re.findall(r'"(\.[A-Za-z0-9]+)"', ext_raw))
    exclusiones = re.findall(r'"([^"]+)"', excl_raw)
    return extensiones, exclusiones


@dataclass
class Fila:
    extension: str
    mime: str
    estado: str  # admitido_estructural | admitido_fallback | no_admitido
    lenguaje: str
    archivos: int = 0
    archivos_unicos: int = 0
    bytes_total: int = 0
    duplicados: int = 0


@dataclass
class ResultadoRaiz:
    nombre: str
    coleccion: str
    ruta: str
    excluidos_dirs: int = 0
    filas: dict[str, Fila] = field(default_factory=dict)
    hashes_vistos: dict[str, int] = field(default_factory=dict)  # sha256 -> conteo
    errores_acceso: int = 0


def clasificar_extension(ext: str, extensiones_admitidas: set[str]) -> tuple[str, str]:
    ext = ext.lower()
    lenguaje = EXTENSION_A_LENGUAJE.get(ext, "Unknown")
    if ext not in extensiones_admitidas:
        return "no_admitido", lenguaje
    if lenguaje in LANGUAGES_CON_CHUNKER_DEDICADO:
        return "admitido_estructural", lenguaje
    return "admitido_fallback", lenguaje


def _esta_excluido(rel_parts: tuple[str, ...], patrones_exclusion: list[str]) -> bool:
    patrones_lower = {p.lower() for p in patrones_exclusion}
    return any(part.lower() in patrones_lower for part in rel_parts)


def escanear_raiz(nombre: str, coleccion: str, ruta: Path,
                   extensiones_admitidas: set[str],
                   patrones_exclusion: list[str]) -> ResultadoRaiz:
    resultado = ResultadoRaiz(nombre=nombre, coleccion=coleccion, ruta=str(ruta))
    if not ruta.exists():
        resultado.errores_acceso += 1
        return resultado

    patrones_lower = {p.lower() for p in patrones_exclusion}
    for dirpath, dirnames, filenames in _walk_seguro(ruta):
        rel_dir = Path(dirpath).relative_to(ruta)
        if _esta_excluido(rel_dir.parts, patrones_exclusion):
            resultado.excluidos_dirs += 1
            dirnames[:] = []
            continue
        # Podar subdirectorios excluidos antes de bajar (bin/obj/etc.), y
        # contarlos aqui: os.walk nunca visita un dirpath ya podado de
        # dirnames, asi que sin este conteo explicito excluidos_dirs se
        # quedaria en 0 aunque la poda funcione correctamente.
        podados = [d for d in dirnames if d.lower() in patrones_lower]
        resultado.excluidos_dirs += len(podados)
        dirnames[:] = [d for d in dirnames if d.lower() not in patrones_lower]

        for fname in filenames:
            fpath = Path(dirpath) / fname
            try:
                if fpath.is_symlink() or not fpath.is_file():
                    continue
                size = fpath.stat().st_size
            except OSError:
                resultado.errores_acceso += 1
                continue

            ext = fpath.suffix
            estado, lenguaje = clasificar_extension(ext, extensiones_admitidas)
            mime, _ = mimetypes.guess_type(fname)
            mime = mime or "application/octet-stream"

            key = f"{ext.lower() or '(sin extension)'}|{estado}"
            fila = resultado.filas.setdefault(
                key, Fila(extension=ext.lower() or "(sin extension)", mime=mime,
                          estado=estado, lenguaje=lenguaje)
            )
            fila.archivos += 1
            fila.bytes_total += size

            digest = _hash_archivo(fpath)
            if digest is None:
                resultado.errores_acceso += 1
                continue
            visto_antes = resultado.hashes_vistos.get(digest, 0)
            resultado.hashes_vistos[digest] = visto_antes + 1
            if visto_antes == 0:
                fila.archivos_unicos += 1
            else:
                fila.duplicados += 1

    return resultado


def _walk_seguro(ruta: Path):
    import os
    for dirpath, dirnames, filenames in os.walk(ruta, onerror=lambda e: None):
        yield dirpath, dirnames, filenames


def _hash_archivo(path: Path, limite_bytes: int = 8 * 1024 * 1024) -> str | None:
    """Hash de contenido para deduplicar. Archivos grandes (>8MB, tipico de
    video) se hashean solo por tamano+primeros bytes: hashear binarios
    multi-GB completos en un inventario de solo-lectura no vale el costo de
    IO, y el objetivo es detectar duplicados exactos comunes (mismo PDF
    copiado dos veces), no un CAS criptografico."""
    try:
        h = hashlib.sha256()
        size = path.stat().st_size
        with path.open("rb") as f:
            if size <= limite_bytes:
                h.update(f.read())
            else:
                h.update(f.read(1024 * 1024))
                h.update(str(size).encode())
        return h.hexdigest()
    except OSError:
        return None


def familia_de(ext: str) -> str | None:
    for familia, exts in FAMILIAS_DE_INTERES.items():
        if ext in exts:
            return familia
    return None


def construir_informe(resultados: list[ResultadoRaiz]) -> dict:
    agregado: dict[str, Fila] = {}
    for r in resultados:
        for key, fila in r.filas.items():
            acc = agregado.setdefault(
                key, Fila(extension=fila.extension, mime=fila.mime,
                          estado=fila.estado, lenguaje=fila.lenguaje)
            )
            acc.archivos += fila.archivos
            acc.archivos_unicos += fila.archivos_unicos
            acc.bytes_total += fila.bytes_total
            acc.duplicados += fila.duplicados

    corpus_util_unico = sum(f.archivos_unicos for f in agregado.values())
    no_admitido_unico = sum(f.archivos_unicos for f in agregado.values()
                             if f.estado == "no_admitido")
    fraccion = (no_admitido_unico / corpus_util_unico) if corpus_util_unico else 0.0

    familias_no_admitidas = defaultdict(lambda: {"archivos_unicos": 0, "bytes_total": 0})
    for fila in agregado.values():
        if fila.estado != "no_admitido":
            continue
        fam = familia_de(fila.extension)
        if fam:
            familias_no_admitidas[fam]["archivos_unicos"] += fila.archivos_unicos
            familias_no_admitidas[fam]["bytes_total"] += fila.bytes_total

    umbral_archivos = 10
    umbral_fraccion = 0.10
    cumple_umbral_archivos = no_admitido_unico >= umbral_archivos
    cumple_umbral_fraccion = fraccion >= umbral_fraccion
    entra_por_volumen = cumple_umbral_archivos and cumple_umbral_fraccion

    return {
        "raices": [
            {
                "nombre": r.nombre,
                "coleccion": r.coleccion,
                "ruta": r.ruta,
                "directorios_excluidos": r.excluidos_dirs,
                "errores_acceso": r.errores_acceso,
                "filas": [
                    {
                        "extension": f.extension,
                        "mime": f.mime,
                        "estado": f.estado,
                        "lenguaje": f.lenguaje,
                        "archivos": f.archivos,
                        "archivos_unicos": f.archivos_unicos,
                        "duplicados": f.duplicados,
                        "bytes_total": f.bytes_total,
                    }
                    for f in sorted(r.filas.values(), key=lambda x: -x.archivos)
                ],
            }
            for r in resultados
        ],
        "agregado": {
            "corpus_util_archivos_unicos": corpus_util_unico,
            "no_admitido_archivos_unicos": no_admitido_unico,
            "fraccion_no_admitido": round(fraccion, 4),
            "familias_no_admitidas": dict(familias_no_admitidas),
            "filas": [
                {
                    "extension": f.extension,
                    "mime": f.mime,
                    "estado": f.estado,
                    "lenguaje": f.lenguaje,
                    "archivos": f.archivos,
                    "archivos_unicos": f.archivos_unicos,
                    "duplicados": f.duplicados,
                    "bytes_total": f.bytes_total,
                }
                for f in sorted(agregado.values(), key=lambda x: -x.archivos_unicos)
            ],
        },
        "criterio_de_entrada": {
            "umbral_archivos_unicos": umbral_archivos,
            "umbral_fraccion": umbral_fraccion,
            "cumple_umbral_archivos": cumple_umbral_archivos,
            "cumple_umbral_fraccion": cumple_umbral_fraccion,
            "entra_por_volumen_de_archivos": entra_por_volumen,
            "nota": (
                "Entrada tambien puede cumplirse con >=5 preguntas de negocio "
                "prioritarias verificadas que solo estos formatos respondan; "
                "eso no lo puede medir un inventario automatico y se registra "
                "aparte en el ledger."
            ),
        },
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--config", type=Path, required=True,
                    help="JSON con la lista de raices autorizadas (nombre, coleccion, ruta)")
    ap.add_argument("--out", type=Path, default=None, help="Ruta de salida JSON")
    ap.add_argument("--markdown", action="store_true", help="Imprime tambien un resumen en Markdown")
    ap.add_argument("--scan-profile-cs", type=Path, default=SCAN_PROFILE_CS)
    args = ap.parse_args()

    extensiones_admitidas, patrones_exclusion = _parse_scan_profile(args.scan_profile_cs)

    with args.config.open(encoding="utf-8") as f:
        config = json.load(f)

    resultados = []
    for raiz in config["raices"]:
        ruta = Path(raiz["ruta"]).expanduser()
        resultados.append(
            escanear_raiz(raiz["nombre"], raiz["coleccion"], ruta,
                          extensiones_admitidas, patrones_exclusion)
        )

    informe = construir_informe(resultados)
    informe["_procedencia"] = {
        "extensiones_admitidas": sorted(extensiones_admitidas),
        "patrones_exclusion": patrones_exclusion,
        "raices_config": str(args.config),
    }

    salida = json.dumps(informe, indent=2, ensure_ascii=False)
    if args.out:
        args.out.write_text(salida + "\n", encoding="utf-8")
    else:
        print(salida)

    if args.markdown:
        _imprimir_markdown(informe)

    return 0


def _imprimir_markdown(informe: dict) -> None:
    agg = informe["agregado"]
    print("\n## Resumen agregado (todas las raices)\n")
    print("| Extension | Estado | Lenguaje | Archivos | Unicos | Duplicados | Bytes |")
    print("|---|---|---|---|---|---|---|")
    for f in agg["filas"]:
        print(f"| {f['extension']} | {f['estado']} | {f['lenguaje']} | {f['archivos']} | "
              f"{f['archivos_unicos']} | {f['duplicados']} | {f['bytes_total']:,} |")
    print(f"\nCorpus util (archivos unicos, sin bin/obj/etc.): **{agg['corpus_util_archivos_unicos']}**")
    print(f"No admitidos (archivos unicos): **{agg['no_admitido_archivos_unicos']}** "
          f"({agg['fraccion_no_admitido']*100:.2f}%)")
    crit = informe["criterio_de_entrada"]
    print(f"\nCriterio de entrada por volumen: **{'CUMPLE' if crit['entra_por_volumen_de_archivos'] else 'NO CUMPLE'}** "
          f"(>= {crit['umbral_archivos_unicos']} archivos y >= {crit['umbral_fraccion']*100:.0f}% del corpus util)")


if __name__ == "__main__":
    sys.exit(main())
