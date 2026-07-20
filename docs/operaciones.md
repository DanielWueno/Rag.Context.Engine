# Operaciones

> Runbook: infraestructura, observabilidad y resolución de los problemas conocidos.

## Infraestructura

```bash
docker compose -f infra/docker-compose.yml up -d    # Qdrant (gRPC 6334, REST 6333)
curl -s http://localhost:6333/collections           # sanity check REST
ollama serve                                        # requerido solo para `rag ask`
```

`rag doctor` valida todo el stack en un comando (Qdrant, modelo ONNX, tokenizador, disco).

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

### Falla la compilación de proyectos NUEVOS fuera del repo

El SDK .NET de la máquina puede tener un workload set corrupto (`dotnet workload repair`). Este repo compila porque `Directory.Build.props` fija `MSBuildEnableWorkloadResolver=false`; copia ese archivo a proyectos auxiliares.

### `--force` se cuelga en scripts/CI

El prompt de confirmación de Spectre.Console exige TTY. En automatización: `curl -X DELETE http://localhost:6333/collections/<n>` y corre `ingest` sin `--force`.

## Checklist de despliegue en una máquina nueva

1. .NET 10 SDK + Docker + (opcional) Ollama con `qwen2.5-coder`.
2. `docker compose -f infra/docker-compose.yml up -d`
3. `bash infra/download-model.sh` y ajustar rutas en `appsettings.json` si difieren.
4. (Opcional) `bash infra/download-model.sh reranker` si vas a usar `--rerank`.
5. `dotnet build && rag doctor`
6. `rag ingest <repo> -c <colección>` y una búsqueda de humo en ambos idiomas.
