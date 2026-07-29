# Hueco de recall en preguntas parafraseadas + no-determinismo de retrieval — investigación pausada

> **Estado: pausada el 2026-07-24, para retomar.** Este documento consolida lo investigado y
> probado hasta ahora. Hay dos hilos entrelazados: (1) el hueco de recall en preguntas
> parafraseadas de `innovapp-docs`, que fue el objetivo original, y (2) un hallazgo colateral más
> urgente — no-determinismo de retrieval en Qdrant — que bloquea poder medir con confianza
> cualquier avance sobre (1). No implementar nada de lo listado aquí sin releer el estado primero.

## Contexto / cómo se llegó aquí

Se construyó un eval-set real de ground-truth (`docs/eval/innovapp-docs.eval-set.json`, 26
preguntas, categorizadas: literal/parafraseada/ambigua/fuera-de-dominio/regresión-conocida) y un
comando real (`rag eval`, `src/RagEngine.Cli/Commands/EvalCommand.cs`) que lo corre contra
`ISemanticRetriever` real (mismo código que `search`/`ask`) y calcula recall@{1,3,5,10}.

Primera medición (22 preguntas con ground truth, excluye fuera-de-dominio):

| | recall@1 | recall@3 | recall@5 | recall@10 |
|---|---|---|---|---|
| Sin rerank | 5% | 14% | 18% | 36% |
| Con `--rerank` | 18% | 50% | 55% | 55% |

Por categoría con rerank: literal 100%@10, **parafraseada solo 12%@10**, ambigua 50%@10 (25%
full-coverage de todos los anchors), regresión-conocida 50%@10. El hueco con más impacto para el
caso de uso real (usuario no técnico) es "parafraseada" — ahí se centró la investigación.

Esto **contradice/matiza** una memoria anterior que decía que el híbrido "funciona muy bien
out-of-the-box" en vaults de docs — esa conclusión se basó en 1 sola pregunta de smoke-test, no
en un eval-set real.

## Experimento 1: HyDE (reescritura de query con LLM local)

**Hipótesis:** generar una respuesta hipotética con `qwen2.5:7b-instruct` (Ollama local) y
usarla como query en vez de la pregunta cruda, para acercar el vocabulario coloquial al formal
del documento.

**Diagnóstico previo (importante):** se confirmó que el hueco NO es un problema de ranking —
el chunk correcto no aparece ni en un pool ancho (top-60, sin rerank) para las preguntas que
fallan. Rerank no puede arreglar lo que nunca entra al pool.

**Resultado (probado en 3 preguntas, con contexto ampliado a las 8):** mixto e inconsistente.
- 1/3 mejoró notablemente: de "no aparece en top-60" a **rank #2 en top-10**.
- 2/3 no mejoró nada (ni en top-60), porque el LLM local **inventó terminología equivocada** al
  no conocer el sistema real — dijo "Marcar como Resuelto/Revisado" cuando el término real del
  documento es "Leído"; en otro caso hasta invirtió el sentido de una regla real (dijo que NO se
  necesita permiso especial cuando la regla real dice que SÍ).
- Concatenar pregunta original + HyDE (en vez de reemplazar) tampoco ayudó en los 2 casos que
  fallaban.

**Conclusión parcial:** HyDE muestra promesa real pero no es confiable en su forma simple
(single-shot, sin anclaje al vocabulario real del sistema). No descartado — ver alternativas
(few-shot con glosario) en la sección de próximos pasos.

## Hallazgo colateral (más urgente): no-determinismo de retrieval en Qdrant

Al verificar manualmente uno de los "miss" del eval-set, se encontró que **la misma pregunta,
exactamente el mismo texto, en corridas separadas de `rag search`, devuelve conjuntos de
candidatos distintos** — no solo reordenamiento menor. Un chunk con una regla real (`RN-2.4`)
aparece en el top-10 en 5 de 8 corridas repetidas y no aparece en absoluto en las otras 3.

**Aislamiento hecho (experimentos reales, no solo teoría):**
1. Persiste **sin** `--rerank` (5/8 coinciden, 3/8 divergen) → descarta al cross-encoder.
2. Se forzó `IntraOpNumThreads = 1` en `OnnxVectorizationBrain.cs` (sospecha de no-asociatividad
   flotante multi-hilo) → sin cambio, seguía divergiendo. Descartado y revertido.
3. **Prueba definitiva:** se instrumentó temporalmente un dump del vector de embedding (primeros
   8 floats) para la misma pregunta en 4 procesos separados → **idéntico bit a bit las 4 veces**.
   Embedding descartado por completo como causa. Instrumentación ya revertida, repo limpio.
