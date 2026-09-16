# 7.d — Investigación: ¿corte adaptativo o min_score/top_k fijo?

Item: `7.d-investigar-corte-adaptativo-de-ruido`. Investigación pura — no cambia
comportamiento de producción. Instrumentación: `EvalCommand` ganó un flag
aditivo `--dump-hits` (solo bajo `--json`) que expone `rank/score/is_target_file/
matches_anchor` por resultado crudo, sin filtrar por `min-score`, para poder
simular distintas políticas de corte en post-proceso sin re-consultar Qdrant por
cada estrategia. No toca `QdrantSemanticRetriever` ni `RetrievalFusionOptions`.

## Hallazgo previo que redefinió el método

El plan original comparaba "min_score global actual" filtrando el *score final
devuelto* (`RetrievalResult.SimilarityScore`) contra 0.10. Al correr esto sin
rerank, **el 100% del recall se pierde en los 4 sets** — no porque 0.10 sea mal
umbral, sino porque `RetrievalOptions.MinimumSimilarityScore` **no filtra ese
score**: solo aplica como `ScoreThreshold` al *prefetch denso* (coseno crudo,
antes de fusionar), documentado en
`src/RagEngine.Core/Domain/RetrievalScoreScale.cs` (item 4.9). Sin rerank, el
score que devuelve la búsqueda es RRF (`RankFusionNative`/`RankFusionWeighted`):
"función del ranking, no de la similitud" y **no comparable entre consultas**
(`IsComparableAcrossQueries == false`). Aplicar un umbral absoluto ahí no mide
nada real — confirma con datos lo que ese archivo ya advertía.

Por eso el informe se corrió con `--rerank`, donde el score final es
`CrossEncoderStable`/`CrossEncoderBatched`: sigmoide en [0..1], comparable entre
consultas, y es la escala en la que el guardrail de banda baja (item 4.3) ya
opera con un "piso de ruido" ~0.08 — la misma noción que origina esta ficha.
Con rerank apagado (el default real de producción hoy), ningún umbral absoluto
sobre el score devuelto es coherente; esto en sí mismo es hallazgo del item, no
una limitación del método.

## Método

- 4 eval-sets servidos, un solo retrieval real por pregunta (`--top-k 10
  --min-score 0 --rerank --dump-hits --json`), sin post-filtrar en la
  consulta — todas las políticas se simulan sobre la misma lista cruda de 10
  resultados por pregunta.
- Políticas comparadas @10, sobre preguntas con ground truth (ancladas):
  1. **top_k fijo** (actual sin filtro de score): los 10 tal cual.
  2. **min_score global actual** (0.10): quita hits con score < 0.10.
  3. **corte adaptativo candidato**, dos familias:
     - *relativo al mejor*: conserva score ≥ α·máximo (α ∈ {0.3, 0.4, 0.5}).
     - *hueco*: corta en el primer salto > δ respecto al máximo visto hasta ahí,
       recorriendo en el orden final de rank (no se reordena por score — el
       invariante 4.2 dice que la posición #0 no siempre es la de mayor score).
- "Baja calidad omitida" = hits con score < 0.08 que la política excluyó.

Scripts y JSON crudos en `docs/eval/quality/7.d/` (`raw-rerank-*.json` = datos
por pregunta; `informe-corte-adaptativo-rerank.json` = agregados).

## Resultados (recall@10 hit-any, sobre preguntas ancladas)

| Set | N | top_k fijo | min_score 0.10 | mejor adaptativo | ganancias vs fijo (todas las políticas) |
|---|---|---|---|---|---|
| bsuite-auditorias | 16 | 68.8% | 56.2% (−2) | 62.5% (relativo/hueco, −1) | **0** |
| bsuite-repo | 76 | 55.3% | 50.0% (−4) | 50.0% (relativo 0.3, −4) | **0** |
| innovapp-docs | 22 | 54.5% | 54.5% (±0) | 54.5% (relativo 0.3, ±0) | **0** |
| tickets-microservice | 19 | 31.6% | 31.6% (±0) | 31.6% (relativo 0.3/0.4, ±0) | **0** |

En **ninguno de los 4 sets, en ninguna de las 8 variantes de corte probadas
(1 min_score + 3 relativas + 3 de hueco), una sola pregunta gana** al pasar de
top_k fijo a una política de corte por score. El único movimiento observado es
pérdida (0 a 13 preguntas según el set/umbral) o empate.

## Ruido omitido (hits con score < 0.08 no devueltos)

| Set | min_score 0.10 | mejor adaptativo (mismo recall) |
|---|---|---|
| bsuite-auditorias | 43 | 35 (relativo 0.3, pero con 1 pérdida) |
| bsuite-repo | 331 | 280 (relativo 0.3, con 4 pérdidas — igual que min_score) |
| innovapp-docs | 59 | 54 (relativo 0.3, 0 pérdidas) |
| tickets-microservice | 65 | 54 (relativo 0.3, 0 pérdidas) |

Donde el corte adaptativo (relativo 0.3) empata en recall con min_score o con
top_k fijo, omite un poco más de ruido de baja calidad que min_score en 3 de 4
sets (innovapp-docs, tickets-microservice, bsuite-auditorias); en bsuite-repo
omite menos. Las variantes "de hueco" (gap-based) fueron sistemáticamente peores
en el trade-off: pierden más recall por cada resultado de ruido que quitan que
las variantes relativas o que min_score.

## Conclusión

Sobre estos 4 eval-sets, con rerank activo (la única escala donde un umbral
absoluto es coherente), **no hay evidencia de que un corte adaptativo (por hueco
o relativo al mejor score) reduzca el ruido mejor que un top_k fijo con
min_score global**: ninguna política de corte por score gana una sola pregunta
frente a devolver los 10 resultados tal cual, y como mucho iguala el recall del
min_score actual mientras omite un poco más o un poco menos de ruido según el
set. Es una hipótesis nula: la variable que domina el resultado es filtrar o no
filtrar por score, no *cómo* de sofisticado es el umbral.

Además, sin rerank (el camino por defecto de producción hoy), la pregunta ni
siquiera es coherente sobre la escala actual del score devuelto (RRF, no
comparable entre consultas) — cualquier implementación de corte por score
(fijo o adaptativo) tendría que operar sobre un score calibrado (cross-encoder),
no sobre el RRF crudo.

**No se cambia el comportamiento por defecto de producción.** Si 7.a
(perfil por colección) decide incorporar algún corte, este informe recomienda
no priorizar el adaptativo sobre un min_score simple: no mostró ventaja medible
en ningún set, y añade una superficie de configuración (α o δ) sin beneficio
observado.

## Limitaciones

- 4 eval-sets, 16-76 preguntas ancladas cada uno — señal débil para diferencias
  pequeñas (1-2 preguntas) dado que varias respuestas ya son marginales
  (score 0.06-0.08, el mismo rango que origina la ficha).
- Solo se probó rerank con cross-encoder; no se investigó si el guardrail de
  banda baja (item 4.3, que ya usa `CrossEncoderStable` con umbrales
  calibrados) haría redundante cualquier corte adicional a nivel de lista.
- α y δ se muestrearon en rejillas pequeñas (3 valores cada una); no se buscó
  el óptimo por grid search exhaustivo — el patrón (ninguna ganancia) fue
  consistente en todo el rango probado, lo que sugiere que ampliar la rejilla
  no cambiaría la conclusión, pero no se verificó exhaustivamente.
