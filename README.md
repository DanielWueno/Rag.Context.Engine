# RagEngine — Universal Local RAG Context Engine

> Motor de contexto semántico **100% local, offline y privacy-first** para repositorios de código enterprise.
> Búsqueda híbrida (densa + dispersa) multilingüe **ES/EN**, con respuestas generadas por un LLM local.

| | |
|---|---|
| **Runtime** | .NET 10 · C# 13 |
| **Embeddings** | ONNX Runtime + `paraphrase-multilingual-MiniLM-L12-v2` (int8, 384 dims, 50+ idiomas) |
| **Vector DB** | Qdrant (Docker, gRPC) — esquema dual denso + disperso con fusión RRF |
| **Generación** | Semantic Kernel + Ollama (`qwen2.5-coder`) |
| **CLI** | Spectre.Console |

---

## Quick Start

**Requisitos:** .NET 10 SDK · Docker · ~1 GB de disco para el modelo · (opcional para `rag ask`) [Ollama](https://ollama.com) con `qwen2.5-coder`.

```bash
# 1. Levantar Qdrant
docker compose -f infra/docker-compose.yml up -d

# 2. Descargar el modelo de embeddings (multilingüe, por defecto)
bash infra/download-model.sh

# 3. Compilar
dotnet build

# 4. Indexar un repositorio
dotnet run --project src/RagEngine.Cli -- ingest /ruta/a/tu/repo --collection mi-repo

# 5. Buscar y preguntar (funciona en español e inglés)
dotnet run --project src/RagEngine.Cli -- search "¿cómo se validan los pedidos?" -c mi-repo
dotnet run --project src/RagEngine.Cli -- ask "¿Qué reglas aplican al guardar una orden?" -c mi-repo
```

> ⚠️ **Nota sobre `--min-score`:** el umbral se aplica a la similitud coseno de la rama densa.
> Con el modelo multilingüe, los pares pregunta↔código relevantes puntúan **~0.12–0.25**;
> el default es `0.10`. No uses valores "clásicos" como 0.65 — vaciarías el retrieval.

## Comandos

| Comando | Descripción |
|---|---|
| `rag ingest <path>` | Indexa un repositorio en Qdrant (denso + disperso) |
| `rag search <query>` | Búsqueda híbrida semántica; salida rich/markdown/json |
| `rag ask <query>` | Pregunta en lenguaje natural → respuesta del LLM local con citas |
| `rag status` | Estadísticas de las colecciones |
| `rag doctor` | Diagnóstico de dependencias (Qdrant, ONNX, disco) |

Referencia completa de opciones: **[docs/guia-cli.md](docs/guia-cli.md)**.

## Documentación

| Documento | Contenido |
|---|---|
| [docs/arquitectura.md](docs/arquitectura.md) | Componentes del núcleo, capas, mapa de dependencias |
| [docs/busqueda-hibrida.md](docs/busqueda-hibrida.md) | Rama densa multilingüe, tokenización dispersa, fusión RRF y semántica de scores |
| [docs/pipeline-de-ingesta.md](docs/pipeline-de-ingesta.md) | Flujo scanner → chunking → vectorización → Qdrant; concurrencia y rendimiento |
| [docs/guia-cli.md](docs/guia-cli.md) | Referencia completa de comandos y opciones |
| [docs/configuracion.md](docs/configuracion.md) | `appsettings.json`, gestión de modelos, cuándo re-ingestar |
| [docs/operaciones.md](docs/operaciones.md) | Logs, métricas, troubleshooting y runbook |
| [arnes-plan](https://github.com/DanielWueno/arnes-plan) | **Arnés de ejecución de planes.** Cómo se trabaja aquí en tareas largas con un asistente: el ledger, los comandos, un ítem por sesión limpia. Ya no vive en este repo: es un plugin de Claude Code (`claude plugin marketplace add DanielWueno/arnes-plan`). Lo que sí es de aquí es el ledger, en `docs/analisis-futuro/ejecucion-plan.estado.json`. |

## Estructura de la solución

```
Rag.Context.Engine/
├── infra/                  # docker-compose (Qdrant) + descarga de modelos
│   └── arnes/              # Arnés de ejecución de planes (portable, con guía e instalador)
├── docs/                   # Documentación formal del sistema + Plan de proyecto (Fase 1..5)
├── src/
│   ├── RagEngine.Core/     # Toda la lógica: abstracciones, dominio, pipeline,
│   │                       # chunking (Roslyn/TS/Markdown), vectorización, Qdrant
│   ├── RagEngine.Api/      # API HTTP: endpoints de búsqueda y generación
│   └── RagEngine.Cli/      # Entry point: comandos Spectre.Console
├── tests/                  # Suite de tests unitarios
├── poc/                    # Proof-of-concepts (búsqueda libre, etc.)
└── replicate-env/          # Ambiente de replicación con cache de resúmenes
```

## Principios

1. **Local-first:** ningún dato sale de tu máquina; sin llamadas a APIs externas.
2. **Multilingüe simétrico:** consulta en español o inglés contra cualquier corpus — la normalización léxica y el modelo denso tratan ambos idiomas por igual.
3. **Determinista:** IDs de chunk basados en contenido; re-ingestas idempotentes.
4. **Observable:** logs estructurados (Serilog JSON), métricas y `rag doctor`.
