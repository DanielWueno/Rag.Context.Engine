# Búsqueda libre para usuarios no técnicos — RRF a 3 bandas

> **Estado: propuesta de diseño, no implementada.** Este documento consolida un análisis
> arquitectónico exploratorio. No describe el comportamiento actual del sistema — para eso,
> ver [`arquitectura.md`](../arquitectura.md) y [`busqueda-hibrida.md`](../busqueda-hibrida.md).
> Las decisiones aquí están sujetas a validación mediante un PoC antes de tocar el pipeline real.

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

`k = 60` (default estándar de RRF, el mismo que usa Qdrant internamente hoy). Pesos base a
calibrar empíricamente con un set de ~20 preguntas mixtas (libres + técnicas), misma metodología
usada para calibrar `min-score` en `busqueda-hibrida.md`:

- `w_resumen = 1.3` — boost, es la rama que atiende el caso hoy desatendido (usuario no técnico).
- `w_codigo = 1.0`, `w_sparse = 1.0` — baseline, para no regresionar el patrón ya validado en
  `bsuite-repo`.

**Ajuste dinámico propuesto (no estático):** un peso fijo siempre es un compromiso. Heurística
barata y agnóstica del framework — no depende de reglas por stack, solo de una convención
universal de nomenclatura de código (PascalCase/camelCase) — para detectar intención de la
query: si contiene un token que matchea contra el índice de nombres de entidad ya conocidos por
metadata de ingesta, subir `w_codigo`/`w_sparse` y bajar `w_resumen` para esa query puntual
(el usuario ya conoce el vocabulario técnico); si no hay match y la query "suena" a pregunta
libre, mantener los pesos base.

**Reencuadre que baja la presión de acertar los pesos:** `operaciones.md` ya documenta que si el
chunk correcto entra al pool ampliado (3×TopK) pero queda mal rankeado por RRF, `--rerank`
(Cross-Encoder) normalmente lo sube. El trabajo real de los pesos no es lograr el ranking final
perfecto — es garantizar **recall** (que el chunk correcto sobreviva dentro del pool fusionado).
Evaluar en el PoC si activar `--rerank` por default en el "modo libre" reduce la necesidad de
calibrar pesos con precisión.

## Próximos pasos (no ejecutados aún)

1. PoC acotado: 50-100 archivos de un mismo módulo de negocio, cruzando stacks, con script
   offline de generación de resúmenes (sin tocar el pipeline de ingesta real todavía).
2. Gate de validación manual sobre la muestra generada, comparando calidad de resumen entre
   lenguajes/stacks antes de comprometerse a escalar.
3. Si el PoC valida la hipótesis: implementar el caché SQLite (Reto A) y el fusor RRF manual con
   pesos (Reto B) en el pipeline real.
