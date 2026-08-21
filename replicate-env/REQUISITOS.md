# Requisitos para replicar el entorno

## Software

| Requisito | Versión / detalle | Notas |
|---|---|---|
| .NET SDK | 10 (net10.0) | `dotnet --version` |
| Docker | cualquiera reciente con soporte de bind mounts | Para Qdrant |
| Ollama | https://ollama.com | Corre NATIVO en el host, no en Docker (para conservar aceleración GPU/Metal — ver comentario en `infra/docker-compose.yml`) |
| Modelo Ollama | `qwen2.5-coder` | `ollama pull qwen2.5-coder` |
| Python 3 | cualquiera con json/urllib (stdlib) | Solo para `infra/verify-simple-mode.py` |
| git | — | Para clonar los repos fuente |

## Credenciales

- **Azure DevOps** (org `GR-PlasticosAdministrativo`): acceso de lectura a los
  repos `BusinessSuite/BusinessSuite.Xaf` y
  `Reyma.InnovApp/Reyma.TI.Tickets.Microservice`. Necesitas un Personal Access
  Token (PAT) con permiso `Code: Read` configurado en tu git credential
  helper, o el clone en `03-clone-source-repos.sh` va a pedir usuario/password
  interactivamente (o fallar si no tienes acceso a esa org).
- Los otros dos repos fuente (`docs-bsute-innovapp-plan`,
  `Business-Suite`) son públicos en GitHub — no requieren credenciales.

## Disco

| Item | Tamaño aprox. |
|---|---|
| Modelos ONNX (multilingual + reranker) | ~1.2 GB |
| Qdrant storage (todas las colecciones, estado actual) | ~304 MB |
| Repos fuente clonados (BusinessSuite.Xaf es el más grande) | varía, deja unos cuantos GB libres |
| `data/summary-cache.sqlite3` (ya incluido en esta carpeta) | 7.8 MB |

## Puertos usados

| Puerto | Servicio |
|---|---|
| 6333 | Qdrant REST |
| 6334 | Qdrant gRPC |
| 11434 | Ollama (asumido default, `Ollama:Endpoint` en appsettings.json) |
| 5080 | RagEngine.Api (si corres la API, no solo el CLI) |

## Configuración a ajustar

`src/RagEngine.Api/appsettings.json` y `src/RagEngine.Cli/appsettings.json`
tienen rutas absolutas de la máquina original (`OnnxBrain:ModelPath`,
`CrossEncoder:ModelPath`, etc. apuntan a `/Users/DevStudio/models/...`).
Ajusta esas rutas a donde `scripts/01-download-models.sh` haya descargado los
modelos en la nueva máquina (por defecto, `./models/` relativo a la raíz del
repo — no coincide con la ruta absoluta que hay hoy en appsettings.json,
edítala).

Si vas a correr la API vía Docker (`infra/docker-compose.yml`), los volúmenes
ahí también apuntan a rutas absolutas de `/Users/DevStudio/...` — edítalos
igual antes de `docker compose up`.
