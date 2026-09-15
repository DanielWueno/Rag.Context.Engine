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

## Diagnóstico de drift de fuente (ronda siguiente, misma sesión)

Antes de decidir cómo reentrar, se verificó la existencia en disco de cada
`SourceFile` de AMBOS eval-sets contra el árbol actual de BusinessSuite.Xaf
(`docs/eval/quality/5.b/diagnostico-drift-fuente.json`):

- **`bsuite-repo`: 47/47 preguntas respondibles vivas (100%)** — el corpus que
  generó ese baseline sigue intacto en el árbol actual, con un único root
  (`/Users/DevStudio/Documents/Projects/BusinessSuite.Xaf`). No hace falta
  recuperar ningún commit histórico para este set.
- **`bsuite-auditorias`: 0/15 vivas (100% muertas)** — confirma que el bloqueo de
  la ronda anterior es real: el módulo completo fue eliminado del árbol fuente.
  Este set queda **fuera de alcance** de 5.b; necesitaría su propia investigación
  (¿la feature de auditorías fue removida del producto? ¿hay un fork/rama que la
  conserve?) antes de poder evaluarse.

**Decisión**: proceder con el A/B real de 5.b sobre `bsuite-repo` (corpus vivo,
sin drift), dejando `bsuite-auditorias` documentado como no evaluable hoy.

## Resultado del A/B real sobre `bsuite-repo` — FAIL decisivo

Se ejecutó una ingesta `PerFile` completa y real (Ollama `qwen2.5-coder` local,
caché SQLite aislada, colección Qdrant aislada `bsuite-repo-5b-perfile`) sobre
los 22.600 chunks de `bsuite-repo`. Duración: **02:56:29**. Llamadas LLM reales:
**2.202** (grupos únicos archivo/tipo, `prompt_version=4dab4480`) — factor de
reducción real **10.26×** (22.600/2.202), confirmando la hipótesis de costo
"÷~10" de la ficha con datos reales.

`rag eval` + `comparar.py` contra el baseline histórico
(`docs/eval/baselines/bsuite-repo.norerank.baseline.json`, mismo corpus, sin
drift):

| Categoría | Control | Candidato (PerFile) |
|---|---|---|
| Respondibles (total) | 19/47 | **7/47** |
| literal | 7/12 | 1/12 |
| parafraseada | 8/18 | 3/18 |
| ambigua | 2/5 | 1/5 |
| símbolo | 2/12 | 2/12 |
| fuera-de-dominio (negativo) | 0/8 | 0/8 |

**Pérdida neta: 12 preguntas @10** (umbral de la ficha: ≤1). `comparar.py`
reporta `FAIL`. Verificado con muestreo manual que no es un bug de
instrumentación: los `top_score` de preguntas puntuales cayeron de forma
consistente entre control y candidato para las mismas preguntas.

**Causa probable** (hipótesis razonada, no verificada exhaustivamente): con
`weight_resumen=2.5` en la fusión ponderada (RRF sobre canales
código/sparse/resumen), el canal de resumen aporta el mayor peso de la señal.
Al compartir el mismo vector de resumen entre TODOS los chunks de un
archivo/clase, ese canal deja de discriminar entre chunks individuales del
mismo archivo (métodos/propiedades distintas quedan indistinguibles para el
canal de resumen), degradando severamente el ranking fino que el resumen por
chunk sí aportaba.

**Conclusión**: la hipótesis de 5.b (reducir el costo de Ollama agrupando
resúmenes por archivo/tipo sin perder recall) queda **refutada** con evidencia
real. El ahorro de costo (10.26× menos llamadas LLM) no compensa la pérdida de
calidad. `SummaryGranularity=PerChunk` permanece como default y único modo
recomendado en producción. `SummaryGranularity=PerFile` queda en el código
como modo opt-in ya implementado y testeado (243/243 tests), mantenido solo
como referencia histórica/experimental — **no promovido**.

Evidencia archivada en `docs/eval/quality/5.b/bsuite-repo-ab/`
(`candidato.perfile.json`, `comparador-salida.txt`, `instrumentacion.json`) y
`docs/eval/quality/5.b/diagnostico-drift-fuente.json`. Artefactos experimentales
(colección Qdrant, caché SQLite temporal) limpiados tras extraer la evidencia.

El ítem se cierra como `descartado` (no `bloqueado`, no `hecho`): se obtuvo un
resultado real y conclusivo — no una imposibilidad de medir.

---

## Historial: por qué el ítem quedó `bloqueado` en la ronda anterior

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

## Condición de reentrada (histórica — ya no aplica, ver arriba)

La condición descrita abajo dejó de ser el camino elegido: en vez de recuperar
un commit histórico o re-congelar un baseline, se diagnosticó que `bsuite-repo`
no tenía drift (100% vivo) y se ejecutó el A/B directamente sobre el árbol
actual, sin tocar anclas. `bsuite-auditorias` sigue con el mismo bloqueo (0%
vivo) y sigue fuera de alcance de 5.b.

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
