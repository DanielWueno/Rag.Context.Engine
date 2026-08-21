#!/usr/bin/env bash
# Copia el caché real de resúmenes de negocio (../data/summary-cache.sqlite3,
# 947+ entradas) a donde SummaryCache.Open lo espera. Esto evita regenerar
# resúmenes vía Ollama (--con-resumen) para bsuite-repo / bsuite-auditorias-test
# — el paso más lento de la ingesta.
#
# IMPORTANTE: la caché se indexa por content_hash + prompt_version (ver
# SummaryCache.cs / OllamaBusinessSummaryGenerator.ComputePromptVersion). Un hit
# requiere que el contenido del chunk sea BYTE-IDÉNTICO (mismo repo, mismo commit)
# y que el modelo Ollama configurado (Ollama:ModelId en appsettings.json) sea el
# mismo qwen2.5-coder usado para generarla. Si el repo fuente cambió de commit
# desde la ingesta original, o usas otro modelo, la caché simplemente no pega
# (miss silencioso, se regenera) — no rompe nada, solo no acelera.
set -euo pipefail

DEST="${1:-$HOME/Library/Application Support/rag-engine/summary-cache.sqlite3}"
mkdir -p "$(dirname "$DEST")"

SRC="$(cd "$(dirname "$0")/.." && pwd)/data/summary-cache.sqlite3"
cp -n "$SRC" "$DEST" 2>/dev/null && echo "Copiado a: $DEST" || echo "Ya existe (no sobrescrito): $DEST"

echo "Recuerda apuntar Ingestion:ResumenCachePath (appsettings.json / env var) a: $DEST"
