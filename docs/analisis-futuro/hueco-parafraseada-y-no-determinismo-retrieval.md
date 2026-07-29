# Hueco de recall en preguntas parafraseadas + no-determinismo de retrieval — investigación pausada

> **Estado: bloqueo de membresía resuelto el 2026-07-29 (ver "Retomado" más abajo); hueco de
> parafraseada ya desbloqueado, pendiente de retomar.** Este documento consolida lo investigado y
> probado hasta ahora. Hay dos hilos entrelazados: (1) el hueco de recall en preguntas
> parafraseadas de `innovapp-docs`, que fue el objetivo original, y (2) un hallazgo colateral que
> bloqueaba medir con confianza cualquier avance sobre (1) — no-determinismo de retrieval en
> Qdrant. (2) ya no bloquea: el baseline regenerado dio `recall@k` idéntico en corridas repetidas.
> Queda un residuo menor (orden en empates de score exacto, no afecta `recall@k` medido aquí) que
> sólo se cierra del todo con la Opción 3 (fusión RRF manual). No implementar nada de lo listado
> aquí sin releer el estado primero.

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

## Retomado el 2026-07-29 — Opción 1 aplicada, con evidencia directa

Se aplicó la opción 1 (la más barata): `PATCH /collections/innovapp-docs` con
`optimizers_config.default_segment_number=1`. Qdrant fusionó los 6 segmentos a 1 en ~15s (743
puntos). **Cambio aplicado y dejado así** — no revertido, es una ganancia neta sin downside
observado (`status: green`, `optimizer_status: ok`).

Se repitió el repro de 5 corridas (misma pregunta, `rag search --top-k 10 --output json`) y se
comparó programáticamente (Python, no a ojo):

- **El CONJUNTO de 10 `chunk_id` es idéntico en las 5 corridas.** Antes (6 segmentos), un chunk
  con regla real podía faltar por completo en algunas corridas — ese síntoma, el más grave, ya no
  se reprodujo ni una vez en 5 intentos.
- **El ORDEN dentro de ese conjunto sigue variando.** Comparando el score por posición entre las 5
  corridas: las posiciones que cambian de chunk son EXACTAMENTE las que tienen score empatado
  (posiciones 1-2 ambas en 0.5, 3-4 en 0.333, 5-6 en 0.25, 9-10 en 0.167); las posiciones 7 y 8,
  con score único sin empate, fueron estables en las 5 corridas sin excepción.

**Diagnóstico ahora es exacto, no una hipótesis:** el no-determinismo restante está 100% aislado a
desempates de score idéntico. Reducir a 1 segmento eliminó la fusión-entre-segmentos como fuente
(la más grave, afectaba membresía), pero el empate de score en sí no tiene regla de desempate
estable en Qdrant — coincide exactamente con el síntoma del issue #6814 ya referenciado ("tied
documents changing their ranking"). Esto ya no es hipótesis por descarte; es la causa confirmada.

**Por qué el empate importa igual:** un empate en posición 1-2 puede voltear `recall@1` de una
corrida a otra para la misma pregunta si el objetivo es uno de los dos empatados. Para `recall@10`
no importa mientras el grupo de empate no cruce el corte de 10 (en este repro, el grupo 9-10 sí
queda justo en el borde — con un grupo de empate más grande podría empujar algo fuera del top-10
de forma no determinista).

**Conclusión — la opción 3 ya no es especulativa, es el único camino que cierra esto del todo:**
reducir segmentos (opción 1) ya está aplicado y ayuda, pero no alcanza porque el empate persiste
dentro de un solo segmento. Sólo una fusión RRF manual en C# con desempate explícito por
`ChunkId` (opción 3) elimina el empate sin regla. Sigue siendo el mismo cambio que
[[rag-engine-3bandas-pesos-calibrados]] (Reto B de `busqueda-libre-rag-3-bandas.md`) necesita para
los pesos por rama — un solo cambio resuelve ambos pendientes.

**Pendiente de decidir con el usuario:** si implementar la opción 3 ahora (justificada por dos
frentes: cierra el no-determinismo Y habilita Reto B) o seguir con el hueco de parafraseada
primero, ahora que el no-determinismo de membresía (el que invalidaba comparaciones) está resuelto
y solo queda el de orden-en-empates (que no afecta recall@10 en la mayoría de los casos).