4. Por descarte + confirmación externa: es un **bug conocido y actualmente abierto de Qdrant**
   — [issue #6814](https://github.com/qdrant/qdrant/issues/6814), *"Tied documents changing
   their ranking with each Hybrid Search"*. Describe exactamente el síntoma: empates de score al
   fusionar RRF entre segmentos no tienen desempate estable entre corridas. La colección
   `innovapp-docs` tiene 6 segmentos (`shard_number: 1`, así que el patrón de "fusión anidada en
   prefetch por shard" que también causa este problema en setups multi-shard no aplica aquí — es
   específicamente el merge entre segmentos dentro de un mismo shard).

**Efecto medido:** se concentra en preguntas marginales (score bajo — exactamente "parafraseada"
y "ambigua"). Las preguntas "literal" (score alto, dominante) fueron 100% estables en las
pruebas. Esto interactúa con el gate de confianza de 3 niveles (`RagGenerationService`,
calibrado para `TopK=10`): la misma pregunta podría cambiar de banda (corta / hedge) entre una
consulta y otra, no solo el ranking interno.

**Eje separado, no relacionado:** la generación (`Ollama`, `temperature = 0.1` en
`RagGenerationService.cs:498`, sin `seed` fijo) tiene su propia fuente de variabilidad,
independiente de retrieval. No se ha tocado ni investigado a fondo en este hilo.

## Por qué se pausó aquí

El no-determinismo invalida cualquier comparación de una sola corrida — incluyendo el baseline
guardado (`docs/eval/baselines/innovapp-docs.rerank.baseline.json`) y el experimento HyDE. No
tiene sentido seguir afinando HyDE (ni nada que dependa de medir recall) hasta no resolver o
al menos mitigar esto primero.

## Opciones evaluadas para el no-determinismo (ninguna implementada aún)

1. **Reducir la colección a 1 segmento** (consolidar vía `optimizers_config` de Qdrant) — la
   opción más simple y barata; con 743 puntos, 1 segmento es razonable y elimina el problema de
   raíz (ya no hay "entre segmentos" que fusionar).
2. **Forzar `Params.Exact = true`** en las prefetch queries de `QdrantSemanticRetriever` —
   rápido de probar, pero podría no cambiar nada si el problema es el desempate del merge y no
   el índice aproximado (la colección ya debería estar haciendo full-scan por su tamaño).
3. **Reimplementar la fusión RRF en C#** (reemplazando la nativa `Query{Fusion=Rrf}` de Qdrant)
   con desempate explícito por `ChunkId` — más esfuerzo, pero resolvería esto **y** habilitaría
   los pesos por rama que ya estaban pendientes para Ruta A (Reto B en
   `busqueda-libre-rag-3-bandas.md`) — dos problemas con un solo cambio si esa línea se retoma.
4. **Fijar `seed`** en la llamada a Ollama — no toca Qdrant, pero elimina la otra fuente de
   variabilidad (generación), independiente de lo anterior.

## Opciones evaluadas para el hueco de parafraseada (bloqueado por lo anterior)

1. Ampliar el experimento HyDE a las 8 preguntas completas de forma confiable, una vez resuelto
   el no-determinismo (para no medir sobre ruido).
2. Explorar un modelo de embedding más fuerte (implica re-ingesta, decisión más pesada).
3. Aceptar el guardrail existente (gate de 3 niveles) como mitigación suficiente — verificar que
   efectivamente hace que el sistema dude/pida aclaración en estos casos en vez de fallar en
   silencio, antes de seguir invirtiendo en cerrar el recall puro.
4. HyDE few-shot con glosario de 3-5 términos reales del corpus, para reducir el riesgo de que
   el LLM invente terminología equivocada.

## Cómo retomar

1. Releer este documento + memorias relacionadas: `rag-engine-eval-set-innovapp-docs`,
   `rag-engine-retrieval-no-determinismo-qdrant`.
2. Decidir primero sobre el no-determinismo (bloqueante) — probablemente empezar por la opción 1
   (reducir a 1 segmento) por ser la más barata de probar.
3. Una vez estabilizado, re-generar el baseline (`rag eval --collection innovapp-docs --rerank
   --json`) y confirmar que corridas repetidas dan resultados idénticos antes de confiar en
   ningún número.
4. Retomar el hueco de parafraseada con mediciones confiables.

## Archivos y comandos relevantes

- `docs/eval/innovapp-docs.eval-set.json` — eval-set ground-truth (26 preguntas)
- `docs/eval/baselines/innovapp-docs.rerank.baseline.json` — baseline guardado (advertencia:
  puede estar afectado por el no-determinismo, no tratar como número sólido todavía)
- `src/RagEngine.Cli/Commands/EvalCommand.cs` — comando `rag eval`
- `src/RagEngine.Core/Infrastructure/VectorStore/QdrantSemanticRetriever.cs:98-117` — fusión RRF
  nativa de Qdrant (foco de la opción 3 de arriba)
- `src/RagEngine.Core/Infrastructure/Vectorization/OnnxVectorizationBrain.cs` — embedding, ya
  descartado como causa del no-determinismo
- `src/RagEngine.Core/Services/Generation/RagGenerationService.cs:498` — temperature de
  generación (eje separado de variabilidad)

Comando para reproducir la investigación de no-determinismo:

```bash
Q="Si escojo 'otro' como razón para cancelar, ¿tengo que explicar por qué?"
for run in 1 2 3 4 5; do
  dotnet run --project src/RagEngine.Cli -- search "$Q" --collection innovapp-docs --top-k 10 --output json > /tmp/repro_${run}.json
done
# comparar los 5 archivos — deberían diferir en al menos 1 de 5
```
