# Operaciones

> Runbook: infraestructura, observabilidad y resolución de los problemas conocidos.

## Infraestructura

```bash
docker compose -f infra/docker-compose.yml up -d    # Qdrant (gRPC 6334, REST 6333)
curl -s http://localhost:6333/collections           # sanity check REST
ollama serve                                        # requerido solo para `rag ask`
```

`rag doctor` valida todo el stack en un comando (Qdrant, modelo ONNX, tokenizador, disco).

### Caché de resúmenes de negocio (SQLite)

`~/Library/Application Support/rag-engine/summary-cache.sqlite3` guarda los resúmenes generados por
la Fase 2, con clave `content_hash + prompt_version` y **compartida entre todas las colecciones**.
Es lo que convierte una re-ingesta de horas en uno de minutos: si el contenido del chunk no cambió,
no se vuelve a llamar a Ollama.

> ⚠️ **Nunca la escribas desde el host y la leas desde el contenedor a la vez.** El contenedor
> `rag-api` la monta por bind mount y SQLite la abre en modo WAL. El índice WAL vive en memoria
> compartida (`*-shm`) y exige coherencia de `mmap` entre todos los procesos que abren la base;
> VirtioFS/gRPC-FUSE no la garantiza entre el host y la VM de Linux. **Baja `rag-api` antes de
> correr una ingesta con `--con-resumen`.** Ver el runbook de recuperación más abajo.

## Observabilidad

- **Logs estructurados:** Serilog → `logs/rag-engine-YYYYMMDD.json` (CompactJsonFormatter). La consola solo muestra `Warning+`; el archivo lo tiene todo, incluyendo `CorrelationId` por búsqueda. Fuente de verdad para tiempos de ingesta y conteos:

```bash
grep -h "Ingestion complete" logs/rag-engine-*.json | tail -3
grep -h "Search completed"  logs/rag-engine-*.json | tail -10
```

- **Métricas** (`System.Diagnostics.Metrics`, medidor `RagEngine`): `chunks_indexed_total`, `ingestion_errors_total` (etiquetadas por etapa), `search_latency_ms`, `search_errors_total`.
- **Resiliencia:** las llamadas a Qdrant pasan por Polly (3 reintentos exponenciales + circuit breaker 50%/30s). Los reintentos se loguean con `Execution attempt`.

## Troubleshooting

### "I cannot find enough information…" en `rag ask`

