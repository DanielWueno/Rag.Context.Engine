# Replicar el entorno de Rag.Context.Engine en otra máquina

Esta carpeta junta todo lo que **no vive en git** y que hace falta para seguir
trabajando en otra máquina: modelos, datos de Qdrant, caché de resúmenes, y las
preguntas históricas reales usadas para verificar el modo Simple (Fase 1/Fase 2).

Generada 2026-08-07 durante la implementación de la Fase 2 del plan
`docs/analisis-futuro/modo-respuesta-simple-codigo.md`.

## ⚠️ Antes de nada: dos caminos posibles para Qdrant

El storage real de Qdrant en la máquina original pesa **304MB**
(`/Users/DevStudio/qdrant_storage`). Tienes dos opciones:

- **Opción A — copiar el storage directo** (recomendado si hay red entre las
  máquinas, o puedes moverlo por USB/nube): `rsync`/`scp`/`tar` esa carpeta a
  `~/qdrant_storage` en la otra máquina, apunta `02-start-qdrant.sh` ahí, y ya
  está — todas las colecciones existen tal cual, sin gastar tiempo de cómputo.
- **Opción B — regenerar desde cero** con `04-ingest-collections.sh`. Mucho más
  lento: la primera ingesta real de `bsuite-repo` con `--con-resumen` tardó
  **~18h55m** en la máquina original (ver memoria de proyecto). Aunque "la otra
  máquina tiene potencia", el cuello de botella de `--con-resumen` es una
  llamada a Ollama POR CHUNK (22986 chunks solo en bsuite-repo) — el resumen de
  esa cache (`data/summary-cache.sqlite3`, ver paso 5) puede evitar la mayor
  parte de ese costo si el contenido del repo fuente no cambió de commit.

Si no tienes forma de mover 304MB entre las máquinas, ve directo a Opción B.

## Pasos (Opción B / regenerar)

```bash
# 0. Requisitos — ver REQUISITOS.md primero (esto asume que ya están instalados)

# 1. Modelos ONNX (~1.2GB)
bash scripts/01-download-models.sh

# 2. Qdrant (contenedor standalone "qdrant-local", igual que la máquina original)
bash scripts/02-start-qdrant.sh

# 3. Clonar los repos FUENTE que se ingestan como contenido
#    (dos son Azure DevOps privados — necesitas acceso a la org, ver REQUISITOS.md)
bash scripts/03-clone-source-repos.sh

# 4. (Opcional pero recomendado) restaurar el caché de resúmenes real ANTES de
#    ingestar con --con-resumen, para no regenerar desde cero
bash scripts/05-restore-summary-cache.sh

# 5. Ingestar cada colección (o "all" para las 6 confirmadas)
bash scripts/04-ingest-collections.sh all
#    — Nota: esto sigue tardando horas si el content_hash de los chunks no
#      matchea la caché restaurada en el paso anterior (repo en otro commit,
#      u otro modelo Ollama). Ver NOTAS-INGESTA.md para el detalle de cada
#      colección y qué tan confirmado está cada comando.

# 6. Verificar
bash scripts/06-verify.sh
```

## Qué hay en `data/`

- `data/questions/<coleccion>.json` — TODAS las preguntas reales históricas
  hechas contra esa colección (427 en total, todas las que hay en los logs de
  la máquina original), extraídas limpias de `logs/rag-api-*.json` (que en sí
  mismo pesa 316MB y está en `.gitignore` — por eso se generó este extracto en
  vez de copiar los logs crudos). Cada entrada trae `query`, `topK`,
  `minScore`, `rerank`, `responseMode`, `historyTurns`. Útil para re-correr
  `infra/verify-simple-mode.py` en la otra máquina, o para seguir evaluando
  recall/calidad sin depender de traer los logs completos.
- `data/summary-cache.sqlite3` — copia real del caché de resúmenes de negocio
  (SummaryCache), 947+ entradas. Ver `scripts/05-restore-summary-cache.sh`.

## Qué NO está aquí (y por qué)

- **`logs/` completo** (316MB, gitignorado): contiene preguntas reales de
  usuarios de negocio y las respuestas completas — más sensible que el
  extracto de `data/questions/`, y mucho más pesado. Si de verdad lo necesitas
  completo (por ejemplo para auditar respuestas exactas, no solo qué se
  preguntó), cópialo aparte directamente entre máquinas.
- **Modelos ONNX** (~1.2GB): se regeneran con `scripts/01-download-models.sh`,
  no tiene sentido versionarlos/copiarlos a mano.
- **`engine-repo`**: colección con esquema de vectores roto
  ("Not existing vector name error: dense") y **sin ningún rastro recuperable**
  de qué repositorio fuente se ingestó ahí — ver `NOTAS-INGESTA.md`. Se
  recomienda simplemente no recrearla.

## ⚠️ Sensibilidad de este contenido

Esta carpeta incluye:
- Preguntas reales de usuarios de negocio sobre sistemas internos
  (`data/questions/*.json`).
- URLs de repositorios privados de la organización en Azure DevOps
  (`scripts/03-clone-source-repos.sh`).
- Resúmenes de lógica de negocio interna (`data/summary-cache.sqlite3`).

**No se subió a git ni se pusheó a ningún remoto** — queda como carpeta local
para que decidas cómo moverla (USB, red interna, almacenamiento privado). Si
quieres que la suba a algún lado, dímelo explícitamente y a dónde.
