#!/usr/bin/env bash
# Reingesta las colecciones de Qdrant desde cero. Los comandos abajo fueron
# reconstruidos investigando logs/rag-engine-*.json, logs/rag-api-*.json y
# docs/analisis-futuro/*.md de la máquina original — NO todos son literales
# capturados, ver el nivel de confianza en el comentario de cada bloque y en
# ../NOTAS-INGESTA.md.
#
# ADVERTENCIA DE TIEMPO: la primera ingesta real de bsuite-repo (--con-resumen)
# tardó ~18h55m en la máquina original. Si tienes forma de copiar directo la
# carpeta qdrant_storage/ (304MB) en vez de correr esto, es MUCHO más rápido —
# ver ../README.md, opción A.
#
# Requiere: Qdrant corriendo (02-start-qdrant.sh), Ollama corriendo con
# qwen2.5-coder si vas a usar --con-resumen, y los repos fuente clonados
# (03-clone-source-repos.sh) en las rutas por defecto de abajo.
#
# Uso:
#   bash 04-ingest-collections.sh <bsuite-repo|bsuite-auditorias-test|innovapp-docs|micro-repo|wiki-solis|rag-engine|all>
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECTS_DIR="${PROJECTS_DIR:-$HOME/Documents/Projects}"
CLI="dotnet run --project $REPO_ROOT/src/RagEngine.Cli --"

ingest_bsuite_repo() {
  # Confianza: ALTA — comando literal encontrado en
  # docs/analisis-futuro/busqueda-libre-rag-3-bandas.md:701 y corroborado por
  # logs/rag-engine-20260731.json ("Starting ingestion..." con los mismos valores).
  # 2161 archivos, 22986 chunks la primera vez.
  $CLI ingest "$PROJECTS_DIR/BusinessSuite.Xaf" \
    --collection bsuite-repo --repo-name BusinessSuite.Xaf --con-resumen --force
}

ingest_bsuite_auditorias_test() {
  # Confianza: ALTA — reconstruido de logs/rag-engine-2026073{0,1}.json (tres
  # "Starting ingestion" con ConResumen:true, mismo repository_name). Sin --force:
  # la colección se borraba antes con un DELETE directo a Qdrant, no con --force.
  # Si la colección no existe todavía, --force es inofensivo (crea igual); se
  # deja por seguridad de re-ejecución idempotente.
  local base="$PROJECTS_DIR/BusinessSuite.Xaf/src/Modules"
  $CLI ingest "$base/REYMA.XAFR1PV.Compras/Auditorias" \
    --collection bsuite-auditorias-test --repo-name BusinessSuite.Xaf --con-resumen --force
  $CLI ingest "$base/REYMA.XAFR1PV.Compras/Compras/Utils" \
    --collection bsuite-auditorias-test --repo-name BusinessSuite.Xaf --con-resumen
  $CLI ingest "$base/REYMA.XAFR1PV.Base" \
    --collection bsuite-auditorias-test --repo-name BusinessSuite.Xaf --con-resumen
}

ingest_innovapp_docs() {
  # Confianza: MEDIA — source path y con-resumen=NO confirmados por payloads de
  # búsqueda (file_path real, sin resumen_pending). NO hay log del comando de
  # ingesta original (es anterior a los logs retenidos) — --lang/--force/--batch-size
  # se asumen default porque es documentación pura (Markdown).
  $CLI ingest "$PROJECTS_DIR/docs-bsute-innovapp-plan" --collection innovapp-docs
}

ingest_micro_repo() {
  # Confianza: MEDIA — source path confirmado por comentarios "// Repository:"/
  # "// File:" en payloads de búsqueda. con-resumen=NO confirmado (repository_name
  # default "my-repo", sin resumen_pending). La colección actual está ROTA
  # ("Not existing vector name error: dense" — esquema de vectores desactualizado);
  # re-ingestar con --force de cero además arregla eso.
  $CLI ingest "$PROJECTS_DIR/Reyma.TI.Tickets.Microservice" --collection micro-repo --force
}

ingest_wiki_solis() {
  # Confianza: MEDIA — source path confirmado por file_path reales en
  # logs/rag-api-*.json (docs/wiki de BSuite). con-resumen=NO confirmado.
  $CLI ingest "$PROJECTS_DIR/Business-Suite" --collection wiki-solis
}

ingest_rag_engine() {
  # Confianza: MEDIA-ALTA — repository_name "RagEngine" confirmado en payloads;
  # "rag-engine" es el --collection default del CLI, así que probablemente se
  # omitió -c. con-resumen=NO confirmado (sin resumen_pending, 0/10 medido).
  $CLI ingest "$REPO_ROOT" --repo-name RagEngine
}

TARGET="${1:-}"
case "$TARGET" in
  bsuite-repo) ingest_bsuite_repo ;;
  bsuite-auditorias-test) ingest_bsuite_auditorias_test ;;
  innovapp-docs) ingest_innovapp_docs ;;
  micro-repo) ingest_micro_repo ;;
  wiki-solis) ingest_wiki_solis ;;
  rag-engine) ingest_rag_engine ;;
  all)
    ingest_bsuite_repo
    ingest_bsuite_auditorias_test
    ingest_innovapp_docs
    ingest_micro_repo
    ingest_wiki_solis
    ingest_rag_engine
    ;;
  *)
    echo "Uso: $0 <bsuite-repo|bsuite-auditorias-test|innovapp-docs|micro-repo|wiki-solis|rag-engine|all>" >&2
    echo "NOTA: 'engine-repo' NO está incluida — su fuente es irrecuperable, ver ../NOTAS-INGESTA.md" >&2
    exit 1
    ;;
esac