## Por qué se pausó aquí (contexto histórico, previo a la reanudación de arriba)

El no-determinismo invalida cualquier comparación de una sola corrida — incluyendo el baseline
guardado (`docs/eval/baselines/innovapp-docs.rerank.baseline.json`) y el experimento HyDE. No
tiene sentido seguir afinando HyDE (ni nada que dependa de medir recall) hasta no resolver o
al menos mitigar esto primero.

## Opciones evaluadas para el no-determinismo

1. **Reducir la colección a 1 segmento** (consolidar vía `optimizers_config` de Qdrant) — la
   opción más simple y barata; con 743 puntos, 1 segmento es razonable y elimina el problema de
   raíz (ya no hay "entre segmentos" que fusionar). **Aplicada el 2026-07-29** (ver "Retomado"
   arriba) — estabilizó el CONJUNTO de candidatos por completo, pero no el orden dentro de él:
   el empate de score en sí no tiene desempate estable, con o sin múltiples segmentos.
2. **Forzar `Params.Exact = true`** en las prefetch queries de `QdrantSemanticRetriever` —
   ya no es necesario probarlo: el diagnóstico de la opción 1 aisló la causa restante a
   desempates de score exacto, no a aproximación del índice (que de por sí ya no aplicaba, la
   colección hace full-scan por su tamaño).
3. **Reimplementar la fusión RRF en C#** (reemplazando la nativa `Query{Fusion=Rrf}` de Qdrant)
   con desempate explícito por `ChunkId` — **ahora es la única opción que cierra el
   no-determinismo del todo** (ver "Retomado" arriba), y de paso habilita los pesos por rama de
   Reto B en `busqueda-libre-rag-3-bandas.md` (ya calibrados en el PoC, ver
   [[rag-engine-3bandas-pesos-calibrados]]) — dos problemas con un solo cambio si esta línea se
   retoma.
4. **Fijar `seed`** en la llamada a Ollama — no toca Qdrant, pero elimina la otra fuente de
   variabilidad (generación), independiente de lo anterior. Sigue sin probarse.

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

1. ~~Releer este documento + memorias relacionadas.~~ **Hecho el 2026-07-29.**
2. ~~Decidir primero sobre el no-determinismo (bloqueante) — empezar por la opción 1.~~ **Hecho** —
   aplicada, membresía estable, orden-en-empates sigue variando (ver "Retomado" arriba).
3. **Pendiente:** decidir si implementar la opción 3 (fusión RRF manual en C#, cierra el
   no-determinismo restante Y habilita Reto B) antes de seguir, o aceptar el estado actual
   (membresía estable, orden puede voltear recall@1 en casos de empate) como suficiente para
   retomar (4) ya.
4. ~~Re-generar el baseline y confirmar que corridas repetidas dan `recall@k` idéntico.~~ **Hecho
   el 2026-07-29** — dos corridas de `rag eval --collection innovapp-docs --rerank --json` dieron
   `recall@k` byte-idéntico (18%/50%/55%/55% @1/3/5/10, 22 preguntas con ground truth) y 0
   preguntas con `hit_any_at_k` distinto entre corridas. Comparado contra el baseline viejo
   (generado antes del fix): los `recall@k` resultaron ser exactamente los mismos — sólo cambió el
   `top_score` crudo en 8 preguntas (tercer/cuarto decimal, residuo esperado del empate de score),
   nunca un hit/miss. El baseline viejo era, en los hechos, numéricamente correcto; ahora hay
   prueba directa en vez de sospecha. Baseline regenerado y versionado.
5. Retomar el hueco de parafraseada con mediciones confiables — ya no bloqueado.

## Archivos y comandos relevantes

- `docs/eval/innovapp-docs.eval-set.json` — eval-set ground-truth (26 preguntas)
- `docs/eval/baselines/innovapp-docs.rerank.baseline.json` — baseline regenerado el 2026-07-29
  sobre la colección ya consolidada a 1 segmento; confirmado determinista (`recall@k` idéntico en
  2 corridas). Ya no lleva la advertencia de las versiones anteriores de este documento.
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
