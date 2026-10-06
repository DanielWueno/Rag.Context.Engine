# docs/eval/18.2/ — evidencia histórica, NO es la baseline vigente

Estos archivos se portaron **byte a byte** (verificado con `git hash-object`/sha256
contra el commit local `595340a20a832fbd16a21e3471595f4abd090d97`, ítem `18.2` de la
rama local `feat/rag-api-selector-coleccion`, ahora `21.2-poblar-colecciones-en-x64`
en el ledger de este repo) por el ítem `10.6.3-portar-ambiente-dev-qa-x64`. Ningún
byte de su contenido se modificó al portarlos; este `README.md` es el único archivo
nuevo de este directorio.

## Esto es "contrato 2", no la medición vigente

La corrida que documentan estos archivos comparó recall@10 de `micro-repo` contra
`docs/eval/baselines/tickets-microservice.norerank.baseline.json` — una baseline de
una generación de contrato DE RETRIEVAL anterior a la vigente en `main`. Desde
entonces, `main` avanzó el contrato de retrieval/chunking varias veces más (ver
5.e, 8.f y 5.d del ledger) y fijó su propia baseline vigente de contrato v3 en
`docs/eval/quality/5.e/`.

**No uses estos números para decidir si el ambiente x64 actual degrada recall.**
La medición contra la baseline vigente de contrato v3 es el ítem
`21.5-medir-recall-x64-contra-baseline-5e` del ledger (bloqueado por este ítem,
deliberadamente no tocado aquí). El propio `_resultado_original_local` de
`21.2-poblar-colecciones-en-x64` ya señala el hallazgo abierto relevante: el
`eval_set_hash` de esta corrida (`2ae380bb02df`) coincide con el de la baseline
vigente de `5.e`, lo que debilita (no confirma) la explicación de `autocrlf` que
esa misma corrida dio para su propio delta de hash contra
`tickets-microservice` — dos comparaciones de hash distintas que no deben
confundirse entre sí. Revisarlo es alcance de `21.5`, no de este directorio.

## Qué contiene cada archivo

| Archivo | Contenido |
|---|---|
| `collections.x64.json` | Colecciones reportadas por `rag-api` tras la ingesta (`rag-engine`, `micro-repo`). |
| `ingest-rag-engine.x64.log` / `ingest-micro-repo.x64.log` | Log completo de `rag ingest` para cada colección. |
| `puntos-colecciones.x64.txt` | Conteo de puntos por colección, verificado vía Qdrant REST. |
| `micro-repo.x64.eval.json` / `.txt` | Salida de `rag eval` (recall@10) para `micro-repo`, JSON y texto. |
| `quality-baseline.x64.json` / `.txt` | Salida de `infra/quality-baseline.py` sobre ambas colecciones. |
| `verificacion.txt` | Salida de `infra/qa/verificar-colecciones-x64.ps1` en su corrida original. |

Procedencia completa (modelo, hashes, limitaciones) en el campo
`_resultado_original_local` de `21.2-poblar-colecciones-en-x64` en
`docs/analisis-futuro/ejecucion-plan.estado.json`.
