# Centralización de prompts: ¿"vault" externo o consolidación en código?

> **Estado:** propuesta, no implementada. Es un plan de **arquitectura/deuda técnica**, no de calidad de retrieval ni de guardrails. No cambia el comportamiento del sistema: el criterio de éxito es *cero cambio observable* en las respuestas del LLM.
>
> **Origen:** observación de que los prompts están hardcodeados como `const string` de C# repartidos en el código, y la pregunta de si conviene un "vault" de prompts centralizado y fuera del código.

## El problema — inventario real

Hoy **no existe ninguna externalización** del texto de los prompts: todos son literales `const string` (raw strings `"""..."""`) compilados en el ensamblado. Lo único configurable es indirecto: consts `PromptVersion = "v1"` (para cache-key) y `MetaIntentOptions` (umbral de similitud, no texto).

Hay **5 prompts distintos** enviados a un LLM (Ollama/Qwen2.5 vía Semantic Kernel), ~300-350 líneas de instrucciones en total, más fragmentos de apoyo. Distribución:

| Archivo | Constante | Líneas | ~Tamaño | Construcción |
|---|---|---|---|---|
| `RagGenerationService.cs` | `NoContextFallbackMessage` | 86-87 | 1 línea | const plano |
| `RagGenerationService.cs` | `CodeSystemPromptTemplate` | 94-152 | ~55 líneas / 8 reglas | `string.Format({0} ctx, {1} fallback)` |
| `RagGenerationService.cs` | `DocsSystemPromptTemplate` | 167-223 | ~55 líneas / 8 reglas | idem |
| `RagGenerationService.cs` | `SimpleSystemPromptTemplate` | 234-352 | **~115 líneas / 11 reglas** (el mayor, con ejemplos BAD/GOOD bilingües embebidos) | idem |
| `RagGenerationService.cs` | `LowConfidenceAddendum` | 362-376 | ~14 líneas | concatenado al template (línea 614) |
| `RagGenerationService.cs` | `SelfDescriptionBlock` | 383-396 | ~13 líneas (ES) | const; también `{0}` de NoGrounding |
| `RagGenerationService.cs` | `NoGroundingSystemPromptTemplate` | 413-455 | ~42 líneas / 5 reglas | `string.Format({0})` con SelfDescriptionBlock |
| `OllamaBusinessSummaryGenerator.cs` | `SystemPrompt` | 45-62 | ~17 líneas (ES) | const + user prompt interpolado |
| `poc/…/SummaryGenerator.cs` | `SystemPrompt` | 26-43 | ~17 líneas | **duplicado verbatim** del anterior |

Tres hechos que fijan el diagnóstico:

1. **God-file.** `RagGenerationService.cs` tiene **1042 líneas** que mezclan 4 responsabilidades: los 7 constantes de prompt, la lógica de selección (`SelectSystemPromptTemplate:889`), el armado de contexto (`BuildContextBlock:977`, `FormatChunkAsMarkdown:~1020`) y regex de sanitización. El texto del prompt está *enterrado* entre lógica.

2. **Duplicación real.** Los 3 templates grounded (Code/Docs/Simple) comparten bloques copy-pasteados casi idénticos (el ejemplo del "40% de descuento", el párrafo de "las respuestas previas cuentan como hecho"). Y el prompt de resumen de negocio está **duplicado íntegro** en el PoC (`poc/RagEngine.Poc.FreeSearch/SummaryGenerator.cs`). Un cambio de política obliga a editar N lugares y arriesga divergencia silenciosa.

3. **El versionado ya estaba en la mente del equipo.** Existen consts `PromptVersion = "v1"` usados como cache-key. Cualquier consolidación debe respetar/formalizar esto, porque cambiar el texto de un prompt de resumen **invalida el caché de resúmenes** (ver `[[rag-engine-resumen-negocio-implementado]]`).

