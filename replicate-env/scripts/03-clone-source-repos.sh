#!/usr/bin/env bash
# Clona los repositorios FUENTE que se ingestaron en las colecciones de Qdrant.
# Estos NO son parte de Rag.Context.Engine — son otros proyectos del negocio
# que este motor indexa como contenido.
#
# Dos son Azure DevOps privados (necesitas acceso a la org GR-PlasticosAdministrativo /
# un Personal Access Token con permiso de "Code: Read"); dos son GitHub.
#
# Uso:
#   bash 03-clone-source-repos.sh [directorio-destino]
# Default: ~/Documents/Projects (misma convención que la máquina original, para
# que las rutas de 04-ingest-collections.sh funcionen sin editar nada).
set -euo pipefail

DEST="${1:-$HOME/Documents/Projects}"
mkdir -p "$DEST"
cd "$DEST"

clone_if_missing() {
  local url="$1" dir="$2"
  if [ -d "$dir/.git" ]; then
    echo "Ya existe: $dir (skip)"
  else
    echo "Clonando $url -> $dir"
    git clone "$url" "$dir"
  fi
}

# bsuite-repo / bsuite-auditorias-test
clone_if_missing "https://GR-PlasticosAdministrativo@dev.azure.com/GR-PlasticosAdministrativo/BusinessSuite/_git/BusinessSuite.Xaf" "BusinessSuite.Xaf"

# innovapp-docs
clone_if_missing "https://github.com/DanielWueno/docs-bsute-innovapp-plan.git" "docs-bsute-innovapp-plan"

# micro-repo (colección actualmente rota — "Not existing vector name error: dense" — pero
# el path fuente sigue siendo válido para re-ingestar de cero, lo que de paso arregla el
# esquema de vectores desactualizado)
clone_if_missing "https://GR-PlasticosAdministrativo@dev.azure.com/GR-PlasticosAdministrativo/Reyma.InnovApp/_git/Reyma.TI.Tickets.Microservice" "Reyma.TI.Tickets.Microservice"

# wiki-solis
clone_if_missing "https://github.com/solis1408/Business-Suite.git" "Business-Suite"

echo
echo "Nota: Rag.Context.Engine (la colección 'rag-engine', auto-ingesta) ya la tienes —"
echo "es este mismo repo."
