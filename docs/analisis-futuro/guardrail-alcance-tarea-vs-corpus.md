# Guardrail de alcance: rechazar ayuda con tareas ajenas al sistema

> **Estado:** propuesta, no implementada. Es la **tercera** pieza de la cadena de guardrails del chat, después de:
> - `guardrail-dominio-chat.md` (gate de confianza en 3 bandas sobre el score — *implementado*), y
> - `guardrail-banda-baja-conversacional.md` (`NoGroundingSystemPromptTemplate` — *implementado*).
>
> **Desambiguación:** este documento NO trata sobre *si hay grounding* (eso lo resuelven las 3 bandas) ni sobre *si la pregunta es una meta-pregunta* ("¿quién eres?", que resuelve el `SemanticMetaIntentDetector`). Trata sobre una dimensión distinta y nueva: **si la petición es una tarea que el usuario debería llevar a un asistente de propósito general** (Excel, refactorizar/revisar su propio código, how-to de herramientas externas), en cuyo caso el sistema debe **declinar y redirigir**, aunque el modelo sepa la respuesta y aunque no fabrique ningún hecho del corpus.

## El problema

El 2026-08-08, en dos sesiones consecutivas, se comprobó que el chat actúa como asistente de trabajo de propósito general en cuanto se le pide. Evidencia directa del log `logs/rag-api-20260808.json` (reconstruida por `TraceId`):

| Hora | Pregunta | Banda | topScore | Qué hizo |
|---|---|---|---|---|
| 19:45:18 | *(pega su código MAUI)* "…lo puedes refactorizar" | **ALTA** | **0.846** | Aceptó refactorizar y explicó los métodos extraídos |
| 19:48:13 / 19:48:51 | "…una opción de excel, cómo calculo un campo…" | MEDIA | 0.121 | Entregó un tutorial de Excel completo |

Dos observaciones que fijan el diseño:

1. **El score no separa este caso.** El Excel entró por banda media (0.121 > 0.05, pasó el gate). El refactor entró por banda **alta** (0.846) porque el código pegado se parece semánticamente al código indexado de InnovApp — el gate de confianza lo leyó como "consulta central del corpus". Un gate por score jamás va a distinguir "pregunta sobre el sistema" de "hazme una tarea": ya está documentado en `guardrail-dominio-chat.md` ("*el score nunca va a resolver esto — hace falta una señal de intención aparte*", caso 0.439).

2. **El sanitizer de código NO es el control.** En modo Simple, `SanitizeSimpleAnswer` (`RagGenerationService.cs:837-858`) vació el payload (el refactor salió como `*(se omitió un fragmento técnico)*`, las fórmulas como `[detalle técnico]`), dejando la respuesta en "plática" inútil — que es el efecto deseado. Pero eso es **suerte, no control**:
   - Solo existe en modo Simple. En modo Technical no hay sanitizer → el refactor de 0.846 se habría entregado completo. **La fuga es independiente del modo; el sanitizer solo la tapa en uno.**
   - Es post-generación: gasta la inferencia entera y luego la borra, y produce respuestas confusas ("aquí tienes tu refactor: *(nada)*").

   El sanitizer se queda como red de defensa en profundidad, pero el control real debe **declinar antes de recuperar/generar**.

## Cambio de política — reconciliación con el invariante vigente

`guardrail-banda-baja-conversacional.md` fija el invariante rector: *"el guardrail existe para evitar que se afirme un hecho de negocio no fundamentado — no para prohibir toda respuesta generada por el LLM cuando no hay contexto de dominio."* Bajo ese invariante, el tutorial de Excel es **aceptable** (no fabrica ningún hecho del corpus). Por eso pasó.

Este plan **extiende el invariante con un segundo eje**, de forma deliberada y explícita:

> El asistente responde **solo sobre el sistema indexado**. Además de *no fabricar hechos de negocio* (eje existente), **no ejecuta ni asiste tareas ajenas al sistema aunque pueda resolverlas y aunque no fabrique nada** (eje nuevo).

**Qué sigue permitido (no se toca):**
- Conversación con gracia: saludos, agradecimientos, despedidas ("Hola, buen día", "hola").
- Meta-preguntas: "¿quién eres?", "¿con qué me puedes ayudar?", "¿sobre qué sistema puedo preguntar?" → las resuelve el `SemanticMetaIntentDetector`.
- Declinar off-topic inocuo ("¿puedo ver Netflix?", "el mouse no sirve") → ya lo hace con gracia el camino no-grounding. **No es el objetivo de este gate.**
- Preguntas legítimas del sistema, **incluidas las que preguntan *sobre* código indexado** ("¿qué hace `ServicioCliente`?", "¿dónde se define `EsPermiteCancelarTraslado`?").

