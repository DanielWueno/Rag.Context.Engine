# Búsqueda libre para usuarios no técnicos — RRF a 3 bandas

> **Estado: propuesta de diseño, no implementada en el pipeline real.** Este documento consolida
> un análisis arquitectónico exploratorio. No describe el comportamiento actual del sistema — para
> eso, ver [`arquitectura.md`](../arquitectura.md) y [`busqueda-hibrida.md`](../busqueda-hibrida.md).
> El PoC offline (paso 1) ya corrió y validó la hipótesis central, y ya se calibraron pesos RRF y
> se probó rerank sobre el PoC (ver "Próximos pasos" y `poc/RagEngine.Poc.FreeSearch/RESULTADOS.md`
> §1.1-1.3). Lo que sigue pendiente antes de implementar en el pipeline real (Retos A-D): re-etiquetar
> el eval-set por concepto y resolver los chunks-imán (`Ticket.cs` sobre-amplio).

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
3. Si tras (2) la hipótesis se sigue sosteniendo: implementar en el pipeline real —
   - Reto A (caché SQLite) y Reto B (fusor RRF manual con pesos).
   - Reto C (flag `EnableResumenLlm` opt-in por colección — no activar el costo del LLM en
     ingesta para colecciones donde no aporta, p. ej. docs/wikis ya en prosa humana).
   - Reto D (`SourceDto.Resumen` — exponer el resumen ya generado/cacheado como campo de fuente,
     no solo como insumo interno del vector `dense-resumen`).
