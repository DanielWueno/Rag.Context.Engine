# Guardrail de dominio para el chat RAG — gate de confianza en 3 niveles

> **Estado: propuesta de diseño, no implementada.** Este documento consolida un análisis
> exploratorio a partir de logs reales del piloto (`logs/rag-api-2026072{1,2}.json`) y
> experimentos ad-hoc contra `innovapp-docs`. No describe el comportamiento actual del
> sistema — para eso, ver [`arquitectura.md`](../arquitectura.md) y
> [`busqueda-hibrida.md`](../busqueda-hibrida.md). Las decisiones aquí están sujetas a
> calibración empírica antes de tocar el pipeline real.
>
> **No confundir con [`busqueda-libre-rag-3-bandas.md`](busqueda-libre-rag-3-bandas.md)**:
> ese documento propone 3 *ramas de retrieval* (dense-código + dense-resumen + sparse)
> para el problema de vocabulario técnico vs. de negocio en `bsuite-repo`. Este documento
> propone 3 *niveles de confianza en el gate posterior a retrieval*, para el problema de
> respuestas fuera de dominio en `innovapp-docs`. Son mecanismos distintos que solo
> comparten el número "3" por coincidencia.

## El problema

Desde que se expuso el chat del piloto (`RagEngine.Api`), los logs reales muestran que
responde con confianza a preguntas totalmente fuera del corpus (traducir al alemán, la
Guerra de los Pasteles, una pirámide de asteriscos en PowerShell) y, en un caso
confirmado, inventó un hecho sobre el problema personal de un usuario (afirmó que su
mouse era "nuevo" y tenía "un par de semanas de uso" — dato que el usuario nunca dio).

## Causas descartadas y confirmadas (investigación empírica)