**Qué se rechaza (objetivo del gate) — categoría estrecha:** peticiones de *producir o transformar trabajo del usuario* con conocimiento de propósito general — refactorizar/revisar/escribir su propio código, fórmulas y how-to de herramientas externas (Excel, Word, correo), consejos/algoritmos generales. Es decir, cuando el usuario usa el RAG como sustituto de ChatGPT para su trabajo.

**Por qué esto NO es "enumerar categorías" (evita el descarte previo).** `guardrail-banda-baja-conversacional.md` descartó *"una lista de categorías de intención que crece sin límite"* — pero eso era para **whitelistear cada tipo de mensaje inocuo** con respuesta fija. Aquí es lo inverso: **una sola categoría en blacklist** ("petición de tarea de trabajo"), detectada por **un clasificador semántico que generaliza** (embeddings), no por enumeración de frases. Es el mismo mecanismo ya aceptado para meta-intent, con signo contrario.

## Opciones consideradas

Todas las capas donde puede vivir un control, con veredicto. `caso Excel` = banda media 0.121; `caso refactor` = banda alta 0.846 con código pegado.

| # | Mecanismo | Etapa | Precisión | Costo | Atrapa Excel | Atrapa refactor | Veredicto |
|---|---|---|---|---|---|---|---|
| A | Denylist regex de herramientas ("excel", "netflix", "refactoriza") | input | baja (frágil, se evade) | nulo | parcial | no | **Descartado** — patrón ya rechazado en `guardrail-dominio-chat.md` (regex → semántico) |
| B | Gate por score de retrieval | retrieval | nula para intención | nulo | no (0.12 pasa) | no (0.85 pasa) | **Descartado** como control de alcance (ya lo dice el doc de 3 bandas) |
| C | **Clasificador semántico bi-encoder de alcance** (exemplars in/out) | input | media-alta | ~1 embed ONNX/CPU (barato) | sí | **parcial** (embed dominado por código) | **Elegido — capa rápida** |
| D | **Detector estructural de código pegado** (bloque cercado / densidad de código + verbo imperativo) | input | alta para su caso | nulo (regex de forma) | no | **sí** | **Elegido — capa 0** (único que cubre el bypass 0.846) |
| E | **Juez fino LLM-as-classifier** (Qwen local, solo banda dudosa) | input | alta | ~1 generación corta, gateada | sí | sí | **Elegido — capa fina** |
| F | Reusar el cross-encoder reranker como juez de intención | input | baja | medio | débil | débil | **Descartado** — es modelo de *relevancia* query-doc (mmarco), no de intención; no aplica |
| G | Cláusula de alcance en los system prompts (auto-policía del LLM) | generación | media (LLM poco fiable para auto-abstenerse) | nulo | parcial | parcial | **Elegido — defensa en profundidad** (cubre todos los modos, incl. banda alta) |
| H | Sanitizer de salida (actual) | post-gen | — | alto (buffer, 2x) | tapa payload | tapa payload | **Se conserva** como red, no como control |
| I | Segunda pasada LLM de reescritura de la respuesta | post-gen | media | ~2x latencia | — | — | **Descartado** — ya en parking lot en `modo-respuesta-simple-codigo.md` y `modo-simple-explicacion-semantica.md` |
| J | Alcance por cercanía al manifold del corpus (centroide de la colección) | retrieval | baja para tareas | bajo | quizá | **no** (el refactor está *cerca* del corpus) | **Descartado** — cercanía-al-corpus ≠ alcance; el refactor lo demuestra |
| K | Clasificador multi-clase de intención (meta/sys/task/social) | input | media | bajo | sí | parcial | **Parking lot** — reencuadra meta-intent + alcance en un solo modelo; más limpio pero mayor riesgo de regresión sobre lo ya calibrado. Reevaluar tras C+D+E |
| L | Detección de deriva conversacional multi-turno | input | — | bajo | — | — | **Parking lot** — la sesión derivó en 32 turnos; v1 clasifica solo la query actual (suficiente: cada query mala es mala por sí sola) |

## Enfoque recomendado — cascada en capas (mapea tu "grueso → fino → resolución")

Defensa en profundidad, escalonada por costo. Cada capa puede resolver sola; la siguiente solo corre si la anterior queda en duda.

