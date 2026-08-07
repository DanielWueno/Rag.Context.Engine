# Arreglar el modo de respuesta "Simple" para colecciones de código

## Contexto

Esta semana se implementó un toggle `ResponseMode` (Simple/Technical) para que usuarios no
técnicos (soporte, QA, negocio) reciban respuestas sin código ni citas de archivo. El modo
Simple usa un único prompt (`SimpleSystemPromptTemplate` en `RagGenerationService.cs`) que
reemplaza los prompts Code/Docs existentes.

Al revisar 33 preguntas reales en modo Simple de esta semana (logs `logs/rag-api-202608*.json`),
se confirmó que **casi todas las preguntas sobre colecciones de código** (`bsuite-repo`) violan
las reglas del prompt: bloques ` ```csharp` crudos, nombres de clase/método/atributo
(`ServicioCliente`, `GenerarPlanAuditoria`, `[SupportedEstatus(...)]`), y frases como "según el
código proporcionado". Un primer intento de arreglarlo reforzando el prompt (reglas más
explícitas + ejemplo concreto BEFORE/AFTER) **se probó en vivo contra el contenedor reconstruido
y no funcionó** — se verificó con `docker cp` que el binario desplegado sí tenía el prompt nuevo,
y el modelo igual violó las reglas. Peor: una de las reglas nuevas ("no evadas, responde directo")
causó una regresión — en una pregunta donde antes el modelo correctamente decía "no hay evidencia
de que esto sea automático", con el prompt nuevo **inventó un método C# completo** que no existe
en el contexto, para "no sonar evasivo". Confirmado comparando contra el modo Technical con el
mismo contexto recuperado, que no fabricó nada.

**Hipótesis de trabajo (no certeza — n=1):** `qwen2.5-coder` — el único modelo de generación
configurado, para ambos modos — parece sesgado a responder en términos de código cuando el
contexto recuperado es código, resistiendo instrucciones explícitas incluso con ejemplo concreto.
Es la explicación más probable con lo que se probó hasta ahora, pero un solo intento fallido de
reforzar el prompt no la confirma como hecho — no se agotaron variantes más baratas (mover la
instrucción anti-código después del contexto, few-shot negativo más agresivo, bajar temperatura).
El plan de abajo no depende de que esta hipótesis sea 100% correcta: la Fase 1 es una red de
seguridad estructural que funciona sin importar la causa real.

## Hallazgos adicionales relevantes

- **El caché de resúmenes de negocio (`SummaryCache`/`Resumen`) ya existe** (Reto A, implementado
  hace semanas) y se genera con un prompt de ingesta *separado* (`OllamaBusinessSummaryGenerator`,
  `src/RagEngine.Core/Services/Summary/OllamaBusinessSummaryGenerator.cs`) que sí logra describir
  el código en lenguaje de negocio sin sintaxis — porque nunca compite contra una instrucción de
  "responde la pregunta del usuario using this code", es una tarea aislada de traducción.
  Ese prompt sí explicita "empieza nombrando la entidad/archivo, seguido de dos puntos" — por
  diseño cada resumen arranca con un prefijo tipo `ServicioCliente.cs:`, un leve resabio técnico
  pero muy lejos de exponer sintaxis/atributos.
- **Cobertura de `--con-resumen` es desigual entre colecciones.** Medido en vivo (`/api/search`,
  10 fuentes por colección):

  | Colección | Con resumen |
  |---|---|
  | bsuite-repo | 10/10 |
  | bsuite-auditorias-test | 10/10 |
  | wiki-solis | 0/10 |
  | rag-engine | 0/10 |
  | innovapp-docs | 0/10 (esperado: es documentación, ya es prosa) |
  | engine-repo, micro-repo | error pre-existente no relacionado ("Not existing vector name error: dense") |

  Esto no es solo un límite de cobertura — para `wiki-solis`/`rag-engine` (0% resumen), la regla
  de Fase 2 de "excluir chunks sin resumen" puede vaciar el contexto casi por completo en preguntas
  de código, cayendo al gate de no-grounding: el usuario pasaría de recibir una respuesta (aunque
  con código podado por la Fase 1) a recibir "no tengo evidencia" — una regresión funcional real,
  no solo cobertura parcial. Ver condición de aceptación de Fase 2 más abajo.
- El campo `Resumen` ya se expone directo al usuario en `sources` cuando `ResponseMode=Simple`
  (cambio de esta sesión, `SourceDto.Redacted`), con el prefijo `Entidad.cs:` sin limpiar — esto ya
  contradice el objetivo del modo Simple *hoy*, no es un problema que aparezca recién en Fase 2.
  Se mueve a Fase 1 (ver abajo) precisamente porque es una decisión de scope, no una necesidad
  técnica de esperar a que se toque ese código por otra razón.

## Opciones consideradas

| # | Opción | Garantiza cero código | Cobertura hoy | Costo | Riesgo |
|---|---|---|---|---|---|
| A | Filtro determinístico post-generación (regex que detecta y quita/reemplaza fences ``` ``` ``` e identificadores tipo código antes de mostrar la respuesta) | Sí, siempre | 100% de colecciones | Bajo-medio | Pierde streaming token-a-token en modo Simple (hay que bufferear para poder filtrar); puede dejar prosa cortada si el bloque de código era el contenido central de la respuesta; riesgo de falsos positivos sobre prosa legítima (nombres propios en PascalCase, términos compuestos) — no solo falsos negativos |
| B | Segunda pasada de reescritura (generar respuesta técnica completa, luego una llamada aparte que la reescribe en lenguaje simple, sin chunks de código en el contexto de esa segunda llamada) | Probablemente alto (el input ya es prosa, sin código que tiente al modelo) | 100% | Medio | 2x latencia (ya hay respuestas de 10-20s); pierde streaming igual que A; la segunda pasada podría fabricar de nuevo si no se acota bien |
| C | Modelo no-coder (`qwen2.5:7b-instruct`) solo para modo Simple | Incierto | 100% (no depende de resumen) | Medio (plumbing de 2 modelos) | Hay un precedente en memoria de que "ningún modelo gana limpio" comparando estos dos en vault de docs, pero esa medición era sobre *precisión leyendo código*, no sobre *suprimir sintaxis en la salida* — son tareas distintas; no es evidencia fuerte para descartar C, solo una razón para no priorizarla mientras haya rutas más baratas y ciertas (A, D) |
| D | Cambiar el contexto: para modo Simple, alimentar al LLM con el `Resumen` cacheado en vez del contenido crudo del chunk (garantía estructural — si el LLM nunca ve código, no puede filtrarlo) | Alto para chunks con resumen | Parcial (2/7 colecciones probadas) | Medio (nueva dependencia `SummaryCache` en `RagGenerationService`, lookup por chunk, fallback para chunks sin resumen) | No resuelve el problema para colecciones sin `--con-resumen`; la estrategia de fallback (excluir vs. degradar a contenido crudo) importa — ver Fase 2 |
| E | Corregir la regresión de la regla anti-evasión (revertir/acotar la parte de la regla 5 que causó la fabricación del método inventado) | N/A — es un fix de calidad, no de fuga de código | 100% | Muy bajo | Ninguno; es una corrección directa de un error introducido esta sesión |