- El único guardrail hoy es una instrucción de prompt ("responde solo con el contexto,
  si no aplica dilo") — no hay ningún control verificable en código más allá de "¿hay 0
  chunks tras el umbral de 0.10?" (`RagGenerationService.cs:223-228`), y ese umbral solo
  se aplica al prefetch denso (`QdrantSemanticRetriever.cs:84-96`), nunca al score final.
- El score RRF (el único siempre disponible sin `--rerank`) es puramente posicional, no
  comparable entre queries — solo el score del cross-encoder (sigmoide [0..1]) es un
  proxy de relevancia real, y hoy el rerank es **opt-in del cliente**
  (`Program.cs`: `request.Rerank ?? false` en los tres endpoints), no default del
  servidor.
- Verificado con 36 queries reales de producción (ambos días de logs) + inspección
  directa del corpus fuente (`docs-bsute-innovapp-plan/`): los casos reales de
  fabricación (mouse, "ayúdame a redactar un ticket") puntúan con el cross-encoder casi
  tan bajo como el ruido puro (0.017–0.070). Pero un caso de score casi idéntico
  ("Procesos que influyen en la innovapp?", 0.071) resultó ser una **pregunta de negocio
  legítima con contenido real en el corpus, mal rankeada**: el chunk correcto (tabla de
  Fases del proyecto en `Plan_De_Trabajo_InnovApp_Resumen.md`) entra al pool de
  candidatos (rank 11 de 30) pero pierde contra un falso positivo léxico — un chunk
  técnico que menciona "sin terminar el **proceso**" en sentido de threading/OS, no de
  negocio. Esto prueba que **un único umbral no puede separar ambos casos** — necesita
  tratarse como confianza (banda media = mostrar con reserva), no como corte binario.
- Al intentar calibrar ese umbral se descubrió un problema más fundamental: el score del
  cross-encoder **no es reproducible** entre corridas idénticas de la misma query
  (0.0575 vs 0.071 para el mismo chunk ganador, en corridas consecutivas con parámetros
  idénticos). Aislado con un experimento controlado (candidatos fijos, mismo orden, 3
  corridas directas contra `OnnxCrossEncoderReRanker` sin pasar por retrieval → score
  idéntico a 6 decimales), se confirmó que **el cross-encoder ONNX sí es determinista**;
  la causa real es que Qdrant rompe empates de score RRF de forma no determinista entre
  corridas (confirmado: mismo conjunto de 10 candidatos siempre, pero orden distinto
  entre los que empatan en score RRF), lo que reordena qué candidatos caen en cada lote
  de 8 antes del rerank — y el padding dinámico por lote (`OnnxCrossEncoderReRanker.cs:140-156`),
  combinado con la cuantización int8 del modelo, produce scores ligeramente distintos
  según con quién quede agrupado cada candidato. **Este fix debe ir antes de calibrar
  cualquier banda**, o se estaría calibrando contra ruido de medición.
- Las meta-preguntas sobre el asistente ("¿quién eres?", "¿con qué tecnología estás
  hecho?") puntúan casi cero con el cross-encoder en 6 de 7 casos reales observados — un
  gate de score las bloquearía bien por accidente — salvo "¿qué proyecto analizas?"
  (0.439), que pasa cualquier umbral razonable por solape léxico con contenido real del
  proyecto aunque la intención es sobre el asistente, no sobre el contenido. El score
  nunca va a resolver esto — hace falta una señal de intención aparte.
- Nota aparte verificada en logs (`rag-api-20260722.json`, campo `Answer` de
  `QueryEvent`): el intercambio "cuántos archivos te constituyen" → "cuéntalos" →
  "explícame el número 47" **no fue fabricación** — el modelo listó archivos reales,
  luego confundió el conteo de archivos con un conteo real de RF (Requerimientos
  Funcionales, 47) presente en otro chunk del contexto, y finalmente explicó RF-47
  correctamente con hedge honesto. Es un error de referente entre dos listas distintas
  en contexto, no invención — no requiere ningún cambio de este plan.

**Alcance explícito — qué NO resuelve este plan:** el gate evita el daño (no fabrica con
falsa seguridad, no bloquea con exceso de confianza), pero no arregla que a "procesos
que influyen en la innovapp" se le siga mostrando como mejor candidato un chunk
equivocado en vez de la tabla correcta enterrada en rank 11. Eso es un problema de
ranking/recall (mismatch de tokens 512/256 ya documentado en
[`busqueda-libre-rag-3-bandas.md`](busqueda-libre-rag-3-bandas.md) + el nuevo dato de
falso positivo léxico "proceso" vs "fases") que pertenece al hilo paralelo de recall,
no a este guardrail.

## Piezas a implementar

### 0. Fix de reproducibilidad del reranker (prerequisito, bloquea calibrar bandas)

**Archivo:** `src/RagEngine.Core/Infrastructure/Reranking/OnnxCrossEncoderReRanker.cs`

En `ScorePairs` (~línea 118-156), ordenar `candidates` por una clave estable
(`ChunkId`) **antes** de agruparlos en lotes de `BatchSize`. Esto garantiza que, sin
importar en qué orden Qdrant entregue empates de RRF, el reranker siempre agrupa los
mismos candidatos juntos — mismo padding dinámico, mismo resultado, sin tocar el costo
de inferencia (a diferencia de forzar padding fijo a 512, que sí lo tocaría).

### 1. Rerank activado por default en el servidor

**Archivo:** `src/RagEngine.Api/Program.cs`

En los tres endpoints (`/api/search`, `/api/ask`, `/api/ask/stream`), cambiar
`var rerank = request.Rerank ?? false;` → `?? true`. Sin esto no hay score comparable
entre queries que el gate pueda usar (el RRF puro no sirve, según lo ya documentado en
el propio código en `QdrantSemanticRetriever.cs:84-87`).

### 2. Gate de confianza en 3 niveles (aplicado al score final, no al de prefetch)

**Archivo:** `src/RagEngine.Core/Services/Generation/RagGenerationService.cs`, en
`AskStreamingAsync`, inmediatamente después del check actual de `chunks!.Count == 0`
(línea 223-228) — se extiende esa misma idea a 3 bandas en vez de a un binario
"0 o no-0":

- **Banda alta** (score del chunk top ≥ `HighConfidenceThreshold`): flujo actual sin
  cambios.
- **Banda media/ambigua** (`LowConfidenceThreshold` ≤ score < `HighConfidenceThreshold`):
  se genera igual (para aprovechar el mejor candidato disponible, aunque sea débil),
  pero se añade un addendum corto al system prompt ya elegido
  (`CodeSystemPromptTemplate`/`DocsSystemPromptTemplate`) que obliga a enmarcar la
  respuesta como baja confianza y ofrecer el mejor candidato en vez de afirmarlo como
  hecho — no se necesita un tercer template completo, solo un fragmento que se concatena
  al elegido.
- **Banda baja** (score < `LowConfidenceThreshold`): mismo camino que el actual "0
  chunks" — corta antes de generar, devuelve `NoContextFallbackMessage`.

Los valores de `HighConfidenceThreshold`/`LowConfidenceThreshold` van como
constantes/opciones configurables (no hardcodeadas sin más), pero **su calibración
final es un paso posterior a este documento**: se hace repitiendo cada query de
calibración varias veces (una vez estabilizado el score con el ítem 0) y usando el
rango observado con margen, con la misma metodología de preguntas reales ya usada en
esta investigación — no un único punto de corte asumido de los números vistos aquí.

### 3. Pre-filtro de intención para meta-preguntas

**Archivo:** `src/RagEngine.Core/Services/Generation/RagGenerationService.cs`, en
`AskStreamingAsync`, **antes** del paso de retrieval (paso 1 actual).

Lista cerrada de patrones (de las frases reales ya observadas en logs: "quién eres",
"qué proyecto/tecnología/modelo", "con qué estás entrenado", "en qué idioma",
"alucinaciones", "cuántos archivos/documentos"). Si matchea, se salta retrieval y
generación libre por completo y se responde con un bloque de auto-descripción fijo e
inyectado como hecho verdadero (no se le pide al LLM que "recuerde" quién es). Esto
resuelve por prioridad el caso "¿qué proyecto analizas?": el pre-filtro de intención se
evalúa antes que el score, así que gana sobre la coincidencia léxica accidental que hoy
lo dejaría pasar con score alto por razones equivocadas.

## Verificación propuesta

1. **Ítem 0**: repetir el experimento de esta investigación — mismo batch fijo de
   candidatos, 3 corridas — debe seguir dando score idéntico. Además, repetir la query
   real ("Procesos que influyen en la innovapp?") contra el pipeline completo (con
   retrieval) 3 veces seguidas y confirmar que el score del top-1 ya no varía.
2. **Ítems 1-2**: usar las 36 queries reales ya recolectadas de
   `logs/rag-api-2026072{1,2}.json` como set de regresión: los casos de off-topic puro
   deben caer en banda baja, "ayúdame a redactar un ticket"/mouse en banda baja-media
   con hedge (no fabricación), y las preguntas de dominio con score alto deben responder
   igual que hoy.
3. **Ítem 3**: repetir las meta-preguntas reales observadas ("quién eres", "qué proyecto
   analizas", etc.) y confirmar que responden con el bloque fijo sin tocar
   retrieval/generación libre.
4. Correr `rag search --rerank --output json` (CLI ya existente) para inspeccionar
   manualmente scores durante la calibración de los umbrales del ítem 2, igual que se
   hizo en esta investigación.

## Próximos pasos (no ejecutados aún)

1. Implementar el ítem 0 y verificar reproducibilidad antes de tocar nada más.
2. Activar el ítem 1 (rerank default) y remedir la distribución de scores reales del
   piloto con el score ya estabilizado.
3. Calibrar `HighConfidenceThreshold`/`LowConfidenceThreshold` empíricamente contra el
   set de 36 queries reales + el rango de variación observado.
4. Implementar ítems 2 y 3, y correr la regresión completa antes de desplegar al piloto.