**Contenido en lenguaje natural, prompt-adyacente** (no se envía como prompt pero es texto editable que conviene tener cerca): `SemanticMetaIntentDetector.Exemplars` (`SemanticMetaIntentDetector.cs:33-58`, ~24 preguntas-ejemplo en español para detección de meta-intención por coseno) y las cabeceras en español de `ContextAssembler.cs:40-70`.

## El matiz que evita la conclusión fácil

"Hardcodeado" **no es el olor**. El olor es *duplicado + enterrado en un god-file*. Eso se arregla sin sacar el texto a archivos externos.

Y sacarlo a archivos tiene un costo que se subestima: los prompts usan `string.Format` **posicional** (`{0}` = bloque de contexto, `{1}` = frase de fallback). Ese `{0}/{1}` es un **contrato tipado hoy**. Al moverlo a `.txt`/`.md`/`.json`:

- El placeholder deja de validarse en compilación → un `{2}` mal puesto o un `{0}` perdido revienta en **runtime**, no en build.
- Se pierde el type-safety de referenciar la constante desde el código.
- El versionado deja de viajar atado al build/commit (a menos que se re-implemente).

Por eso el eje de decisión **no es** "código vs archivo". Es **quién edita los prompts y con qué frecuencia cambian**.

## Opciones consideradas

| # | Opción | Resuelve duplicación | Resuelve god-file | Conserva contrato `{0}/{1}` tipado | Permite editar sin recompilar | Costo de implementación | Veredicto |
|---|---|---|---|---|---|---|---|
| A | **Status quo** — consts dispersos | no | no | sí | no | nulo | Descartado — es el problema |
| B | **Consolidación en C#** — carpeta `Prompts/`, una clase estática por prompt, fragmentos compartidos componibles | **sí** | **sí** | **sí** | no | bajo (mover + de-dup, sin lógica nueva) | **Elegido — Fase 1** |
| C | Archivos externos `.md`/`.txt` sueltos + `File.ReadAllText` | sí | sí | **no** (placeholders stringly-typed) | sí | medio | Descartado por ahora — pierde contrato sin beneficio activo |
| D | **Embedded resources** (`.prompt` como `EmbeddedResource` + `GetManifestResourceStream`) | sí | sí | no | no (siguen en el assembly) | medio | Descartado — costo de C sin su beneficio (tampoco edita sin build) |
| E | **`IPromptProvider` con impl. embebida** (interfaz + provider que hoy devuelve las clases de B) | sí | sí | sí | no (todavía) | bajo-medio | **Elegido — Fase 2**, deja la puerta abierta a F sin tocar call-sites |
| F | Vault externo real (archivos/DB) **detrás de `IPromptProvider`**, con hot-reload/versionado | sí | sí | mitigable (validación de placeholders al cargar) | **sí** | alto | **Parking lot** — solo si aparece un disparador (ver abajo) |
| G | Servicio de prompts SaaS / prompt registry externo | sí | sí | — | sí | muy alto | Descartado — el sistema es 100% local, sin LLM externo; contradice el diseño |

## Enfoque recomendado — dos fases, la segunda condicional

### Fase 1 — Consolidación en C# (hacer ahora)

Resuelve ~90% del dolor sin perder tipado ni contrato.

1. Crear `src/RagEngine.Core/Prompts/` (o `.../Services/Generation/Prompts/`).
2. Una clase estática por prompt, sacando los 7 constantes de `RagGenerationService.cs` y el `SystemPrompt` de `OllamaBusinessSummaryGenerator.cs`.
3. **De-duplicar** los bloques compartidos de Code/Docs/Simple en fragmentos `const` componibles (ej. `SharedRules.PriorRepliesAreFact`, `SharedRules.DiscountExample`), y ensamblar cada template por concatenación/`string.Format` desde esos fragmentos.
4. **Matar la copia del PoC**: `poc/…/SummaryGenerator.cs` referencia el mismo fragmento (o se documenta explícitamente por qué el PoC debe divergir; hoy no diverge).
5. `RagGenerationService` se queda **solo con la lógica** (selección, armado de contexto, sanitización); el texto vive en `Prompts/`.
6. Formalizar `PromptVersion` junto al texto que versiona, para que un cambio de prompt y su bump de versión sean **el mismo diff** (evita invalidación de caché olvidada o falsa).

