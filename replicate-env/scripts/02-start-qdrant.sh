#!/usr/bin/env bash
# Levanta Qdrant tal como corre en la máquina original: contenedor standalone
# llamado "qdrant-local" (fuera de infra/docker-compose.yml — ver el comentario
# en ese archivo), con storage en bind mount a disco (no volumen nombrado) para
# poder inspeccionarlo/copiarlo directo con cp/rsync.
#
# Uso:
#   bash 02-start-qdrant.sh [ruta-de-storage]
# Default de ruta-de-storage: ~/qdrant_storage
set -euo pipefail

STORAGE_DIR="${1:-$HOME/qdrant_storage}"
mkdir -p "$STORAGE_DIR"

if docker ps -a --format '{{.Names}}' | grep -qx qdrant-local; then
  echo "El contenedor 'qdrant-local' ya existe. Arrancándolo (si no estaba corriendo)..."
  docker start qdrant-local
else
  docker run -d --name qdrant-local \
    -p 6333:6333 -p 6334:6334 \
    -v "$STORAGE_DIR:/qdrant/storage" \
    qdrant/qdrant:v1.14.1
fi

sleep 3
curl -sf http://localhost:6333/readyz && echo " Qdrant OK en http://localhost:6333"
