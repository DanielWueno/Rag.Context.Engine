# Notas sobre la reconstrucción de comandos de ingesta

No existe historial de shell (`~/.zsh_history` no tiene ninguna línea con
`ingest`) ni un log de auditoría completo desde el día 1 — los comandos de
`scripts/04-ingest-collections.sh` se reconstruyeron investigando
`logs/rag-engine-*.json`, `logs/rag-api-*.json` y `docs/analisis-futuro/*.md`.
El nivel de confianza es distinto por colección — repetido aquí en más detalle
que en los comentarios inline del script:

| Colección | Ruta fuente | `--con-resumen` | Confianza | Comando literal encontrado |
|---|---|---|---|---|
| `bsuite-repo` | `BusinessSuite.Xaf` (repo completo) | Sí | **Alta** | Sí — `docs/analisis-futuro/busqueda-libre-rag-3-bandas.md:701` |
| `bsuite-auditorias-test` | 3 subcarpetas de `BusinessSuite.Xaf` | Sí | **Alta** | No, pero reconstruido de 3 logs "Starting ingestion" con todos los campos |
| `innovapp-docs` | `docs-bsute-innovapp-plan` | No | Media | No — ingesta anterior a los logs retenidos |
| `micro-repo` | `Reyma.TI.Tickets.Microservice` | No | Media | No — ídem, y la colección actual está rota (ver abajo) |
| `wiki-solis` | `Business-Suite` | No | Media | No — ídem |
| `rag-engine` | Este mismo repo (auto-ingesta) | No | Media-alta | No, pero `--repo-name RagEngine` confirmado por payload |
| `engine-repo` | **Desconocida — irrecuperable** | Desconocido | — | No |

"Media" significa: la ruta fuente y el estado de `--con-resumen` están
confirmados por evidencia indirecta sólida (paths reales en payloads de
búsqueda, o el conteo de `resumen_pending` en los resultados), pero no hay un
log del comando de ingesta en sí, así que flags como `--force`, `--lang` o
`--batch-size` no confirmados se dejan en su default.

## `engine-repo`: por qué se excluye

Cada consulta logueada contra `engine-repo` (`logs/rag-api-2026080{3,7}.json`)
falla con:

```
Grpc.Core.RpcException: Status(StatusCode="InvalidArgument",
  Detail="Wrong input: Not existing vector name error: dense")
```

Esto significa que el esquema de vectores de esa colección quedó desactualizado
respecto al código actual (mismo problema documentado para `micro-repo` en
`docs/analisis-futuro/modo-respuesta-simple-codigo.md:52`, pero `micro-repo` sí
tiene su ruta fuente confirmada — `engine-repo` no tiene ningún payload
recuperable porque la colección nunca responde). No hay ningún `file_path`,
`repository_name`, ni log de ingesta que sobreviva para esta colección — es
irrecuperable con la evidencia disponible. Recomendación: no intentar
recrearla; si hace falta, habría que preguntarle directamente al usuario qué
repo era.

## `micro-repo`: colección rota pero ruta conocida

Mismo síntoma de esquema roto que `engine-repo`, pero aquí SÍ hay evidencia
recuperable de la ruta fuente (comentarios `// Repository:` / `// File:` en
payloads de búsqueda de `logs/rag-engine-20260730.json`, antes de que se
rompiera). `scripts/04-ingest-collections.sh` la re-ingesta con `--force`,
lo que de paso corrige el esquema desactualizado.