### Fase 2 — Introducir `IPromptProvider` (hacer si/cuando)

Meter una interfaz `IPromptProvider` delante de las clases de la Fase 1. Impl. inicial = las clases estáticas (cero cambio de comportamiento). Esto **desacopla los call-sites** del origen del texto, de modo que migrar a un vault externo (opción F) sea un cambio de una sola implementación, no una cirugía en `RagGenerationService`.

### Cuándo saltar a un vault externo (opción F) — disparadores explícitos

Solo si aparece **al menos uno**:

- **Gente no-dev edita prompts** (producto, prompt engineering) y no debe recompilar/redeployar.
- Se necesita **hot-swap / A-B de prompts en runtime** sin redeploy.
- El nº de prompts crece a **decenas**.
- Se formaliza **i18n** (hoy se mezcla instrucción en inglés con ejemplos en español — es el único disparador que ya *roza*, pero con 5 prompts no lo justifica solo).

Mientras ninguno aplique, F es sobre-ingeniería: paga complejidad (carga, validación de placeholders, versionado, cache-invalidation) por una edición que hoy solo hace un dev con acceso al repo.

## Riesgos y mitigaciones

- **Cambiar texto por accidente al mover.** El refactor debe ser **byte-idéntico**. Mitigación: ver criterio de éxito.
- **Invalidar caché de resúmenes sin querer.** Si un raw-string cambia (aunque sea un espacio), cambia `PromptVersion`/cache-key efectivo y fuerza re-ingesta cara. Mitigación: la Fase 1 no debe tocar el *contenido* del prompt de resumen; solo su *ubicación*. Verificar con diff de texto.
- **Escepticismo n=1.** No basta con "compila y una pregunta responde bien". Ver criterio.

## Criterio de éxito (cuantitativo) y rollback

**Éxito de Fase 1 = cero cambio de comportamiento**, comprobado, no asumido:

1. **Igualdad de texto ensamblado.** Test que construye cada system prompt final (Code/Docs/Simple/NoGrounding/Summary con sus `{0}/{1}` rellenos con valores fijos) **antes y después** del refactor y assert `Assert.Equal` byte a byte. Si un solo carácter difiere, el refactor está mal.
2. **Regresión de comportamiento.** Correr el eval-set existente (`rag eval` sobre `innovapp-docs` / `bsuite-*`) y confirmar recall@10 **idéntico** al baseline (el refactor no toca retrieval, así que debe ser bit-a-bit igual salvo no-determinismo conocido de Qdrant, ver `[[rag-engine-retrieval-no-determinismo-qdrant]]`).
3. **Prompt de resumen intacto.** Diff textual del `SystemPrompt` de negocio = vacío → el caché de resúmenes no se invalida.
4. **Escenario de riesgo real, no el benigno.** Probar explícitamente el prompt **más acoplado** (`SimpleSystemPromptTemplate`, ~115 líneas con ejemplos BAD/GOOD y `{0}/{1}`), no solo el `NoContextFallbackMessage` de 1 línea.

**Rollback:** la Fase 1 es un refactor puro en un commit aislado; revertir = revertir el commit. Sin flags de runtime porque no cambia comportamiento. La Fase 2 (`IPromptProvider`) sí entra detrás de la interfaz con impl. por defecto = la actual, revertible sin tocar call-sites.

## Relación con otros documentos

- `[[rag-engine-modelo-generacion-vault-docs]]` — el system prompt de `RagGenerationService` está hardcoded para código, no para prosa; la consolidación no lo *resuelve* pero lo deja visible y editable en un solo lugar.
- `guardrail-banda-baja-conversacional.md` / `guardrail-alcance-tarea-vs-corpus.md` — cualquier cláusula de alcance/guardrail que se agregue a los system prompts (opción G de ese plan) se vuelve más barata y menos propensa a divergir si primero existe la consolidación de Fase 1.
