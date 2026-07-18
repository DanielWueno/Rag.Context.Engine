# Guía de la CLI

> Referencia completa de comandos. Todos se invocan como `dotnet run --project src/RagEngine.Cli -- <comando>` (o `rag <comando>` si publicas el binario).

## `rag ingest <path>` — Indexar un repositorio

```bash
rag ingest /ruta/al/repo --collection mi-repo --repo-name MiRepo
```

| Opción | Default | Descripción |
|---|---|---|
| `<path>` | — | Raíz del repositorio a indexar |
| `-c, --collection` | `rag-engine` | Colección destino en Qdrant |
| `-r, --repo-name` | `my-repo` | Nombre usado en los encabezados de contexto de cada chunk |
| `-b, --batch-size` | `32` | Chunks por lote de inferencia ONNX |
| `-f, --force` | off | Borra y recrea la colección (confirmación interactiva) |
| `-l, --lang` | todos | Restringe a un lenguaje: `csharp`, `typescript`, `sql`, `markdown` |

> Automatización/CI: `--force` exige TTY. Para scripts, borra la colección vía API REST
> (`curl -X DELETE http://localhost:6333/collections/<nombre>`) y corre `ingest` sin `--force`.

## `rag search <query>` — Búsqueda híbrida

```bash
rag search "¿cómo se validan los pedidos?" -c mi-repo -k 10
rag search "AuditoriaResultadoHallazgo reglas" -c mi-repo -o markdown
```

| Opción | Default | Descripción |
|---|---|---|
| `-c, --collection` | `default` | Colección a consultar |
| `-k, --top-k` | `10` | Máximo de chunks devueltos |
| `-s, --min-score` | `0.10` | Umbral de coseno para la rama densa — ver [escala](busqueda-hibrida.md#escala-de-min-score-denso-coseno) |
| `-l, --language` | — | Filtro por lenguaje del chunk |
| `-n, --namespace` | — | Filtro por namespace |
| `-r, --rerank` | off | Amplía el pool para re-ranking (3× TopK) |
| `-o, --output` | `rich` | `rich` (paneles), `markdown` (listo para prompt), `json` (integración) |
| `--max-tokens` | `8000` | Presupuesto del ensamblado markdown |

> El porcentaje mostrado en la salida rich es el **score RRF** (tope ~0.5), no una similitud coseno.

## `rag ask <query>` — Pregunta con respuesta del LLM

Requiere Ollama corriendo con el modelo configurado (`qwen2.5-coder` por defecto).

```bash
rag ask "¿Qué condiciones aplican para registrar un hallazgo en una auditoría?" -c mi-repo -k 10
rag ask "Explain the ingestion pipeline" -c rag-engine --no-stream
```

| Opción | Default | Descripción |
|---|---|---|
| `-c, --collection` | `default` | Colección a consultar |
| `-k, --top-k` | `5` | Chunks recuperados para el contexto |
| `-s, --min-score` | `0.10` | Umbral denso (misma semántica que `search`) |
| `--no-stream` | off | Respuesta completa en panel en lugar de streaming token a token |

La respuesta llega **en el idioma de la pregunta**, citando archivo y rango de líneas. Si el contexto recuperado no contiene la respuesta, el LLM lo dice explícitamente (grounding estricto) — en ese caso prueba a reformular, subir `-k`, o verificar que la colección esté ingestada con el motor actual.

## `rag status` — Estadísticas de colecciones

```bash
rag status --all          # todas las colecciones
rag status -c mi-repo     # una colección
```

## `rag doctor` — Diagnóstico de dependencias

```bash
rag doctor
```

Verifica conectividad con Qdrant, presencia y carga del modelo ONNX, tokenizador y espacio en disco. Es el primer comando a correr cuando algo falla — ver [operaciones.md](operaciones.md).

## Recetas

```bash
# Contexto en markdown para pegar en un prompt externo
rag search "manejo de errores del pipeline" -c rag-engine -o markdown --max-tokens 4000

# Integración programática (jq)
rag search "retry policy" -c rag-engine -o json | jq '.[].metadata.relative_file_path'

# Corpus multilingüe: la misma pregunta funciona en ambos idiomas
rag ask "¿qué reglas tiene una auditoría para ser creada?" -c bsuite-repo
rag ask "what rules must an audit satisfy to be created?" -c bsuite-repo
```
