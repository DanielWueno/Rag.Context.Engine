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
| `-f, --force` | off | Borra y recrea la colección (pide confirmación) |
| `-y, --yes` | off | Confirma `--force` sin preguntar; necesario sin terminal interactiva |
| `--con-resumen` | off | Genera el tercer vector de resumen de negocio vía LLM (Fase 2) |
| `-l, --lang` | todos | Restringe a un lenguaje: `csharp`, `typescript`, `sql`, `markdown` |

> **Automatización/CI:** `rag ingest ... --force --yes`. Sin `--yes` y sin TTY la CLI aborta con un
> mensaje explícito en vez de colgarse.
>
> **Sin `--force` la ingesta es incremental** y además borra los puntos que quedaron obsoletos
> (archivos editados cuyos chunks cambiaron de Id). Ver
> [pipeline-de-ingesta.md](pipeline-de-ingesta.md#idempotencia-y-re-ingesta).
>
> **Qué se indexa:** el escáner respeta `.gitignore` y `.ragignore` de la raíz del repositorio —
> ver [exclusiones del corpus](pipeline-de-ingesta.md#exclusiones-del-corpus--gitignore-y-ragignore).
>
> **`--con-resumen` baja `rag-api` primero:** la Fase 2 escribe la caché SQLite y compartirla con el
> contenedor la corrompe (ver [operaciones.md](operaciones.md#caché-de-resúmenes-de-negocio-sqlite)).

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
| `-r, --rerank` | off | Amplía el pool a 3×TopK y lo re-puntúa con el Cross-Encoder — ver [detalle](busqueda-hibrida.md#re-ranking-cross-encoder--onnxcrossencoderreranker) |
| `-o, --output` | `rich` | `rich` (paneles), `markdown` (listo para prompt), `json` (integración) |
| `--max-tokens` | `8000` | Presupuesto del ensamblado markdown |

> El porcentaje mostrado en la salida rich es el **score RRF** (tope ~0.5) sin `--rerank`, o el
> **sigmoide del Cross-Encoder** (0..1) con `--rerank` — no son comparables entre sí.

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
| `-r, --rerank` | off | Re-puntúa el pool con el Cross-Encoder antes de construir el contexto del LLM — sube precisión a costa de ~1-2s extra |
| `--no-stream` | off | Respuesta completa en panel en lugar de streaming token a token |

La respuesta llega **en el idioma de la pregunta**, citando archivo y rango de líneas. Si el contexto recuperado no contiene la respuesta, el LLM lo dice explícitamente (grounding estricto) — en ese caso prueba a reformular, subir `-k`, o verificar que la colección esté ingestada con el motor actual.

### Cómo formular buenas preguntas (patrón verificado empíricamente)

1. **Nombra las entidades por su identificador** (`AuditoriaResultadoHallazgo`, no "los registros de problemas") — el nombre exacto ancla la rama léxica dispersa.
2. **Pregunta por relaciones, reglas, condiciones o flujos** — es la información que viven en atributos declarativos y asociaciones, y que el prompt del sistema enseña al LLM a traducir (`[Persistent]` → tabla, `[RuleRequiredField]` → validación al guardar, `[Appearance]` → campos/acciones deshabilitados).
3. **Pregunta en el idioma de los identificadores del corpus** — la convergencia cross-lingüe del stemming solo ocurre cuando las raíces coinciden (`ingest`/`ingesta` sí; `import`/`importar` no), así que "the import process" no ancla la clase `ImportarAuditoria`.

```bash
# ⭐ Patrón ganador: entidades nombradas + relación/flujo
rag ask "¿Cuál es la relación entre AuditoriaResultado, AuditoriaResultadoHallazgo y AuditoriaResultadoHallazgoAccionCorrectiva? Describe el flujo." -c bsuite-repo -k 12

# ✅ Metadata declarativa (el atributo ES la respuesta)
rag ask "¿En qué tablas se persisten los hallazgos de auditoría y sus evidencias?" -c bsuite-repo
rag ask "¿Qué acciones o campos se deshabilitan cuando un hallazgo está Cancelado?" -c bsuite-repo
```

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
# Precisión extra cuando el LLM no logra sintetizar entre candidatos parecidos
rag ask "¿Qué condiciones aplican para registrar un hallazgo en una auditoría?" -c bsuite-repo --rerank

# Contexto en markdown para pegar en un prompt externo
rag search "manejo de errores del pipeline" -c rag-engine -o markdown --max-tokens 4000

# Integración programática (jq)
rag search "retry policy" -c rag-engine -o json | jq '.[].metadata.relative_file_path'

# Corpus multilingüe: la misma pregunta funciona en ambos idiomas
rag ask "¿qué reglas tiene una auditoría para ser creada?" -c bsuite-repo
rag ask "what rules must an audit satisfy to be created?" -c bsuite-repo
```
