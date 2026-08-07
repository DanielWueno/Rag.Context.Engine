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

Esto establece que el problema no es (solo) de wording del prompt: `qwen2.5-coder` — el único
modelo de generación configurado, para ambos modos — está fuertemente sesgado a responder en
términos de código cuando el contexto recuperado es código, y ese sesgo resiste instrucciones
explícitas incluso con ejemplos concretos.

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

  Esto significa que usar el resumen como fuente de verdad para el modo Simple **no cubre hoy
  la mayoría de colecciones de código** — requeriría re-ingestar con `--con-resumen`, que por
  precedente de esta semana toma horas (bsuite-repo tardó ~18h55m).
- El campo `Resumen` ya se expone directo al usuario en `sources` cuando `ResponseMode=Simple`
  (cambio de esta sesión, `SourceDto.Redacted`). No pasa por ningún filtro — si en el futuro se
  usa también como contexto del LLM, conviene limpiar el prefijo `Entidad.cs:` en el mismo lugar.

## Opciones consideradas

| # | Opción | Garantiza cero código | Cobertura hoy | Costo | Riesgo |
|---|---|---|---|---|---|
| A | Filtro determinístico post-generación (regex que detecta y quita/reemplaza fences ``` ``` ``` e identificadores tipo código antes de mostrar la respuesta) | Sí, siempre | 100% de colecciones | Bajo-medio | Pierde streaming token-a-token en modo Simple (hay que bufferear para poder filtrar); puede dejar prosa cortada si el bloque de código era el contenido central de la respuesta |
| B | Segunda pasada de reescritura (generar respuesta técnica completa, luego una llamada aparte que la reescribe en lenguaje simple, sin chunks de código en el contexto de esa segunda llamada) | Probablemente alto (el input ya es prosa, sin código que tiente al modelo) | 100% | Medio | 2x latencia (ya hay respuestas de 10-20s); pierde streaming igual que A; la segunda pasada podría fabricar de nuevo si no se acota bien |
| C | Modelo no-coder (`qwen2.5:7b-instruct`) solo para modo Simple | Incierto | 100% (no depende de resumen) | Medio (plumbing de 2 modelos) | Ya hay precedente en memoria ("ningún modelo gana limpio" comparando estos dos en vault de docs) — probablemente pierde precisión leyendo C# sin ser code-aware |
| D | Cambiar el contexto: para modo Simple, alimentar al LLM con el `Resumen` cacheado en vez del contenido crudo del chunk (garantía estructural — si el LLM nunca ve código, no puede filtrarlo) | Alto para chunks con resumen | Parcial (2/7 colecciones probadas) | Medio (nueva dependencia `SummaryCache` en `RagGenerationService`, lookup por chunk, fallback para chunks sin resumen) | Ninguno nuevo, pero no resuelve el problema para colecciones sin `--con-resumen` — necesita una estrategia de respaldo igual |
| E | Corregir la regresión de la regla anti-evasión (revertir/acotar la parte de la regla 5 que causó la fabricación del método inventado) | N/A — es un fix de calidad, no de fuga de código | 100% | Muy bajo | Ninguno; es una corrección directa de un error introducido esta sesión |

## Enfoque recomendado (por fases)

**Fase 0 — Corregir la regresión (E), antes que nada.** Reescribir la regla 5 de
`SimpleSystemPromptTemplate` para que "no evadas cuando el contexto ya tiene la regla" no se lea
como "nunca hedgees" — debe seguir permitiendo el hedge legítimo (rule 2 / `LowConfidenceAddendum`)
cuando el contexto realmente no cubre el detalle preguntado, sin abrir la puerta a inventar una
implementación de ejemplo. Re-probar contra las preguntas que fabricaron contenido (la de "plan de
auditoría automático") para confirmar que vuelve a decir "no hay evidencia" sin inventar código.

**Fase 1 — Filtro determinístico (A) como red de seguridad universal.** Implementar un post-proceso
en el límite de salida para `ResponseMode.Simple` que:
- Detecta y elimina bloques ` ``` ` completos (con un aviso genérico tipo "*(se omitió un fragmento
  técnico)*" solo si el bloque tenía contenido no vacío, para no dejar huecos raros en la prosa).
- Detecta y sanitiza identificadores obvios en backticks o en `PascalCase.Method` / `snake_case`
  reconocibles como código (heurística conservadora — mejor dejar pasar algún falso negativo que
  destrozar prosa legítima).
- Como el filtro necesita el texto completo para poder actuar sobre fences que abren/cierran en
  fragmentos distintos del stream, `ResponseMode.Simple` deja de emitir token-a-token: se buffer-ea
  la respuesta completa en `RagGenerationService.AskStreamingAsync` (o en el llamador) y se filtra
  antes de `yield return`. `ResponseMode.Technical` no se toca — sigue streameando igual que hoy.
- Archivo principal: `src/RagEngine.Core/Services/Generation/RagGenerationService.cs` (nuevo
  método privado `SanitizeSimpleAnswer`, invocado en el punto donde hoy se hace
  `await foreach (var fragment in StreamAnswerAsync(...))` para el camino grounded).
- Re-probar las mismas preguntas que fallaron esta semana (Q14, Q19, Q21, Q27/28 de los logs)
  contra el contenedor reconstruido, comparando antes/después igual que se hizo hoy.

**Fase 2 — Swap de contexto a Resumen (D), como mejora de calidad, no como único mecanismo.**
Inyectar `SummaryCache` en `RagGenerationService` (nueva dependencia constructor, mismo patrón que
ya usa `Program.cs` para `Sources`). Para `ResponseMode.Simple`, al construir el bloque de contexto
(`BuildContextBlock`), reemplazar `result.Content` por el `Resumen` cacheado cuando exista (limpiando
el prefijo `Entidad.cs:` con un regex simple); los chunks sin resumen se excluyen del contexto de
ese turno en vez de pasar el contenido crudo (si eso deja el contexto vacío, cae naturalmente en el
gate de no-grounding ya existente). Esto reduce cuánto tiene que intervenir el filtro de la Fase 1
y mejora la prosa (menos "Frankenstein" de texto cortado a la mitad). No requiere re-ingestar nada
para dar valor incremental — mejora automáticamente a medida que más colecciones tengan
`--con-resumen`.
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
3. Re-correr contra el contenedor real las preguntas que fallaron esta semana (las mismas queries
   citadas arriba, colección `bsuite-repo`, `responseMode: "simple"`), confirmando:
   - Cero bloques ` ``` ` y cero identificadores de código en las respuestas.
   - La pregunta de "plan de auditoría automático" vuelve a decir "no hay evidencia" sin inventar
     una implementación.
4. Verificar con `docker cp` + búsqueda del literal nuevo en el DLL desplegado (mismo método usado
   hoy) que el contenedor probado corre el código actualizado — no asumir.
5. Correr también 1-2 preguntas en `innovapp-docs` (colección de documentación, sin `--con-resumen`)
   para confirmar que el modo Simple sigue funcionando bien ahí (ya funcionaba antes de este plan).
