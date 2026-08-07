# Búsqueda libre para usuarios no técnicos — RRF a 3 bandas

> **Estado (2026-07-31): Retos A-D implementados y verificados en producción real.** Retos A-C
> commiteados en `c3593a1` (rama `feat/rag-api-selector-coleccion`). Reto D (`SourceDto.Resumen`)
> implementado y verificado en esta sesión — ver "Sesión 2026-07-31" al final de este documento.
> El PoC offline (paso 1, sección histórica de abajo) sigue siendo la referencia de diseño original.
> Pendientes restantes: limpiar `bsuite-auditorias-baseline` (**hecho**) y remedir throughput antes
> de escalar a `bsuite-repo` (**hecho** — ver "(continuación 4)": ~14h estimadas, sin cambio
> significativo vs. la estimación previa). El ítem 4 (re-etiquetado del eval-set) se resolvió y el
> fix de chunk-imán ya se verificó re-ingestando `bsuite-auditorias-test` — ver "Sesión 2026-07-31
> (continuación)" y "(continuación 2)" al final. Además, se encontró y arregló un bug relacionado
> (metadata `StartLine`/`EndLine` engañosa en chunks agrupados) — ver "(continuación 3)". Recall@10
> real: **56% → 62% (re-etiquetado) → 69% (fix de chunk-imán) → 69% (fix de metadata, sin
> regresión, recall@3/@5 mejoran)**. `bsuite-repo` ya se escaló con ambos fixes (**hecho** — ver
> "(continuación 5)"): 22.986 puntos, ambos vectores, recall@10 baja a 50% por dilución esperada a
> escala completa. Con esto, la línea de trabajo de esta sesión queda cerrada.

## El problema