Grounding estricto: el contexto recuperado no contiene la respuesta, **o el LLM local (7B) no
supo sintetizarla desde el contexto**. En orden:
1. ¿La colección se ingestó con el motor actual? (ver [matriz de re-ingesta](configuracion.md#cuándo-re-ingestar))
2. Sube `-k` (p. ej. 10) y verifica con `rag search ... -o markdown` qué contexto llega realmente. Si el contexto SÍ contiene la respuesta y el modelo se rehúsa, es un límite de síntesis del LLM: reformula hacia lo concreto ("¿qué valor tiene el atributo Persistent de X?" en vez de "¿en qué tabla se guarda X?").
3. Reformula con el [patrón verificado](guia-cli.md#cómo-formular-buenas-preguntas-patrón-verificado-empíricamente): entidades nombradas + relaciones/reglas, en el idioma de los identificadores del corpus.
4. Prueba `--rerank`: si el chunk correcto está en el pool ampliado (3×TopK) pero fuera del
   TopK final por un mal ranking RRF, el Cross-Encoder suele subirlo. Requiere el modelo
   descargado (`bash infra/download-model.sh reranker`) — `rag doctor` avisa si falta.

### 0 resultados en `rag search`

- `--min-score` demasiado alto para la escala real (relevante ≈ 0.12–0.25). Prueba `-s 0`.
- Colección equivocada (`rag status --all`).

### `exit code 134` / `mutex lock failed: Invalid argument` al terminar

Ruido de teardown de ONNX Runtime en Apple Silicon, **posterior** a completar el trabajo (la ingesta/búsqueda ya terminó — verifícalo en el log). Mitigado en `Program.cs` con dispose asíncrono + `Task.Delay(300)`.

### `--rerank` lento o falla con "model not found"

El re-ranker carga su modelo de forma perezosa (`Lazy<T>`) — solo al primer uso de `--rerank`,
no al arrancar la CLI. Si falla con `FileNotFoundException`, descarga el modelo con
`bash infra/download-model.sh reranker` y confirma la ruta en la sección `CrossEncoder` de
`appsettings.json`. La latencia esperada es de cientos de ms a pocos segundos según el TopK
(cada candidato del pool 3×TopK paga una inferencia completa) — medido: pool de 15 candidatos
en ~700ms sobre el modelo int8 ARM64.

### Ingesta lenta

1. Confirma en el log que el lote usa el modelo **int8** (`model_qint8_arm64.onnx`), no fp32 (~2.3× más lento).
2. El costo dominante es ONNX; ver palancas en [pipeline-de-ingesta.md](pipeline-de-ingesta.md#rendimiento-medido).
3. Qdrant con disco lleno o CPU saturada degrada los upserts (aunque `wait:false` los saca de la ruta crítica).

### `SQLite Error 11: 'database disk image is malformed'`

La caché de resúmenes está **corrupta en disco**; no es transitorio y no se cura reiniciando. Toda
petición a `/api/search` y `/api/ask` revienta en `SummaryCache.TryGetAsync`. Causa conocida:
escritor nativo en el host (una ingesta con `--con-resumen`) y lector dentro del contenedor sobre
el mismo archivo en WAL, a través del bind mount de Docker — ver el aviso de la sección
Infraestructura.

Diagnóstico y recuperación (`.recover` rescató 21.082 de ~21.205 filas, 99,4 %):

```bash
D=~/Library/Application\ Support/rag-engine
sqlite3 "$D/summary-cache.sqlite3" "PRAGMA integrity_check;"   # confirma la corrupción

docker stop rag-api                                            # sin lectores durante el swap
cp "$D/summary-cache.sqlite3" /tmp/cache.corrupt.bak            # SIEMPRE respaldar primero
sqlite3 /tmp/cache.corrupt.bak ".recover" > /tmp/recover.sql
rm -f /tmp/rebuilt.sqlite3 && sqlite3 /tmp/rebuilt.sqlite3 < /tmp/recover.sql

sqlite3 /tmp/rebuilt.sqlite3 "PRAGMA integrity_check; SELECT count(*) FROM resumen_cache;"
mv "$D/summary-cache.sqlite3" "$D/summary-cache.sqlite3.corrupt-$(date +%Y%m%d)"
rm -f "$D"/summary-cache.sqlite3-wal "$D"/summary-cache.sqlite3-shm
cp /tmp/rebuilt.sqlite3 "$D/summary-cache.sqlite3"
docker start rag-api
```

Las filas basura que `.recover` arrastra las descarta el `INSERT OR IGNORE` del volcado. Perder
entradas sólo cuesta regenerar esos resúmenes con Ollama en la siguiente ingesta; no hay pérdida
de datos irrecuperable.

### `dense vector must not be empty` e `Indexed: 0`

Todos los lotes rechazados por Qdrant y la colección sin actualizar. Ocurría al **re-ingestar de
forma incremental una colección que ya tenía resúmenes**: Qdrant ≥ 1.14 devuelve el denso en el
oneof `dense` y deja vacío el campo plano legacy `VectorOutput.Data`, que era el que se leía —
daba `float[0]` en vez de `null`, y ese vector vacío se reenviaba en el upsert. Como el upsert es
atómico, se perdía el lote entero, puntos sanos incluidos.

Corregido en `a341e06`. Si vuelves a verlo tras actualizar el cliente o el servidor de Qdrant,
sospecha del mismo patrón en cualquier lectura nueva de vectores. La guarda de "generó chunks pero
no indexó ninguno" hace que hoy la ingesta aborte en vez de reportar éxito.

### Falla la compilación de proyectos NUEVOS fuera del repo

El SDK .NET de la máquina puede tener un workload set corrupto (`dotnet workload repair`). Este repo compila porque `Directory.Build.props` fija `MSBuildEnableWorkloadResolver=false`; copia ese archivo a proyectos auxiliares.

### `--force` en scripts/CI

El prompt de confirmación exige TTY, pero la CLI ya lo detecta y aborta con un mensaje explícito
(`Agrega --yes para confirmar sin preguntar`) en vez de colgarse. En automatización:
`rag ingest ... --force --yes`. Ya no hace falta el rodeo de borrar la colección por REST.

## Checklist de despliegue en una máquina nueva

1. .NET 10 SDK + Docker + (opcional) Ollama con `qwen2.5-coder`.
2. `docker compose -f infra/docker-compose.yml up -d`
3. `bash infra/download-model.sh` y ajustar rutas en `appsettings.json` si difieren.
4. (Opcional) `bash infra/download-model.sh reranker` si vas a usar `--rerank`.
5. `dotnet build && rag doctor`
6. `rag ingest <repo> -c <colección>` y una búsqueda de humo en ambos idiomas.
