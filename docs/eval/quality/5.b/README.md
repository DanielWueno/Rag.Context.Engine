# Ítem 5.b — resumen por archivo/tipo: evidencia y bloqueo

## Qué se implementó (código, no experimental de infraestructura)

- `IngestionOptions.SummaryGranularity` (`PerChunk` default / `PerFile` opt-in).
- `IBusinessSummaryGenerator.GenerateForGroupAsync` + `OllamaBusinessSummaryGenerator`:
  una sola llamada al LLM por grupo (archivo, tipo), con su propio system prompt
  (`GroupSystemPrompt`) y su propio namespace de caché (`GroupPromptVersion` /
  `ComputeGroupPromptVersion`), que NUNCA colisiona con el modo por chunk aunque
  compartan el mismo archivo SQLite.
- `SummaryCache.TryGetAsync`/`SetAsync` aceptan `promptVersionOverride` para vivir
  varios namespaces en la misma base sin migración.
- `DefaultIngestionPipeline.RunResumenPhasePerFileAsync`: agrupa por
  `(RelativeFilePath, ClassName)`, hace una llamada por grupo, reutiliza el resultado
  para todos los chunks del grupo (mismo vector de resumen), e instrumenta
  `Groups/LlmCalls/CacheHits/CacheMisses/ElapsedMs` — expuestos en `IngestionSummary`
  (`ResumenGranularity`, `ResumenGroups`, `ResumenLlmCalls`, `ResumenCacheHits`,
  `ResumenCacheMisses`, `ResumenElapsedMs`).
- Tests: `tests/RagEngine.Core.Tests/ResumenPorArchivoTests.cs` (hash de grupo
  determinista e insensible al orden, no colisiona con content_hash de chunk,
  prompt_version de grupo distinta y determinista) y dos casos nuevos en
  `SummaryCacheTests.cs` para el namespace por override. Suite completa: 243/243 OK.

## Preparación de verificación (exigida por la ficha antes de generar resúmenes)

- `contar-reduccion.py`: cuenta, sobre una colección YA indexada, chunks vs grupos
  únicos archivo/tipo — mide el factor de reducción real de llamadas LLM SIN gastar
  ningún cómputo de Ollama. Resultado real medido:
  - `bsuite-repo`: 22.592 chunks → 2.268 grupos (factor **9.96×**, confirma la
    hipótesis "÷~10" de la ficha con datos reales, no supuestos).
  - `bsuite-auditorias-test`: 1.677 chunks → 356 grupos (factor **4.71×**).
- `comparar.py`: comparador A/B con el umbral literal de la ficha (≤1 pregunta neta
  perdida @10 por set, excluyendo `fuera-de-dominio` del cómputo de respondibles pero
  alertando si un negativo gana un hit@10 que no tenía). Validado con dos
  autocomparaciones (mismo baseline dos veces): `PASS`, 0 pérdida neta, en
  `bsuite-repo` (19/47 total, 2/12 símbolo — coincide con la línea base congelada en
  11.4) y en `bsuite-auditorias` (11/16).

## Por qué el ítem queda `bloqueado` (no `hecho`)

Se lanzó una ingesta real en modo `PerFile` (`Ingestion__SummaryGranularity=PerFile`,
caché SQLite aislada) sobre el subconjunto de auditorías, reconstruyendo primero los
tres directorios raíz que componen `bsuite-auditorias-test`
(`REYMA.XAFR1PV.Base`, `REYMA.XAFR1PV.Compras/Auditorias`,
`REYMA.XAFR1PV.Compras/Compras/Utils`, identificados diffando `file_path` menos
`relative_path` sobre los 1.677 puntos de la colección control). El primer directorio
reingestó correctamente (1.313 chunks → 292 grupos, factor 4.5×, consistente con lo
esperado). El segundo (`REYMA.XAFR1PV.Compras/Auditorias`) **ya no existe en el
árbol de trabajo actual de BusinessSuite.Xaf**: el módulo fue eliminado/reorganizado
(ver `git log --diff-filter=D -- '**/Auditorias/**'` en ese repo, con borrados desde
marzo hasta julio de 2026, incluyendo el commit `51835cf8` del 4 de julio). Los 16
archivos que el eval-set de `bsuite-auditorias` referencia como `SourceFile`
(`AuditoriaResultadoHallazgo.cs`, `Auditorias.cs`, etc.) viven todos en ese módulo
desaparecido — por eso una primera corrida contra solo el directorio reconstruible
dio 0/16 en el comparador: no es un fallo de la granularidad por archivo/tipo, es que
el corpus fuente que el eval-set congelado asume ya no está en disco.

**Esto bloquea CUALQUIER A/B honesto de 5.b hoy** (no solo el de auditorías: el mismo
árbol de trabajo es la fuente de `bsuite-repo`, así que probablemente tenga el mismo
problema para las preguntas que referencian archivos ya movidos/borrados). Reingestar
desde el árbol actual mediría una granularidad distinta contra un corpus distinto del
que generó los baselines — exactamente lo que el protocolo del proyecto prohíbe
("no modificar anclas/corpus para favorecer un resultado"; "si el instrumento cambia
justificadamente, registrar el cambio y medir ambos brazos con la misma
procedencia").

## Condición de reentrada

Antes de poder ejecutar el A/B pre-registrado de 5.b hace falta, en este orden:
1. Recuperar el commit/tag exacto de BusinessSuite.Xaf usado para congelar
   `bsuite-repo`/`bsuite-auditorias-test` (o aceptar explícitamente re-congelar un
   nuevo baseline PerChunk sobre el árbol actual, con su propio hash de procedencia,
   y comparar PerFile contra ESE nuevo control — nunca contra el histórico).
2. Si se opta por (1), un `git worktree`/checkout de ese ref hacia un directorio
   temporal permite re-ingestar sin tocar el checkout de trabajo actual.
3. Repetir la ingesta PerFile con la caché aislada de este directorio, y correr
   `comparar.py` contra el baseline correspondiente a esa procedencia.

Sin (1) o (2) resueltos, cualquier número que se reporte como "el A/B de 5.b dio X"
sería una comparación entre corpus distintos disfrazada de comparación de
granularidad — se documenta el bloqueo en vez de fabricarlo.