**Capa 0 — Estructural, determinística (opción D).** Antes de todo: si la query trae un **bloque de código pegado** (fence ```` ``` ````, o ≥ N líneas con forma de código) **+ un imperativo de acción** ("refactoriza", "revisa", "mejora", "corrige") → ruta de rechazo directa. Reusa los patrones de forma que ya tiene `SanitizeSimpleAnswer` (`FencedCodeBlockPattern` etc., `RagGenerationService.cs:759-813`), aplicados a la *query* en vez de a la *respuesta*. Es lo único que cubre el bypass de banda alta (0.846). Distingue "pega SU código para que lo transforme" (rechazar) de "menciona un identificador del sistema" ("¿qué hace `ServicioCliente`?", permitir) por **tamaño del bloque**, no por presencia de código.

**Capa 1 — Filtro grueso rápido, bi-encoder (opción C).** Espeja `SemanticMetaIntentDetector`:
- Nuevo `IScopeIntentDetector` (mismo shape que `IMetaIntentDetector`, `Abstractions/IMetaIntentDetector.cs:13`) e impl `SemanticScopeIntentDetector` con `string[] Exemplars` hardcoded de **peticiones de tarea fuera de alcance** + `Lazy<Task<float[][]>>` vía `GenerateBatchEmbeddingsAsync`.
- **Decisión de dos lados** (no umbral simple): exemplars de *out-of-scope* Y anclas de *in-scope* (preguntas del sistema); clasifica por centroide/similitud más cercana. Esto es lo que separa "¿cómo hago un ticket?" (in) de "¿cómo hago X en Excel?" (out), que un umbral de un solo lado no separa.
- Salida en tres zonas: **claramente out** → rechazo; **claramente in** → sigue el pipeline normal; **banda dudosa** → escala a Capa 2.
- Costo: 1 pasada de encoder ONNX/CPU (barato). Nota: la query ya se embebe 2× hoy sin cachear (`SemanticMetaIntentDetector.cs:82` y `QdrantSemanticRetriever.cs:68`); una 3ª es consistente, u opcionalmente se cachea el vector.

**Capa 2 — Juez fino, solo banda dudosa (opción E).** Aquí va tu "análisis humano". El cross-encoder que tenemos **no aplica** (es reranker de relevancia). El juez natural es el **LLM local como clasificador**: una llamada corta, constreñida, temp 0 → "¿Es esta petición una tarea ajena al sistema `{colección}`? Responde solo `in-scope` / `off-scope`". Gateado a la minoría dudosa, el costo agregado es marginal. No es la "segunda pasada" descartada (I): eso reescribía *la respuesta*; esto clasifica *antes de generar*.

**Capa 3 — Resolución / ruteo.** Insertar como **"Step 0.5"** en `RagGenerationService.AskStreamingAsync` (entre `RagGenerationService.cs:528` y `:530`, justo tras el Step 0 de meta-intent). Idiom idéntico al meta-intent: al rechazar, `yield return <bloque fijo de rechazo>; yield break;` (sin retrieval ni LLM → cero fabricación, barato). El bloque de rechazo redirige: *"Solo puedo ayudarte con preguntas sobre `{colección activa}`. No puedo asistirte con tareas como Excel o revisar/refactorizar tu código."*
- **Orden vs meta-intent:** meta-intent primero (conversar/describirse gana), luego alcance. Validar que "¿con qué me puedes ayudar?" (meta, permitir) no colisione con exemplars de tarea; la Capa 2 resuelve empates.
- **Dos puntos de entrada:** el gate meta-intent se invoca también directo en `Program.cs:200` y `:296` (endpoints API). El gate de alcance debe **centralizarse** (idealmente dentro del servicio, fuente única) o duplicarse en ambos endpoints — decisión de implementación, recomiendo centralizar para que API stream, API no-stream y CLI compartan la misma decisión y thresholds (`Program.cs:83` ya pide que compartan detector).

**Capa 4 — Cláusula de alcance en prompts (opción G, defensa en profundidad).** Añadir a los 5 templates (`Code/Docs/Simple/NoGrounding/hedge`) una regla de tipo-de-tarea: *"Respondes solo SOBRE `{sistema}`. Si te piden ejecutar una tarea no relacionada (escribir/refactorizar código, Excel, consejos generales), decl­ína y redirige — no la intentes aunque sepas la respuesta."* Es blanda (la regla 2 actual ya mostró que el LLM se auto-abstiene mal), pero es gratis, cubre **todos los modos** y atrapa lo que se cuele por banda alta hacia la generación. Es *complemento*, no sustituto del gate duro.

**Capa 5 — Sanitizer (H).** Sin cambios; queda como última red en modo Simple.

## Fuera de alcance por ahora (parking lot)

- **Clasificador multi-clase unificado (K):** reencuadrar meta-intent + alcance en un modelo. Más limpio, pero toca lo ya calibrado (umbral 0.65 de meta-intent) → riesgo de regresión. Reevaluar solo si mantener dos detectores separados genera fricción.
- **Deriva conversacional (L):** detectar el repropósito progresivo en multi-turno. v1 clasifica la query actual.
- **Cross-encoder de intención dedicado (fine-tuned):** requiere datos de entrenamiento; el LLM-as-classifier lo cubre sin entrenar.
- **Segunda pasada de reescritura (I):** ya descartada dos veces por latencia.

## Verificación — criterios de "hecho"

**Gap de infraestructura (hallado):** no existe proyecto de tests en el repo, ni dataset etiquetado de intención, ni siquiera para el meta-intent existente (sus exemplars/negativos fueron un script ad-hoc no versionado el 2026-07-22). `rag eval` (`EvalCommand.cs`) mide **recall@K de retrieval**, no clasificación. Este plan debe crear ambos.

1. **Dataset etiquetado versionado** (nuevo, p.ej. `docs/eval/scope-intent.labeled.json`). Semilla = las **75 queries distintas del 2026-08-08** (ya extraídas), etiquetadas `SYS` / `META` / `SOCIAL` / `TASK`. Reparto real observado:
   - `TASK` (rechazar): **solo 2** — el refactor (#74) y el Excel (#75). Base rate bajísimo → confirma **precisión-primero**.
   - `SYS` (permitir): ~60, **varias con forma "¿cómo hago X?"** ("¿cómo hago un ticket?", #54) que son los **negativos adversariales** críticos: no deben rechazarse.
   - `META`/`SOCIAL`: el resto (permitir / declinar con gracia).
   - Aumentar con `TASK` sintéticos (más herramientas, más verbos de acción, código en varios lenguajes) porque el caso real es raro pero un usuario adversarial insistirá — y con negativos adversariales de código in-scope ("¿qué hace este método del sistema?").
   - Bordes a etiquetar con el usuario: "Soy nuevo, no sé cuál es mi trabajo" (#68), "¿qué aplicación móvil tienen?" (#72).

2. **Harness de clasificación** (no existe): script de calibración versionado (estilo `verify-simple-mode.py`) o modo nuevo en `EvalCommand`. Métricas: **precision / recall / F1 de la decisión "rechazar"**, y por separado la métrica que manda:
   - **Falsos rechazos sobre `SYS` + `META` + `SOCIAL` = 0** (romper una pregunta legítima es el error caro).
   - Recall sobre `TASK` alto, pero subordinado a lo anterior.
3. **Calibración de umbral** con el método ya documentado en `MetaIntentOptions.cs:7-17`: graficar distribuciones de similitud de in-scope vs out-of-scope, poner el umbral en el **hueco limpio** con margen — no en un punto ajustado a 2 ejemplos (**escepticismo n=1**).
4. **Escenarios de riesgo probados explícitamente** (no solo el benigno): el refactor con código pegado (0.846, debe rechazar vía Capa 0) y el Excel (0.121, vía Capa 1/2); y en paralelo confirmar que las ~60 `SYS` **no** se rechazan.
5. **Flag de rollback** `RagGenerationOptions.EnableScopeGate` vía `IOptionsMonitor` (patrón de `EnableSimpleModeSanitizer`), con log `Warning` al arranque cuando esté en `false`.

## Notas de riesgo

- **Sobre-bloqueo (riesgo #1):** con base rate de 2/75, un gate agresivo hace más daño que bien. La Capa 1 debe sesgarse a "permitir salvo evidencia clara"; la Capa 2 existe precisamente para no rechazar en la duda.
- **Colecciones de código:** en `bsuite-repo` las preguntas *sobre* el código indexado son in-scope legítimas. El discriminante es **pegar código propio para transformarlo** (Capa 0, por tamaño de bloque) vs **preguntar por un identificador del sistema**. No confundir.
- **Latencia de la Capa 2:** solo corre en la banda dudosa; medir qué fracción de queries cae ahí para acotar el impacto (comparar con la latencia Simple ya medida: mediana 9.2s / p90 16.6s).
- **Multilingüe:** exemplars en español (como meta-intent), pero el embedder es multilingüe; validar que una petición de tarea en inglés también se atrape.
- **Idempotencia entre entradas:** si no se centraliza el gate, API-stream / API-no-stream / CLI pueden divergir. Preferir fuente única.