## Enfoque recomendado (por fases)

**Fase 0 — Corregir la regresión (E), antes que nada.** Reescribir la regla 5 de
`SimpleSystemPromptTemplate` para que "no evadas cuando el contexto ya tiene la regla" no se lea
como "nunca hedgees" — debe seguir permitiendo el hedge legítimo (rule 2 / `LowConfidenceAddendum`)
cuando el contexto realmente no cubre el detalle preguntado, sin abrir la puerta a inventar una
implementación de ejemplo. Re-probar contra las preguntas que fabricaron contenido (la de "plan de
auditoría automático") para confirmar que vuelve a decir "no hay evidencia" sin inventar código.

**Fase 1 — Filtro determinístico (A) como red de seguridad universal**, más limpieza de scope y
una válvula de escape. Implementar:

1. **Filtro post-generación.** Post-proceso en el límite de salida para `ResponseMode.Simple` que:
   - Detecta y elimina bloques ` ``` ` completos (con un aviso genérico tipo "*(se omitió un
     fragmento técnico)*" solo si el bloque tenía contenido no vacío, para no dejar huecos raros
     en la prosa).
   - Detecta y sanitiza identificadores obvios en backticks o en `PascalCase.Method` /
     `snake_case` reconocibles como código (heurística conservadora — mejor dejar pasar algún
     falso negativo que destrozar prosa legítima).
   - Como el filtro necesita el texto completo para poder actuar sobre fences que abren/cierran en
     fragmentos distintos del stream, `ResponseMode.Simple` deja de emitir token-a-token: se
     buffer-ea la respuesta completa en `RagGenerationService.AskStreamingAsync` (o en el
     llamador) y se filtra antes de `yield return`. `ResponseMode.Technical` no se toca — sigue
     streameando igual que hoy.
   - Archivo principal: `src/RagEngine.Core/Services/Generation/RagGenerationService.cs` (nuevo
     método privado `SanitizeSimpleAnswer`, invocado en el punto donde hoy se hace
     `await foreach (var fragment in StreamAnswerAsync(...))` para el camino grounded).

2. **Limpieza del prefijo `Entidad.cs:` en `SourceDto.Redacted`** (movido desde Fase 2 — es un
   regex trivial, no depende de tocar `SummaryCache` desde `RagGenerationService`, y el resabio ya
   contradice el objetivo del modo Simple hoy).

3. **Flag de rollback sin rebuild.** Agregar `RagGenerationOptions.EnableSimpleModeSanitizer` (o
   similar), leído vía `IOptionsMonitor<RagGenerationOptions>` (no `IOptions<T>` estático) para que
   un cambio en `appsettings.json`/variable de entorno del compose tome efecto con un simple
   restart de contenedor, sin rebuild. Cuando está en `false`, revierte el comportamiento completo
   a como estaba antes de esta fase — sanitización **y** buffer desactivados juntos (streaming
   crudo sin filtrar), no solo el filtro con el buffer todavía puesto; así el flag es un "volver a
   antes de Fase 1" real, no parcial. Loguear a nivel `Warning` en el arranque cuando el flag esté
   en `false`, para que no quede desactivado por accidente semanas después de un incidente puntual.

4. **Compensación de UX por la pérdida de streaming** (solo aplica a `/api/ask/stream`, que ya
   tiene canal SSE de status). Encadenar tres estados en vez de dos: "Buscando en la
   documentación..." → "Generando respuesta a partir de N fragmentos..." (ya existen) → un tercer
   estado breve como "Verificando formato..." emitido justo cuando el stream interno de generación
   termina y `SanitizeSimpleAnswer` está corriendo, antes del `yield` final. Da una señal de
   progreso real en vez de un silencio de 10-20s que puede leerse como "se colgó" para el público
   de soporte/QA/negocio — que tiene menos tolerancia a esa ambigüedad que un dev acostumbrado a
   esperar un build. Instrumentar (log) la latencia real de Simple (buffer) vs. Technical
   (streaming) antes y después de este cambio, para tener un número si más adelante hace falta
   evaluar si el trade-off valió la pena.

5. **Verificación contra el corpus completo de 33 preguntas, no solo las 4-6 conocidas-malas.**
   Escribir un script chico (reutilizable en Fase 2 y en iteraciones futuras — ya van tres rondas
   de verificación manual ad-hoc esta semana) que:
   - Re-corre las 33 preguntas históricas contra el contenedor reconstruido (`bsuite-repo`,
     `responseMode: "simple"`).
   - Aplica el mismo regex del filtro de forma offline sobre cada respuesta para marcar
     violaciones (fences, identificadores) — esto detecta tanto **falsos negativos** (código que
     se sigue filtrando) como, revisando a mano las ~29 respuestas que esta semana SÍ pasaban bien,
     **falsos positivos** (prosa legítima que el filtro destroza).
   - Criterio de "hecho" cuantitativo: **0 de 33** respuestas históricas contienen bloques de
     código o identificadores después del filtro — no solo "las preguntas que fallaban ya no
     fallan" (eso deja abierto el whack-a-mole de casos nuevos no cubiertos por la muestra de 4-6).

**Fase 2 — Swap de contexto a Resumen (D), como mejora de calidad, no como único mecanismo, y
condicionada a medir el riesgo de regresión de utilidad.**

Inyectar `SummaryCache` en `RagGenerationService` (nueva dependencia constructor, mismo patrón que
ya usa `Program.cs` para `Sources`). Para `ResponseMode.Simple`, al construir el bloque de contexto
(`BuildContextBlock`), reemplazar `result.Content` por el `Resumen` cacheado cuando exista. Para
chunks sin resumen, **no asumir que excluirlos del contexto es gratis**: si eso hace que
`wiki-solis`/`rag-engine` (0% cobertura) caigan al gate de no-grounding en preguntas donde antes sí
había una respuesta (aunque con código que la Fase 1 tenía que podar), es peor para el usuario que
mantener el contenido crudo y dejar que el filtro de Fase 1 lo limpie.

**Mecanismo de fallback: decidido por chunk, en cada request — no por colección de antemano.** Una
lista fija de "estas colecciones excluyen, estas degradan" es un punto de mantenimiento manual que
se desactualiza solo (¿quién se acuerda de sacar `wiki-solis` de la lista el día que se re-ingesta
con `--con-resumen` y pasa de 0% a 100%?). En vez de eso, calcular por request qué fracción de los
chunks *recuperados en ese turno* tiene resumen:
- Si la fracción con resumen es alta (ej. ≥ 50%), excluir del contexto los pocos que no lo
  tienen — se pierde poco, y el contexto queda limpio.
- Si la fracción con resumen es baja (ej. < 50%), degradar a contenido crudo los que no tienen
  resumen en vez de excluirlos, dejando que el filtro de Fase 1 los limpie — así no se vacía el
  contexto solo por mala cobertura circunstancial.

El comportamiento se adapta solo a medida que una colección gana cobertura de `--con-resumen`, sin
tocar código ni config. El umbral exacto (50% es punto de partida, no un número ya validado) se
calibra con la medición de abajo.

Antes de aceptar esta fase:
- Re-correr las mismas 33 preguntas contra `wiki-solis` y `rag-engine` (no solo `innovapp-docs`,
  que es el caso benigno porque ya es prosa sin `--con-resumen`) y medir cuántas pasan de
  "respuesta filtrada" a "sin evidencia" con el mecanismo por-chunk arriba, no con exclusión total.

No requiere re-ingestar nada para dar valor incremental donde ya hay cobertura — mejora
automáticamente a medida que más colecciones tengan `--con-resumen`, sin ningún ajuste manual.
- Archivos: `RagGenerationService.cs` (constructor + `BuildContextBlock`/`RetrieveChunksAsync`),
  posiblemente `ServiceCollectionExtensions.cs` si `SummaryCache` no está ya resuelta para ese
  proyecto en el contenedor de DI de `RagEngine.Core`.

**Fuera de alcance por ahora (parking lot):** re-ingestar `wiki-solis`/`rag-engine`/otras con
`--con-resumen` para ampliar cobertura de la Fase 2 (costoso, horas por colección); segunda pasada
de reescritura (B) y modelo no-coder (C) — solo se evalúan si, después de Fases 0-2, todavía se ve
código filtrando en pruebas reales.

## Verificación

1. `dotnet build` limpio.
2. Reconstruir y reiniciar el contenedor (`docker compose build rag-api && docker compose up -d
   rag-api` desde `infra/`).
3. Correr el script de verificación (Fase 1, punto 5) contra las 33 preguntas históricas de
   `bsuite-repo` en modo Simple, confirmando el criterio cuantitativo: **0/33 con bloques de código
   o identificadores**, y revisión manual de que ninguna de las ~29 que antes pasaban bien perdió
   calidad de prosa por falsos positivos del filtro.
4. Confirmar específicamente que la pregunta de "plan de auditoría automático" vuelve a decir "no
   hay evidencia" sin inventar una implementación (regresión de Fase 0).
5. Verificar con `docker cp` + búsqueda del literal nuevo en el DLL desplegado (mismo método usado
   esta semana) que el contenedor probado corre el código actualizado — no asumir.
6. Antes de aceptar Fase 2: re-correr el mismo corpus de 33 preguntas contra `wiki-solis` y
   `rag-engine` (no solo `innovapp-docs`) y confirmar que la tasa de "sin evidencia" no sube de
   forma significativa respecto a Fase 1 sin Fase 2.
7. Confirmar que el flag de rollback (`EnableSimpleModeSanitizer=false`) efectivamente revierte a
   streaming crudo sin buffer, y que emite el `Warning` de arranque esperado.