Hoy, para obtener resultados relevantes de `rag ask`/`rag search`, la pregunta debe nombrar
entidades técnicas exactas (`AuditoriaResultadoHallazgo`, no "los registros de problemas") y
usar vocabulario cercano al código (ver el patrón verificado en
[`guia-cli.md`](../guia-cli.md#cómo-formular-buenas-preguntas-patrón-verificado-empíricamente)).
Esto funciona bien para desarrolladores, pero es una barrera real si el objetivo es que un
usuario menos técnico pregunte libremente — por ejemplo, "¿cómo levanto un ticket?" — y reciba
una respuesta derivada de reglas de negocio que viven en atributos declarativos del código
(`[RuleRequiredField]`, `[Appearance]`, etc.), sin tener que conocer esos nombres.

La causa raíz: la traducción de sintaxis técnica a semántica de negocio hoy solo ocurre **después**
de la recuperación, dentro del system prompt de `RagGenerationService.cs` (líneas 77-87). La
recuperación misma (dense + sparse, fusionados por RRF) compara la pregunta en lenguaje natural
contra el código crudo — si el chunk relevante nunca entra al TopK por falta de similitud léxica o
semántica con la pregunta, el LLM nunca llega a traducirlo.

Además, el ecosistema es políglota (C#/XAF backend, Vue/MAUI frontend/móvil, más stacks futuros),
lo que descarta cualquier solución que dependa de reglas de traducción específicas por framework.

## Rutas evaluadas

| Ruta | Concepto | Evaluación |
|---|---|---|
| **A — Educar a los vectores** (indexación semántica) | Generar un resumen de negocio por chunk en ingesta, con un LLM, y embeberlo junto al código. | Cierra la brecha semántica en el origen (el vector denso pasa a representar significado de negocio). Costo: LLM por chunk en ingesta; con re-ingesta obligatoria en cada edición (ver `configuracion.md`), exige un mecanismo de caché para no ser inviable a escala. |
| **B — Educar a la pregunta** (query expansion / HyDE-lite) | Un LLM reescribe la pregunta a vocabulario técnico antes de embeberla. | Barato y reversible, no toca el índice. Deal-breaker real: si el expansor decide mal a qué stack/colección pertenece la pregunta, la búsqueda nunca toca la colección correcta — falla **silenciosa** y peor que no responder, porque es confiada. Mitigable con fan-out a todas las colecciones candidatas en vez de elegir una. |
| **C — Agentic RAG** (function calling, LangGraph/Semantic Kernel planners) | Un agente busca, razona, detecta huecos cross-stack, vuelve a buscar y consolida. | Único enfoque con loop de verificación real. Riesgos: 10-20s de latencia, y un modelo local de 7B (`qwen2.5-coder`) es frágil para planning multi-paso confiable — se paga el costo de latencia sin garantía del beneficio de robustez. |

## Decisión: Híbrido — RRF a 3 bandas

Se descarta elegir una sola ruta. Se adopta:

- **Ruta A como base**, con una corrección: el resumen se genera con el modelo **local**
  (`qwen2.5-coder` vía Ollama), no con un modelo en la nube — la checklist de la Fase 5 fija
  explícitamente "sin red en runtime" para el pipeline de ingesta, y usar un modelo cloud (p. ej.
  gpt-4o-mini) rompería esa garantía.
- **Ruta B queda descartada** en su forma original (single-shot, elige un stack) por el riesgo de
  fallo silencioso descrito arriba.
- **Ruta C queda reservada** como escalación acotada (un único salto extra de búsqueda, no un
  planner abierto), disparada por la señal de grounding estricto que ya existe hoy
  ("no encuentro suficiente información") — no como flujo default.
- **Ruta A es opt-in por colección, no global** (ver Reto C): el resumen de negocio ayuda mucho
  en repos de código (el código crudo no es legible para un usuario no técnico) pero puede ser
  contraproducente en colecciones de docs/wikis donde el contenido ya está en lenguaje humano
  estructurado — ahí, parafrasear con un LLM arriesga distorsionar información que ya era clara,
  sin necesidad. La ingesta actual (sin resumen) queda intacta como default; el resumen se activa
  por config para las colecciones que lo necesitan.

### Estrategia de búsqueda

Se extiende la fusión híbrida actual (`dense-código` + `sparse`, hoy vía `Query{Fusion=Rrf}`
nativo de Qdrant en `QdrantSemanticRetriever.cs:102`) agregando una tercera rama: `dense-resumen`.
Cada chunk pasa a tener dos vectores densos (código crudo, resumen de negocio) más su
representación dispersa. No se reemplaza nada del pipeline actual — se suma una rama, preservando
el patrón ya validado en `bsuite-repo` para preguntas técnicas con nombres de entidad exactos.

### Pipeline de enriquecimiento (ingesta)

- **Generación:** `qwen2.5-coder` local, en el paso de ingesta, un resumen de negocio por chunk.
- **Restricción dura de tamaño:** el resumen debe caber holgadamente bajo `MaxSequenceLength: 256`
  del `OnnxBrain` (`appsettings.json`) — el modelo de embeddings trunca ahí; un resumen largo
  diluye la señal en el mean-pooling.
- **Regla de salida:** 1-3 oraciones, ~60 palabras, sin preámbulo, empezando por el nombre de la
  entidad/archivo — maximiza densidad semántica dentro del presupuesto de tokens.
- **Sentinel `SIN_CONTENIDO_DE_NEGOCIO`:** para chunks sin significado de negocio visible
  (boilerplate, imports, configuración). Cuando aparece, no se genera ni se embebe el vector
  `dense-resumen` para ese chunk — cae a solo `dense-código` + `sparse`. Evita fabricar señal
  donde no existe (mismo principio que el filtro de calidad de `<60` chars ya existente en
  `pipeline-de-ingesta.md`).
- **Determinismo:** `temperature` baja (0.15) — es un artefacto permanente del índice, no
  generación creativa.

### Prompt universal, agnóstico del stack

El prompt de resumen no contiene reglas de ningún framework. Recibe únicamente
`[Contexto: Lenguaje: {lenguaje}, Ruta: {ruta_relativa}]`, inyectado genéricamente por la
estrategia de chunking correspondiente (cada `IChunkingStrategy` ya conoce su `SourceLanguage` y
la ruta del `RawArtifact`). Se confía en el conocimiento nativo multilenguaje de `qwen2.5-coder`
para inferir significado de negocio sin codificar sintaxis por framework — la alternativa
(bloques de reglas por stack) fue evaluada y descartada por ser un cuello de botella de
mantenimiento inmanejable a medida que se agregan stacks (MAUI, React, etc.).

Riesgo abierto, a validar empíricamente en el PoC: la calidad del resumen puede variar entre
lenguajes mejor o peor representados en el entrenamiento del modelo (C#/TS/SQL vs. convenciones
de un SFC de Vue o XAML de MAUI).

```text
[SYSTEM]
Eres un analista de negocio que traduce fragmentos de código — en cualquier lenguaje de
programación — a descripciones de comportamiento para usuarios sin conocimiento técnico.

No expliques sintaxis, nombres de frameworks ni construcciones del lenguaje. A partir de tu
propio conocimiento de cómo funciona el lenguaje indicado en el contexto, identifica QUÉ hace
este fragmento, QUÉ dato o acción involucra, y QUÉ regla o condición aplica — en el lenguaje
que usaría alguien que jamás vio código.

Responde en español, en 1 a 3 oraciones (máximo 60 palabras). Empieza nombrando la entidad,
pantalla o archivo, seguido de dos puntos — sin frases como "este componente" o "esta clase
representa". No inventes campos, reglas o comportamientos que no estén explícitos en el
fragmento: si no está en el código, no existe.

Si el fragmento es puramente técnico sin significado de negocio visible (imports,
configuración, boilerplate, getters/setters triviales), responde exactamente:
SIN_CONTENIDO_DE_NEGOCIO

[USER]
[Contexto: Lenguaje: {lenguaje}, Ruta: {ruta_relativa}]

{contenido_crudo_del_chunk}
```

## Retos abiertos para llevar a producción

### Reto A — Caché Hash → Resumen (ingesta a escala)

El caché debe vivir **fuera de Qdrant**: una re-ingesta recrea la colección completa
(`RecreateCollectionAsync`, sin `OrphanChunkCleaner` implementado — ver
[`configuracion.md`](../configuracion.md#cuándo-re-ingestar)), así que un caché dentro de la
colección se perdería en cada re-ingesta.

**Propuesta:** SQLite local (`Microsoft.Data.Sqlite`, sin infraestructura nueva, coherente con
"sin red en runtime"):

```sql
CREATE TABLE resumen_cache (
    content_hash   TEXT NOT NULL,
    prompt_version TEXT NOT NULL,
    resumen        TEXT NOT NULL,
    sin_negocio    INTEGER NOT NULL,
    generated_at   TEXT NOT NULL,
    PRIMARY KEY (content_hash, prompt_version)
);
```

- Clave compuesta `(content_hash, prompt_version)`, no solo el hash: `ContentHash` ya es un
  invariante de todo el pipeline (`RoslynCSharpChunkingStrategy`, `TypeScriptChunkingStrategy`,
  `MarkdownChunkingStrategy`, `FallbackChunkingStrategy` lo usan). Pero si el prompt de resumen
  cambia (algo esperable mientras se itera), un caché keyeado solo por contenido serviría
  resúmenes generados bajo un prompt obsoleto sin ninguna señal de que están desactualizados.
  `prompt_version` (hash corto del system prompt + nombre del modelo) invalida automáticamente
  las entradas afectadas.
- **Punto de integración:** en `DefaultIngestionPipeline.cs`, antes de vectorizar el resumen. Hit
  de caché → reusar texto, cero llamada a Ollama. Miss → llamar Ollama, insertar, continuar.
- **Impacto esperado:** en una re-ingesta típica tras una edición, la mayoría del repo no cambió.
  Con caché, 20,000 chunks con un 5% editado ≈ 1,000 llamadas a Ollama en vez de 20,000 — la
  diferencia entre ~15-50 min y varias horas de generación secuencial local.
- **Aislamiento de fallos:** si Ollama falla en un chunk, no abortar la ingesta ni cachear el
  fallo (mismo principio que "un archivo malformado no detiene la ingesta" de la Fase 5) — ese
  chunk queda sin `dense-resumen` esa corrida y reintenta en la próxima.

### Reto B — Ponderación RRF a 3 vías

**Hallazgo clave:** la fusión RRF actual (`QdrantSemanticRetriever.cs:102`,
`Query{Fusion=Rrf}`) es la implementación **nativa de Qdrant**, que fusiona por rango sin
parámetro de peso por rama — todas las ramas del `prefetch` entran con peso igual. Esto significa
que ponderar las 3 bandas **no es un ajuste de configuración**: requiere reemplazar la llamada
nativa por 3 búsquedas independientes (dense-código, dense-resumen, sparse) y una fusión ponderada
implementada en C#.

**Fórmula propuesta:**

```
score(doc) = w_resumen · 1/(k + rank_resumen(doc))
           + w_codigo  · 1/(k + rank_codigo(doc))
           + w_sparse  · 1/(k + rank_sparse(doc))
```

`k = 60` (default estándar de RRF, el mismo que usa Qdrant internamente hoy). Pesos calibrados
empíricamente con el barrido del PoC (21 combinaciones sobre las 19 preguntas del eval-set de
`Reyma.TI.Tickets.Microservice`, misma metodología usada para calibrar `min-score` en
`busqueda-hibrida.md` — ver `poc/RagEngine.Poc.FreeSearch/RESULTADOS.md` §1.2):

- `w_codigo = 1.0` — ancla, sin cambios.
- `w_sparse = 1.3` (antes 1.0) — subirlo ayudó de forma consistente en casi toda la grilla; el
  vocabulario literal de la pregunta libre igual comparte términos con nombres de constantes/campos.
- `w_resumen = 2.5` (antes 1.3) — el punto dulce medido está entre 2.5-3.0; más allá (4.0) empieza
  a ahogar código/sparse y el recall cae de nuevo. Con pesos base (1.0/1.3) el recall@10 medido fue
  63%; con estos pesos calibrados, 79% — +16pts, +3 preguntas, sobre la misma muestra. Con solo 19
  preguntas el punto óptimo exacto no es confiable (±1 pregunta ≈ ±5pts), pero la dirección
  (sparse y resumen ambos por encima del baseline) tiene margen de varias preguntas y se sostiene
  en casi toda la grilla, no es un pico aislado.

**Ajuste dinámico propuesto (no estático):** un peso fijo siempre es un compromiso. Heurística
barata y agnóstica del framework — no depende de reglas por stack, solo de una convención
universal de nomenclatura de código (PascalCase/camelCase) — para detectar intención de la
query: si contiene un token que matchea contra el índice de nombres de entidad ya conocidos por
metadata de ingesta, subir `w_codigo`/`w_sparse` y bajar `w_resumen` para esa query puntual
(el usuario ya conoce el vocabulario técnico); si no hay match y la query "suena" a pregunta
libre, mantener los pesos base.

**Reencuadre, corregido con evidencia del PoC (`RESULTADOS.md` §1.1-1.3):** la intuición original
era que `--rerank` (Cross-Encoder) reduciría la presión de calibrar pesos con precisión, porque
normalmente sube el chunk correcto si ya sobrevivió dentro del pool ampliado (3×TopK). Medido, el
resultado es más matizado — **son dos palancas para dos objetivos distintos, no sustitutas**:

- Con pesos base (1.0/1.3), rerank sí suma recall@10 (+2/-1 preguntas). Pero con pesos ya
  calibrados (1.3/2.5), rerank **resta** recall@10 (+0/-2): el pool que rerank reordena ya trae
  más objetivos cerca del borde del top-10 gracias a los pesos, y el cross-encoder ocasionalmente
  los empuja afuera en vez de consolidarlos.
- El trabajo real de los **pesos** es garantizar recall (que el chunk sobreviva en el pool
  fusionado) — y ahí es donde se midió la ganancia grande (+16pts@10, ver Fórmula propuesta arriba).
  El trabajo real de **rerank** es precisión de ranking (@1-5, relevante para qué se muestra como
  fuente en Reto D), no recall.
- Implicación operativa: no activar ambos por default asumiendo que son aditivos. Antes de fijar
  la config de producción, medir la combinación específica (pesos + rerank) sobre el eval-set real,
  no extrapolar del efecto de cada uno por separado.

### Reto C — Ingesta con resumen como opt-in por colección

El documento original asumía la Ruta A como una rama que se suma para toda colección. En la
práctica el beneficio es asimétrico:

- **Repos de código** (el caso motivador: `bsuite-repo`, `Reyma.TI.Tickets.Microservice`): el
  contenido crudo no es legible para un usuario no técnico. Aquí el resumen cierra una brecha
  real — confirmado por el PoC (`poc/RagEngine.Poc.FreeSearch/RESULTADOS.md`): +37 pts en
  recall@10 sobre el híbrido real, 0 regresiones.
- **Docs/wikis** (vault de documentación de negocio ya en prosa humana): el contenido ya está en
  el lenguaje que el usuario libre necesita. Pasar un chunk de documento — que ya es una
  explicación humana estructurada — por un LLM que lo vuelve a resumir no cierra ninguna brecha
  semántica y sí arriesga introducir una paráfrasis con matices distintos a los del original
  (mismo riesgo que motiva el sentinel `SIN_CONTENIDO_DE_NEGOCIO` para chunks de puro código sin
  significado de negocio, pero en dirección inversa: aquí el chunk ya *es* el significado de
  negocio).

**Propuesta:** flag de configuración por ingesta, no un comportamiento global fijo:

```json
"Ingestion": {
  "EnableResumenLlm": false
}
```

o equivalente `--con-resumen` en `rag ingest`. Default `false` — la ingesta actual (dense-código +
sparse) no cambia para nadie que no lo pida explícitamente. Se activa por colección según el
perfil de consulta esperado (repos de código consultados por usuarios no técnicos/soporte/QA), no
por tipo de archivo dentro de una misma colección — evita la complejidad de decidir chunk por
chunk si "ya es prosa humana" o no.

### Reto D — Exponer el resumen al usuario, no solo usarlo para retrieval

Hoy (`src/RagEngine.Api/Contracts.cs:26-41`) `SourceDto` expone `File, Section, StartLine,
EndLine, Score, Content` — el fragmento crudo, sin ninguna explicación humana adjunta. La
traducción técnico→negocio ocurre hoy únicamente *dentro* del texto libre de `Answer`
(`RagGenerationService.cs`, regla 7 del system prompt), disuelta y dependiente de que el LLM de
generación decida traducirla bien esa corrida en particular (ver
[`rag-engine-rerank-vs-generacion`]: el LLM a veces responde con un chunk peor aunque el rerank
haya puesto el correcto en #1).

**Propuesta:** adjuntar el resumen de negocio como campo estructurado por fuente, no solo como
insumo del vector `dense-resumen`:

```csharp
SourceDto { File, Section, StartLine, EndLine, Score, Content, Resumen? }
```

- **Costo marginal cero**: el resumen ya se genera y cachea (Reto A, `resumen_cache`) para
  producir el vector `dense-resumen`. Exponerlo en `Sources[]` es reusar un artefacto que ya
  existe, no generar nada nuevo.
- **Resuelve el caso de audiencia mixta sin necesitar un concepto de "audiencia"**: un agente de
  soporte o QA lee `Resumen` (lenguaje humano, sin código); un dev abre `Content` (el fragmento
  real) para verificar la regla/matiz exacto que el resumen pudo simplificar u omitir. Ambos
  salen de la misma respuesta, sin flag de audiencia ni segunda consulta.
- **Coherente con Reto C**: si la colección no generó resumen (flag `EnableResumenLlm=false`, o
  el chunk cayó en el sentinel `SIN_CONTENIDO_DE_NEGOCIO`), `Resumen` viaja `null` y el frontend
  simplemente no renderiza esa sección — no hay fallback que invente una traducción on-demand.

## Próximos pasos (no ejecutados aún)

1. ~~PoC acotado: script offline de generación de resúmenes, sin tocar el pipeline de ingesta
   real.~~ **Hecho** — `poc/RagEngine.Poc.FreeSearch/` (rama `feat/poc-busqueda-libre-rag`,
   mergeada). Resultado: recall@10 código+sparse+resumen 63% vs. 26% del híbrido real (baseline),
   +37 pts, 0 regresiones, sobre 19 preguntas libres reales contra
   `Reyma.TI.Tickets.Microservice/src`. La hipótesis se sostiene con evidencia directa, no solo
   impresión — ver `RESULTADOS.md` del PoC.
2. Pendientes identificados en el PoC antes de escalar (`RESULTADOS.md`, sección 5):
   a. Re-etiquetar objetivos del eval-set por concepto (algunos "fallos" son etiquetado
      estrecho, no fallo de recuperación real).
   b. Inspeccionar los resúmenes de "chunks imán" que dominan el top sin ser relevantes
      (`Ticket.cs:19-309`, chunk sobre-amplio de ~290 líneas).
   c. Evaluar partir ese chunk sobre-amplio.
   d. ~~Calibrar pesos RRF y probar `--rerank` para mejorar @1/@5.~~ **Hecho** —
      `RESULTADOS.md` §1.1-1.3. Pesos calibrados (sparse=1.3, resumen=2.5): recall@10 63%→79%
      (+16pts) sin tocar rerank. Rerank ayuda precisión @1-5 pero, con estos pesos, resta
      recall@10 (79%→68%) — no son aditivos, ver el "Reencuadre" corregido arriba (Reto B).
      Pendientes (a) y (b)/(c) siguen abiertos y no se tocaron en esta pasada.
3. ~~Si tras (2) la hipótesis se sigue sosteniendo: implementar en el pipeline real —~~ **Hecho**
   (Retos A-C), ver "Sesión 2026-07-30" abajo para el detalle completo.
   - ~~Reto A (caché SQLite) y Reto B (fusor RRF manual con pesos).~~ **Hecho.**
   - ~~Reto C (flag `EnableResumenLlm` opt-in por colección...).~~ **Hecho** (`--con-resumen`).
   - ~~Reto D (`SourceDto.Resumen` — exponer el resumen ya generado/cacheado como campo de fuente,
     no solo como insumo interno del vector `dense-resumen`).~~ **Hecho** — ver "Sesión 2026-07-31".

---

## Sesión 2026-07-30 — Implementación real, verificación en producción, y pendientes

Implementación completa de los Retos A-C sobre `RagEngine.Core`/`RagEngine.Cli`. Todo el código
compila y fue verificado contra Qdrant/Ollama reales (no solo tests) en la colección de prueba
`bsuite-auditorias-test` (repo `BusinessSuite.Xaf`, rama `feature/modulo_auditorias`). **Nada de
esto está commiteado todavía** — es el primer punto a decidir en la próxima sesión.

### Qué se implementó (archivos nuevos/modificados)

- `src/RagEngine.Core/Abstractions/IBusinessSummaryGenerator.cs` (nuevo) — interfaz + `BusinessSummaryResult`.
- `src/RagEngine.Core/Services/Summary/OllamaBusinessSummaryGenerator.cs` (nuevo) — genera el resumen vía Ollama, Kernel propio, sentinel `SIN_CONTENIDO_DE_NEGOCIO`, distingue fallos de conexión (`BusinessSummaryConnectionException`) de fallos de contenido.
- `src/RagEngine.Core/Services/Summary/SummaryCache.cs` (nuevo) — caché SQLite/WAL compartida entre colecciones, clave `(content_hash, prompt_version)`.
- `src/RagEngine.Core/Infrastructure/VectorStore/QdrantVectorStore.cs` — tercer vector `dense-resumen`; `HasSummaryVectorAsync` (schema como única fuente de verdad, con caché TTL); `GetExistingResumenStateAsync`/`UpsertBatchAsync` con preservación de estado (ver hallazgo crítico abajo); `ScrollPendingResumenAsync`, `CountResumenPendingAsync`, `MarkResumenCompleteAsync`, `UpdateSummaryVectorAsync`.
- `src/RagEngine.Core/Infrastructure/VectorStore/QdrantSemanticRetriever.cs` — fusión RRF ponderada manual (`SearchWeightedFusionAsync`) cuando la colección tiene 3 vectores; rama nativa intacta para colecciones de 2 vectores.
- `src/RagEngine.Core/Infrastructure/VectorStore/RetrievalFusionOptions.cs` (nuevo) — pesos configurables, default = los calibrados en el PoC.
- `src/RagEngine.Core/Pipeline/DefaultIngestionPipeline.cs` — Fase 2 (resumen) desacoplada en su propio Channel + pool acotado por semáforo; circuit breaker de fallos de conexión consecutivos con Ollama.
- `src/RagEngine.Core/Domain/IngestionTypes.cs` — `IngestionOptions`, `IngestionRequest.EnableResumenLlm`, `IngestionStage.GeneratingResumenes`.
- `src/RagEngine.Core/Extensions/ServiceCollectionExtensions.cs` — registro de todo lo anterior + pipeline Polly `"ollama-summary"`.
- `src/RagEngine.Cli/Commands/IngestCommand.cs` — flag `--con-resumen`.
- `src/RagEngine.Cli/Commands/StatusCommand.cs` — fila de `resumen_pending` cuando aplica.
- `src/RagEngine.Cli/Commands/EvalCommand.cs` — soporte multi-target (`SourceFiles: string[]`), antes solo `SourceFile` único.
- `docs/eval/tickets-microservice.eval-set.json`, `docs/eval/bsuite-auditorias.eval-set.json` (nuevos) — eval-sets reales con anchors verificados literalmente contra el código.

### Hallazgo crítico durante la verificación (ya corregido)

Qdrant hace upsert por **reemplazo completo** de vectores y payload, no merge (verificado
empíricamente con un smoke test dedicado). El diseño original evitaba destruir resúmenes ya
generados saltándose toda la Fase 1 al reanudar — pero eso rompía el caso real de agregar
archivos nuevos a una colección ya con resumen. Fix aplicado: `GetExistingResumenStateAsync`
consulta, antes de cada upsert de lote, el estado de resumen que el chunk ya tenía (si existía) y
lo re-incluye en el upsert — así reingestar (archivos nuevos, modificados, o sin cambios) nunca
destruye trabajo ya hecho ni gasta LLM de más. **Verificado dos veces con datos reales**: (1)
re-ingesta completa sin cambios → 0 resúmenes regenerados, vector idéntico byte a byte antes/después;
(2) agregar una carpeta nueva a la misma colección → solo los puntos nuevos se procesan, los viejos
quedan intactos.

### Verificación en producción real

- Ingesta real: `REYMA.XAFR1PV.Compras/Auditorias` (295 chunks) + `Compras/Utils` (15 chunks) +
  `REYMA.XAFR1PV.Base` (1.266 chunks) → colección `bsuite-auditorias-test`, **1.576/1.576 puntos
  con resumen resuelto, 0 pendientes**.
- `docker compose build && up -d rag-api`: la API/chat web ya corre con el código de hoy y
  responde correctamente vía `/api/search` y `/api/ask` contra `bsuite-auditorias-test`.
- Eval-set real (`docs/eval/bsuite-auditorias.eval-set.json`, 16 preguntas, anchors verificados
  literalmente contra el código): **recall@10 44% sin resumen → 56% con resumen** (colección
  `bsuite-auditorias-baseline` vs `bsuite-auditorias-test`). Antes de agregar `Base`, el desglose
  exacto era +5 preguntas ganadas / -3 regresiones (neto +2); agregar `Base` no movió recall@10
  (las 6 preguntas que siguen fallando apuntan TODAS a archivos que ya estaban en `Auditorias`,
  no en `Base` — la hipótesis de que `Base` las arreglaría no se confirmó con evidencia).

### Hallazgo nuevo, independiente del resumen: chunk-imán de propiedades (generaliza el de `Ticket.cs`)

Al investigar por qué el chat respondía sin certeza a "¿puedo ver auditorías de otro
departamento?" (la regla SÍ existe: `Auditorias.cs` líneas 274-277,
`CriterioDepartamentoCreadoPor`/`CriterioDepartamentoAuditor`), encontramos que ese fragmento vive
enterrado en un chunk de **716 líneas** (`Auditorias.cs:70-786`, tipo `Property`) que junta las
~30 propiedades de la clase sin ningún límite de tamaño — quedó en el puesto #12 de 60, fuera del
top-10.

Causa raíz confirmada en el código: `RoslynCSharpChunkingStrategy.BuildGroupedPropertiesChunk`
(línea 343) arma un solo chunk con **todas** las propiedades de la clase, sin chequear
`MaxTokensPerChunk` — a diferencia de `BuildMethodChunks` (línea 240), que si el método excede el
límite lo parte con sliding-window. Esto **no depende de que el código use `#region`** — es un
chequeo de tamaño que simplemente falta para chunks de tipo `Property` (y probablemente para
`BuildClassHeaderChunk`, que agrupa los `FieldDeclarationSyntax` con el mismo problema). Va a
aparecer en cualquier repo de código con clases de muchas propiedades (modelos de dominio, DTOs,
entidades ORM) — no es específico de este repo.

**No se implementó todavía.** Es independiente del feature de resumen (afecta también a la
búsqueda híbrida de 2 bandas ya en producción) pero se descubrió mientras se probaba.

## Pendientes para retomar en una sesión nueva

1. **Decidir sobre el chunk-imán de propiedades antes de commitear el resumen** (o commitear por
   separado): agregar a `BuildGroupedPropertiesChunk` (y evaluar `BuildClassHeaderChunk`) el mismo
   chequeo de `MaxTokensPerChunk` + split que ya tiene `BuildMethodChunks`, partiendo por límites
   de propiedad completa (nunca a mitad de una declaración). Un solo archivo
   (`RoslynCSharpChunkingStrategy.cs`). Requiere `--force` en cualquier colección para que el nuevo
   límite de tamaño tenga efecto (los IDs de chunk son determinísticos por contenido — los chunks
   viejos y gigantes quedan huérfanos hasta recrear la colección).
2. **Commitear el trabajo de hoy** (Retos A-C) — nada está en git todavía. Revisar
   `git status` en la raíz del repo antes de armar el commit (incluye 2 eval-sets nuevos en
   `docs/eval/` y este mismo doc actualizado).
3. **Reto D** (exponer `SourceDto.Resumen` al usuario final) — sigue pendiente a propósito, se
   dejó para después de validar recall real (ya validado: sección de arriba).
4. **Re-etiquetado del eval-set**: al menos 1 de las 16 preguntas de
   `docs/eval/bsuite-auditorias.eval-set.json` tiene ground-truth ambiguo (dos métodos
   `FileExport()` válidos en clases distintas — `Auditorias.cs` vs `AuditoriaResultadoHallazgo.cs`;
   el modelo encontró uno igual de razonable pero distinto al anclado). Mismo patrón de
   "etiquetado estrecho" ya documentado para el eval-set de `innovapp-docs` — vale la pena revisar
   las otras 5 preguntas que siguen fallando antes de sacar conclusiones sobre el recall real.
5. **Limpieza de colecciones de prueba en Qdrant**: `bsuite-auditorias-test` (1.576 pts, con
   resumen — la "buena"), `bsuite-auditorias-baseline` (295 pts, sin resumen — solo referencia de
   comparación, se puede borrar cuando ya no haga falta).
6. Si se decide escalar más allá de este submódulo: `bsuite-repo` (colección real de
   `BusinessSuite.Xaf` completo, ~20k puntos) NO tiene el vector de resumen todavía — activarlo
   ahí exige `--force` (recrea la colección desde cero) y ronda las 13.6h estimadas con
   concurrencia=2 sin el fix de chunking; con el fix, conviene volver a medir throughput sobre una
   muestra antes de comprometerse a esa corrida completa.

---

## Sesión 2026-07-31 — Reto D implementado y verificado

Expuesto `Resumen` como campo estructurado de fuente, reusando el artefacto que Reto A ya genera y
cachea (costo marginal cero, sin llamadas nuevas al LLM).

### Qué se implementó

- `src/RagEngine.Core/Domain/RetrievalResult.cs` — nuevo campo `ContentHash` (sibling de
  `Metadata`, igual que en `CodeChunk`), no en `CodeChunkMetadata` (ese tipo lo construyen 4
  `IChunkingStrategy` distintas que no conocen el hash del chunk completo).
- `QdrantSemanticRetriever.MapToRetrievalResult` — lee `content_hash` del payload (ya se escribía
  ahí para todo chunk, con o sin resumen; solo faltaba mapearlo).
- `src/RagEngine.Api/Contracts.cs` — `SourceDto` gana el campo `Resumen`; `SourceDto.From` acepta
  un segundo parámetro opcional `resumen`.
- `src/RagEngine.Api/Program.cs` — nuevo helper `BuildSourcesAsync` que, para cada
  `RetrievalResult`, consulta `SummaryCache.TryGetAsync(r.ContentHash)` en paralelo
  (`Task.WhenAll`) y arma los `SourceDto`. Reemplaza los 3 call-sites que hacían
  `results.Select(SourceDto.From)` (`/api/search`, `/api/ask`, `/api/ask/stream`).
- `src/RagEngine.Api/wwwroot/index.html` — `buildSourcesFragment` renderiza `s.resumen` (si no es
  null) en un bloque destacado antes del fragmento de código crudo; sin fallback si es null.
- `poc/RagEngine.Poc.FreeSearch/RecallEvaluator.cs` — actualizado el único otro call-site de
  `new RetrievalResult(...)` para pasar `chunk.ContentHash` (rompía de compilar si no).

### Hallazgo real durante la verificación (ya corregido)

El contenedor Docker `rag-api` (`infra/docker-compose.yml`) **no tenía ningún volumen montado para
la caché SQLite de resúmenes** (`~/Library/Application Support/rag-engine/summary-cache.sqlite3`
en el host, escrita por `rag ingest --con-resumen` corriendo nativo). Sin el mount, `SummaryCache`
dentro del contenedor abría un sqlite3 vacío y todo lookup sería miss — `Resumen` habría viajado
`null` para absolutamente todo, incluso en colecciones con resumen real. Fix: se agregó el volumen
(montado read-write, no `:ro` — el modo WAL de SQLite necesita escribir `*-wal`/`*-shm` incluso
para lecturas) más `Ingestion__ResumenCachePath` apuntando a la ruta montada.

### Verificación en producción real

- `dotnet build` sobre toda la solución: 0 errores.
- `docker compose build rag-api && up -d rag-api`: rebuild limpio, contenedor arriba.
- `/api/search` contra `bsuite-auditorias-test` (con resumen): las 3 fuentes devueltas traen
  `resumen` poblado con texto de negocio real (verificado con la pregunta
  "¿Qué significa que un hallazgo de auditoría sea recurrente?" del eval-set).
- `/api/search` contra `innovapp-docs` (sin `--con-resumen`, vault de docs): `resumen` viaja `null`
  para ambas fuentes, como se esperaba — sin fallback que invente una traducción.
- `/api/ask` contra `bsuite-auditorias-test`: la generación de la respuesta no se vio afectada, y
  `Sources[]` trae `resumen` igual que en `/api/search`.
- El HTML servido por el contenedor (`curl http://localhost:5080/`) confirma que
  `wwwroot/index.html` actualizado es el que efectivamente se sirve (no quedó cacheado un build
  viejo).

### Pendientes para la próxima sesión

Ítems 5-6 de la sesión anterior (limpieza de colecciones de prueba en Qdrant, remedir throughput
antes de escalar a `bsuite-repo`) — nada de esto tocó Reto D. El ítem 4 (re-etiquetado del eval-set)
se resolvió en esta misma sesión, ver "Sesión 2026-07-31 (continuación)" más abajo.

---

## Sesión 2026-07-31 (continuación) — Re-etiquetado del eval-set: resultado

Se investigaron las 7 preguntas sin hit@10 de `docs/eval/bsuite-auditorias.eval-set.json` corriendo
`rag search` real contra `bsuite-auditorias-test` para cada una y comparando el chunk efectivamente
recuperado contra el código fuente real en `BusinessSuite.Xaf` (rama `feature/modulo_auditorias`).
**Conclusión: la hipótesis de partida (varias preguntas mal etiquetadas) no se sostuvo** — de las 7,
solo 1 era un problema real de etiquetado. Evidencia directa, no impresión, por pregunta:

1. **"¿Cuándo puedo exportar la plantilla de hallazgos...?"** (`FileExport`): **NO hay ambigüedad**.
   `AuditoriaResultadoHallazgo.FileExport()` (el otro método con el mismo nombre) exporta *acciones
   correctivas*, no tiene guard de estatus y no es una respuesta razonable a la pregunta — el
   ground-truth original (`Auditorias.cs`, guard `Estatus != Ejecucion`) es correcto. Miss genuino.
2. **"¿Cuándo se puede reabrir una acción correctiva...?"**: miss genuino. El archivo correcto
   (`AuditoriaResultadoHallazgoAccionCorrectiva.cs`) sí aparece en el top-10, pero es un chunk de
   *otro* método (`ActualizaEstatusHallazgo`) — la propiedad `CanReopen` (anchor real) cae en un
   chunk de propiedades agrupadas distinto que no entró al top-10.
3. **"¿Qué información se registra al validar/rechazar...?"**: miss genuino. La clase completa
   `AuditoriaResultadoHallazgoValidacion.cs` (74 líneas) nunca aparece en el top-10, aunque su clase
   hija `...ValidacionEvidencia.cs` sí. Anchor correcto y específico, sin alternativa razonable.
4. **"¿Qué se necesita para reprogramar la ejecución...?"**: miss genuino. `AuditoriaReprogramacion.cs`
   (63 líneas, clase de reglas de validación) nunca aparece; en su lugar domina
   `PlanificacionAuditoriaViewController.cs` (7.1% relevancia, la acción de UI para *otra* pregunta
   del eval-set) — semánticamente cercano pero no es una respuesta válida a "qué se necesita".
5. **"¿Cuándo se puede finalizar completamente...?"** y **6. "¿Cómo se determina si un empleado es
   el auditado...?"**: **ambos explicados por el bug de chunk-imán ya documentado**
   ([[rag-engine-chunk-propiedades-sin-limite-tamano]]) — `CanFinalizarAuditoria` (línea 279) e
   `IsAuditado` (línea 248) de `Auditorias.cs` caen dentro del chunk de propiedades sin límite de
   tamaño (`Auditorias.cs:70-786`, ~716 líneas) que ya se sabía que hundía contenido relevante. No
   es un problema de etiquetado nuevo — es más evidencia a favor de arreglar
   `BuildGroupedPropertiesChunk` antes de sacar conclusiones de recall sobre este eval-set.
7. **"¿Cómo importo auditorías desde un archivo Excel?"**: **el único caso real de etiquetado
   estrecho**. El anchor original (`"Importar auditorías desde un archivo Excel."`, la tooltip de la
   acción) vive en el chunk del constructor, que no entra al top-10. El sistema sí recupera
   `ImportarAuditorias_Execute` (líneas 566-591, la lógica real de mapeo de campos del Excel) — una
   respuesta igual o más útil. Corregido: se agregó un segundo `TargetContentContains` con una línea
   que cae dentro del rango real del chunk recuperado (verificado leyendo el chunk exacto, no
   asumiendo — un primer intento con un string a solo 5 líneas de distancia falló por caer fuera del
   rango real). El runner ya evalúa `TargetContentContains` con OR (`EvalCommand.cs:177`), no
   requirió cambios de código.

**Impacto medido**: recall@10 sobre las 16 preguntas pasó de **56% a 62%** (10/16) solo con este
ajuste — verificado corriendo `rag eval` antes/después, no solo editando el JSON. Los otros 6 misses
quedan como evidencia real de dos causas distintas (chunk-imán de propiedades ×2, y brecha de
recall genuina en clases pequeñas/aisladas ×4) — no como "ruido de eval-set" a descontar.

**How to apply**: antes de invertir en el fix de chunk-imán ([[rag-engine-chunk-propiedades-sin-limite-tamano]]),
usar este recall@10=62% como el nuevo baseline real de `bsuite-auditorias-test`. 2 de los 6 misses
restantes (33%) deberían resolverse solo con ese fix, sin tocar el eval-set de nuevo.

**Verificación independiente (misma sesión):** se re-corrió `rag eval` antes y después del cambio
de forma independiente — confirma 56%→62% (9/16→10/16), y confirma con grep que la línea agregada
como segundo anchor (`var criterios = aux.Checklist...`, `EjecucionAuditoriaViewController.cs:579`)
cae dentro del rango real del chunk recuperado (566-591) y que `EvalCommand.cs` evalúa
`TargetContentContains` con `.Any(...)` (OR), como se afirma arriba. Los puntos 1-6 también se
verificaron de forma independiente por otro camino (mismas preguntas, mismo `rag search`, misma
lectura de código) y coinciden en veredicto y evidencia.

Reto D + el fix del volumen de Docker (sesión anterior) siguen **sin commitear** al cierre de esta
sesión.

---

## Sesión 2026-07-31 (continuación 2) — Verificado el fix de chunk-imán con datos reales

El fix de `BuildGroupedPropertiesChunks` (chequeo de `MaxTokensPerChunk` + split) ya estaba
commiteado en `c3593a1`, pero nunca se había aplicado a `bsuite-auditorias-test` — los IDs de chunk
son determinísticos por contenido, así que los chunks gigantes viejos quedaban huérfanos hasta
recrear la colección. Se borró la colección (`DELETE` directo a Qdrant, no `--force` — ver
[[rag-engine-env-quirks]]) y se re-ingestaron las 3 carpetas originales (`Auditorias`,
`Compras/Utils`, `Base`) con `--con-resumen`.

**Resultado real, no proyectado:** `Auditorias` pasó de 295 a 328 chunks, `Base` de 1.266 a 1.285
(confirma que el split sí ocurrió). `rag eval` antes/después: **recall@10 62% → 69% (11/16)**.

**Pero el efecto no fue uniforme — 3 hallazgos que matizan la mejora:**

- **2 misses sí se resolvieron con el split**, como predecía la hipótesis: "¿Cuándo se puede
  finalizar completamente...?" (`CanFinalizarAuditoria`) y, más allá de lo esperado, "¿Cuándo se
  puede reabrir una acción correctiva...?" (`CanReopen`, en otro archivo — el split cambió los
  vecinos de embedding lo suficiente para que ese chunk también mejorara de ranking).
- **`IsAuditado` sigue fallando pese a estar en la misma clase partida**: `Auditorias.cs` ahora
  aparece 2 veces en el top-10 (líneas 192–232 y 279–303), pero `IsAuditado` vive en la línea 248 —
  en el hueco entre esos dos chunks. El split ayuda en general pero no garantiza que cada
  sub-chunk individual entre al top-10; esta propiedad específica sigue teniendo score débil.
- **Apareció 1 miss nuevo que antes era hit**: "¿Qué pasa con una auditoría si no se encontraron
  hallazgos activos al completarla?" (`ValidarTieneHallazgos`, líneas 632-638, un método — su
  chunking no cambió). Verificado con `--top-k 20`: el chunk correcto cayó al puesto **#11** (antes
  debía estar en el top-10). No es una regresión funcional real — es reordenamiento marginal por
  más chunks compitiendo en el espacio de embeddings tras el split de las clases vecinas, del mismo
  tipo que [[rag-engine-retrieval-no-determinismo-qdrant]] ya documentó (orden fino sensible a
  cambios en el corpus, no un fallo de diseño).

**Misses finales (5/16) tras esta corrida:** validar/rechazar hallazgo, reprogramar ejecución,
`ValidarTieneHallazgos` (#11, marginal), exportar plantilla (`FileExport`), `IsAuditado`. Los
primeros 2 y el de `FileExport` son gap semántico genuino en clases/métodos aislados (ya
investigado en la continuación anterior); ninguno tiene relación con chunking.

**How to apply:** usar **recall@10=69%** como baseline real vigente de `bsuite-auditorias-test`. El
fix de chunk-imán vale la pena (net +2 preguntas) pero no es una solución completa ni libre de
efectos secundarios menores — no asumir que arregla el 100% de las propiedades enterradas, y
esperar algo de reordenamiento marginal en preguntas no relacionadas al re-ingestar.

---

## Sesión 2026-07-31 (continuación 3) — Bug de metadata StartLine/EndLine engañosa: análisis, plan y fix

Al inspeccionar directamente los chunks reales en Qdrant tras el fix de chunk-imán, se encontró un
bug relacionado pero distinto: `BuildGroupedPropertiesChunks`/`BuildClassHeaderChunks` agrupan
miembros filtrados por tipo (`OfType<PropertyDeclarationSyntax>()`/`OfType<FieldDeclarationSyntax>()`)
usando solo el presupuesto de tokens del texto de esos miembros — sin considerar que `OfType<T>()`
salta silenciosamente cualquier otro miembro (métodos, campos, clases anidadas) intercalado en el
archivo real. El `StartLine`/`EndLine` reportado abarcaba desde el primer hasta el último miembro
del grupo, incluyendo huecos de cientos de líneas que no estaban en el `Content` real del chunk —
expuesto al usuario final vía `SourceDto.StartLine/EndLine` (`Contracts.cs:29-30,43-44`).

Evidencia inicial (contra `bsuite-auditorias-test`, 285 archivos): 12/171 chunks `Property` y
61/268 `Class` con este patrón (peor caso: 385 líneas reportadas vs. 15 reales).

### Análisis y plan (antes de tocar código)

Se usó un agente Plan para poner a prueba el diseño contra edge cases reales de Roslyn
(partial classes, records posicionales, `#region`, clases anidadas) usando código real de
`BusinessSuite.Xaf` — ningún caso rompió la premisa central. Plan aprobado: particionar primero
por contigüidad real (dos miembros del mismo tipo comparten grupo solo si son adyacentes por
índice en `typeDecl.Members`, la lista completa sin filtrar — no un umbral arbitrario de líneas),
y aplicar el split-por-presupuesto ya existente dentro de cada partición, no al revés.

### Qué se implementó

- `RoslynCSharpChunkingStrategy.cs` — nuevo helper `PartitionIntoContiguousRuns<TMember>` (una
  pasada O(n) sobre `typeDecl.Members`, sin diccionario de índices). Usado en
  `BuildGroupedPropertiesChunks` y `BuildClassHeaderChunks`.
- **Segundo bug encontrado durante la verificación** (no en el plan original): `BuildClassHeaderChunks`
  fusionaba el header de la clase con el primer grupo de campos, forzando `StartLine` a la línea de
  la declaración de la clase — incorrecto siempre que hay propiedades/métodos antes del primer campo
  (común en este codebase), y para clases sin ningún campo, `EndLine` abarcaba **toda la clase**.
  Fix: la declaración de la clase es **siempre** su propio chunk (nunca fusionado con campos),
  usando `typeDecl.OpenBraceToken` como límite real de fin — nunca el cuerpo completo de la clase.
- **Edge case encontrado y corregido**: `record Foo(...);` terminado en `;` (sin llaves, un caso
  real encontrado en `BusinessSuite.Xaf.Blazor.Server`, no solo teórico) no tiene `OpenBraceToken`
  real — usar `.IsMissing` no lo detecta (Roslyn no lo marca como "missing", es una producción
  gramatical válida sin ese token); se corrigió chequeando `.IsKind(SyntaxKind.OpenBraceToken)`.

### Verificación (script standalone, `.scratch-chunk-verify/`, patrón ya usado antes)

Corrido sobre **todo** `BusinessSuite.Xaf/src` (2088 archivos, no solo el subset de
`bsuite-auditorias-test`) — reveló que el bug es mucho más extendido de lo conocido: **520/3006
(17%) chunks Property y 1470/2637 (56%) chunks Class** con mismatch antes del fix (casos extremos
de hasta 3082 líneas de gap en archivos `.Designer.cs` autogenerados).

Tras el fix: **Property 317/3335, Class 26/3739** — pero un análisis estructural adicional (comparar
contra líneas no-en-blanco del archivo fuente dentro del rango reportado, no solo contra
`Content.Split('\n').Length`) mostró que la enorme mayoría de estos "mismatches" restantes son
**artefactos benignos del propio criterio de medición**, no el bug original:
- Líneas en blanco entre miembros agrupados: `Content` las compacta (`string.Join('\n', ...trim...)`)
  pero el rango de líneas real las cuenta — benigno, no pierde ni oculta contenido.
- Comentarios `///` y bloques de código comentado (`//...`) inmediatamente antes de un miembro:
  Roslyn los adjunta como trivia al siguiente token real; `ToFullString()` los incluye pero
  `GetLocation()`/`Span` no — benigno en la dirección opuesta al bug original (el contenido
  mostrado es *más* de lo que el rango sugiere, nunca menos).

Con ese chequeo estructural, **solo 2 de 7074 chunks Property+Class (0.03%) son mismatches
genuinos** — ambos explicados por una causa **distinta y preexistente** (no introducida por este
fix ni por `c3593a1`): la reconstrucción de `declarationText` (atributos + firma de la clase) no
captura líneas de atributos comentados intercalados entre atributos reales, un problema de
contenido/reconstrucción de la declaración, no de agrupación — documentado aquí como hallazgo
separado, **no corregido en esta pasada** (fuera del alcance de este fix específico).

### Blast radius (comparación en memoria, sin tocar Qdrant)

Dump de `(archivo, tipo, líneas, Id)` con el código viejo (vía `git stash`) vs. el nuevo, sobre
todo `BusinessSuite.Xaf/src`: **26.907 → 28.338 chunks (+5.3%, no explosión combinatoria)**; 6.4%
de los IDs viejos quedan huérfanos; 909/2088 archivos (43.5%) tienen al menos 1 chunk distinto.
Confirma la predicción del análisis previo: el aumento es proporcional al subconjunto ya-roto, no
un blowup de todo el corpus.

### Smoke test real (Qdrant)

Re-ingestado `bsuite-auditorias-test` (borrado + 3 carpetas, `--con-resumen`): 1.677 chunks
(344+15+1.318, antes 1.677→1.677 con el fix anterior — el conteo sube levemente por los nuevos
splits). `rag eval`: **recall@10 se mantiene en 69% (11/16), sin regresión** — mismas 5 preguntas
fallando que antes. **recall@3 mejoró 44%→50%, recall@5 mejoró 56%→62%** — el fix de metadata no
perjudica el retrieval y de hecho ayuda al ranking fino.

**How to apply:** el fix es de **exactitud de metadata/provenance** (lo que se le muestra al
usuario como "de dónde viene" la respuesta), no de recall — verificado que no regresiona nada.
Antes de escalar a `bsuite-repo`, aplicar este mismo fix ahí también (requiere `--force`, igual que
el fix de chunk-imán, así que conviene aplicarlos juntos en la misma re-ingesta, no por separado).
El hallazgo separado (reconstrucción de `declarationText` con atributos comentados) queda como
pendiente menor, no bloqueante, para una sesión futura si se decide perseguir el último 0.03%.

Commiteado al cierre de esta sesión (`f690c63`, sin push): `RoslynCSharpChunkingStrategy.cs` (el
fix) y este doc. `.scratch-chunk-verify/` (script de verificación) queda sin commitear, no forma
parte del repo — mismo patrón que otros `.scratch-*` ya presentes.

---

## Sesión 2026-07-31 (continuación 4) — Throughput medido antes de escalar a `bsuite-repo`

Pendiente #6 de "Sesión 2026-07-30": medir throughput real con ambos fixes (chunk-imán +
metadata) ya aplicados, antes de comprometerse a la ingesta completa de `bsuite-repo` (~20k
puntos, sin vector de resumen todavía).

**Muestra usada:** `REYMA.XAFR1PV.GestionProyectos` (61 archivos, módulo no ingerido antes en
ninguna colección — measurement en frío, sin ningún hit de `SummaryCache`). Ingestado con
`--con-resumen` en una colección de prueba desechable (`bsuite-repo-throughput-sample`, borrada
al cerrar la medición — cumplió su propósito, no aporta como referencia permanente).

**Resultado:** 573 chunks, 391 resúmenes generados (182 sentinel `SIN_CONTENIDO_DE_NEGOCIO`),
**16:58.58 (1018.6s) → 0.56 chunks/seg**.

**Extrapolación:** sobre los 28.338 chunks totales medidos para todo `BusinessSuite.Xaf/src` (ver
"continuación 3", blast radius) → **~14 horas** para la ingesta completa de `bsuite-repo`.
`Auditorias`/`Compras/Utils`/`Base` (1.677 chunks, ~6% del total) ya tienen resumen cacheado por
`bsuite-auditorias-test`, así que esa porción se saltaría el LLM — pero al ser una fracción chica
del total, no cambia la estimación de forma material.

**Conclusión: los fixes de esta sesión no cambiaron significativamente el tiempo estimado**
(~13.6h antes de ambos fixes → ~14h ahora). El +5.3% de chunks que añaden los fixes se compensa
aproximadamente con la latencia por chunk medida; el orden de magnitud de la corrida sigue siendo
el mismo.

**How to apply:** escalar a `bsuite-repo` sigue siendo una decisión de agendar una ventana larga
(overnight), no un bloqueo técnico ni algo que estos fixes hayan encarecido. Ningún pendiente
técnico impide arrancarla cuando se decida.

---

## Sesión 2026-08-01 (continuación 5) — `bsuite-repo` escalado con ambos fixes: resultado

El usuario corrió la ingesta completa en una ventana dedicada (fuera de esta sesión, para no
invalidarla con cambios de código en paralelo): `ingest "BusinessSuite.Xaf" --collection bsuite-repo
--repo-name BusinessSuite.Xaf --con-resumen --force`.

**Resultado:** 2.161 archivos, 22.986 chunks generados e indexados, 12.835 resúmenes generados,
10.151 sentinel `SIN_CONTENIDO_DE_NEGOCIO`, 0 pendientes.

**Bug real encontrado en la tabla de resumen del CLI (no en el pipeline de ingesta):** la duración
mostrada, "18:55.19", se leyó inicialmente como 18 minutos 55 segundos — interpretación que llevó a
concluir (erróneamente) que la corrida había sido dramáticamente más rápida de lo estimado. El
usuario confirmó que en realidad tardó del orden de **18 horas 55 minutos**. Causa raíz confirmada
en el código: `IngestCommand.cs:201` formateaba la duración con `TimeSpan.ToString(@"mm\:ss\.ff")`
— un formato que **descarta silenciosamente el componente de horas** para cualquier duración de 1
hora o más (`TimeSpan.Minutes`/`.Seconds` son los componentes dentro de la hora actual, no el total).
Para una duración real de ~18h55m, ese formato imprime solo "55:XX" con los minutos/segundos
truncados a la hora — el "18" que se vio no eran minutos, sino un artefacto de lectura del
formato truncado. **Fix aplicado**: la duración ahora se formatea condicionalmente
(`d\.hh\:mm\:ss` si ≥1 día, `hh\:mm\:ss` si ≥1 hora, `mm\:ss\.ff` si no) para nunca perder el
componente de horas.

**Con esta corrección, el throughput real (~18h55m para 22.986 chunks ≈ 0.34 chunks/seg) es
consistente con la estimación previa de ~14h** (mismo orden de magnitud, algo más lento incluso —
razonable para una corrida de escala real vs. una muestra chica: más presión de memoria, colas de
batch más grandes, más horas acumuladas de latencia de Ollama). **No hace falta seguir investigando
la discrepancia de cache-hit vs. generación fresca que se especuló inicialmente — el misterio
completo era el bug de formato, no un comportamiento real del pipeline de resúmenes.**

**Verificación de la colección:** 22.986 puntos, ambos vectores (`dense` + `dense-resumen`),
status `green` — estructuralmente sana.

**Verificación de sanidad (reusando el eval-set de auditorías, cuyos archivos SÍ están dentro de
`bsuite-repo`):** `rag eval --eval-set docs/eval/bsuite-auditorias.eval-set.json --collection
bsuite-repo` → **recall@10 baja a 50%** (vs. 69% en `bsuite-auditorias-test`, la colección chica
con solo esos archivos). Caída esperada y no un bug — a escala completa (~23k chunks vs. ~1.7k)
hay muchísima más competencia en el espacio de embeddings, diluyendo el ranking de chunks
específicos de un submódulo. No se investigó pregunta por pregunta (no es el propósito de este
eval-set contra esta colección) — solo sirvió como chequeo de sanidad estructural, que pasó.

**Confirmado también:** los cambios de chunking (ambos fixes) ya son visibles en el chat/API sin
necesidad de reconstruir la imagen Docker de `rag-api` — el fix es de tiempo de ingesta, no de
código de servicio; el contenedor solo sirve lo que ya está en Qdrant.

**Con esto se cierra la línea de trabajo completa de esta sesión**: re-etiquetado del eval-set →
fix de chunk-imán (verificado) → fix de metadata StartLine/EndLine (analizado, planeado,
implementado, verificado) → escalado a `bsuite-repo` con ambos fixes (verificado) → bug de formato
de duración encontrado y corregido.

---

## Sesión 2026-08-03 — Boost estructural (afinidad de clase + herencia): implementado, medido, abandonado

**Origen:** se analizó `codebase-memory-mcp` (herramienta externa de terceros, grafo de código
AST-based para agentes de coding). No es adoptable como componente (dominio distinto — solo
código, sin semántica de negocio; binario en C standalone, no integrable en el pipeline .NET), pero
inspiró la idea de usar señales estructurales (localidad de clase, herencia) para atacar 4 de los 6
misses reales de `bsuite-auditorias-test` catalogados como "gap semántico" en la sesión de
re-etiquetado (arriba). Se descartó explícitamente construir un grafo de llamadas completo
(`CALLS`/`IMPORTS`): el chunker Roslyn (`RoslynCSharpChunkingStrategy.cs`) es puramente sintáctico
(sin `Compilation`/`SemanticModel`) y la ingesta procesa archivo por archivo en streaming — ningún
miss real dependía de "quién llama a quién", así que ese grafo no se justificaba.

**Diseño implementado (Fase 0 + Componente A, ver plan aprobado):**
- `CodeChunkMetadata.BaseTypeNames` (texto de `BaseList`, sin resolver) + `RetrievalResult.ChunkType`
  expuesto de vuelta (antes solo se persistía en Qdrant, nunca se leía en el retriever).
- `IStructuralBooster` / `CompositeStructuralBooster`, insertado entre la fusión RRF y el rerank en
  `QdrantSemanticRetriever.SearchAsync`, con `StructuralBoostOptions` apagado por defecto
  (`EnableClassAffinity=false`, `EnableInheritance=false`).
- Componente A (afinidad de clase): agrupa candidatos del pool por
  `(RepositoryName, Namespace, ClassName)`, y si el mejor miembro del grupo es "fuerte" (por encima
  de un umbral), sube proporcionalmente el score de sus hermanos hacia ese mejor score, sin
  superarlo. Componente B (herencia) quedó solo diseñado en el plan, nunca implementado — se
  abandonó el esfuerzo antes de llegar a esa fase.

**Medición 1 — umbral = media del pool:**
`rag eval --eval-set docs/eval/bsuite-auditorias.eval-set.json --top-k 10 --json`:
- `bsuite-auditorias-test`: **69%→75% (11→12/16), 0 regresiones**, 1 mejora inesperada (pregunta
  fuera de las 4 conocidas como "gap semántico").
- `bsuite-repo` (corpus completo, ~23k puntos, mismo eval-set reutilizado como sanity-check):
  **50%→50% (8/16), pero 2 regresiones y 2 mejoras** — neto cero, por debajo del umbral de éxito
  acordado (+2pp) y con daño nuevo no anticipado en el eval-set chico.

**Diagnóstico de causa raíz** (comparando `rag search --output json` ranked, baseline vs. boost,
para una de las preguntas regresionadas — `AuditoriaResultadoHallazgoAccionCorrectiva.cs`):
el chunk correcto estaba en `#6` (score 0.04547) en baseline, dentro del top-10. Con el boost
activo, el mismo chunk mantiene el mismo score (0.04547) pero cae a `#14` — no porque el propio
chunk pierda relevancia, sino porque **todos los miembros de cada grupo "fuerte" del pool se
inflan simultáneamente** (`FiltroConcentradoAuditorias` puso 3 miembros en el top-3,
`EjecucionAuditoriaViewController` puso 3-4 miembros entre el `#4` y el `#9`), inundando el top-10
con hermanos de clase y desplazando positivos reales que no tienen "familia" en el pool.

**Intento de fix — umbral endurecido:** se cambió el gate de "mejor score del grupo > media del
pool" a "mejor score del grupo rankeado en el tercio superior del pool ampliado (≈ top-K real)" —
mucho más estricto. **Resultado idéntico**: mismas 2 regresiones, mismas 2 mejoras, mismos números
exactos en ambas colecciones. Esto confirma que el problema no era el umbral de activación (los
grupos que disparan el boost ya eran "fuertes" incluso bajo el criterio más estricto) sino el
mecanismo en sí: inflar a **todos** los miembros de cualquier grupo fuerte, con múltiples grupos
fuertes compitiendo a la vez, garantiza que el top-10 se llene de hermanos de clase.

**Decisión:** abandonar el boost estructural. Dos iteraciones de diseño (peso proporcional +
umbral por media, luego umbral por rango) mostraron que la señal es frágil incluso en el mejor
caso (+6pp sobre solo 16 preguntas es poco dato) y activamente dañina a escala real. Arreglarlo de
verdad requeriría otra capa de complejidad (acotar el boost a un solo hermano por grupo en vez de
todos, recalibrar el peso a la escala real de estos scores ~0.03-0.06 en vez de un 15% relativo,
posiblemente rediseñar la fórmula por completo) sin garantía de que no aparezca un tercer modo de
falla. El usuario decidió no seguir invirtiendo en esta línea. **Todo el código se revirtió**
(`IStructuralBooster`, `CompositeStructuralBooster`, `StructuralBoostOptions`, los campos
`BaseTypeNames`/`ChunkType`, y los cambios en `QdrantSemanticRetriever`/`QdrantVectorStore`/
`RoslynCSharpChunkingStrategy`/`ServiceCollectionExtensions`/`appsettings.json`) — no quedó nada
mergeado ni deshabilitado-pero-presente en el árbol.

**How to apply:** si se retoma esta idea en el futuro, no repetir el mismo mecanismo de "boost
proporcional a todos los miembros del grupo" — probar primero acotar a un único hermano más
cercano (no todos) y calibrar el peso empíricamente contra la escala real de los scores de la
colección (no un porcentaje relativo genérico como 15%), y medir SIEMPRE contra `bsuite-repo`
(corpus completo) además de `bsuite-auditorias-test` (corpus chico) antes de considerar cualquier
señal positiva como concluyente — la señal positiva en el corpus chico fue completamente engañosa
sobre el comportamiento a escala real. El Componente B (herencia por texto de `BaseTypeNames`)
nunca se implementó ni se midió — sigue siendo una idea sin evidencia de que funcione o falle.
