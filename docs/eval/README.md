# Eval-sets y baselines

## Qué hay aquí

- `*.eval-set.json` — ground-truth: pregunta, categoría, archivo esperado, sección y
  fragmentos que la respuesta correcta debe contener.
- `baselines/*.baseline.json` — resultado de correr un eval-set, **con la procedencia
  de la corrida**.
- `gate-bandas.labeled-set.json` — conjunto para calibrar las bandas del gate de
  confianza: 65 consultas etiquetadas `presente` / `ausente` según si el corpus contiene
  la respuesta, con negativos adversariales (adyacentes al dominio, cross-corpus y meta,
  no sólo fuera de dominio). Cada ausencia lleva en `evidencia` el `grep` que la
  confirma contra el contenido indexado. Es ground-truth de **abstención**, no de
  recall: aquí no importa qué chunk sale, sino si el motor debe responder.
- `gate-bandas.scores.json` — score del resultado #1 de cada una de esas consultas, con
  `CrossEncoder:StableGateScore` encendida y apagada. Lo produce y lo analiza
  `infra/gate-bandas-barrido.py`.
- `quality/*.json` — corridas de `infra/quality-baseline.py`: respuestas completas de la
  API más métricas mecánicas de calidad. No miden recall; miden lo que ve el usuario.

## El problema que resuelve la procedencia

Antes existía un solo baseline y guardaba colección, `top_k`, `rerank` y `min_score`.
No guardaba el commit, ni el modelo de embeddings, ni los pesos RRF, ni la versión del
chunker. Los pesos se recalibraron después de generarlo (sparse 1.0→1.3, resumen
1.0→2.5, +16 puntos de recall@10), así que ese archivo dejó de ser comparable con
cualquier corrida nueva — y no había forma de saberlo mirándolo.

Ahora cada baseline lleva un bloque `provenance` con los 12 campos que determinan si
dos números son comparables:

| Campo | Por qué invalida la comparación |
|---|---|
| `git_commit`, `git_dirty` | Un baseline generado con el árbol sucio no es reproducible, y se dice. |
| `eval_set_hash` | Detecta el re-etiquetado: corregir el ground-truth cambia el recall sin que cambie el motor. |
| `embedding_model`, `embedding_dimensions`, `embedding_max_sequence_length` | Otro modelo son otros vectores; exige re-ingesta. |
| `cross_encoder_model` | El rerank cambia el orden final. |
| `weight_codigo`, `weight_sparse`, `weight_resumen`, `rrf_k` | Los pesos de la fusión RRF a 3 bandas mueven el recall varios puntos. |
| `chunking_contract_version` | Si los chunks del índice se generaron con otro criterio de corte, nada es comparable. |
| `resumen_prompt_version` | El tercer vector de la fusión se genera con ese prompt; cambiarlo cambia lo indexado. |

## Regenerar un baseline

```bash
dotnet run --project src/RagEngine.Cli -- eval \
  --eval-set docs/eval/innovapp-docs.eval-set.json -c innovapp-docs -k 10 --rerank \
  --json > docs/eval/baselines/innovapp-docs.rerank.baseline.json
```

Hazlo con el árbol limpio, o el baseline saldrá con `git_dirty: true` y no será
reproducible.

## Comparar contra un baseline

```bash
dotnet run --project src/RagEngine.Cli -- eval \
  --eval-set docs/eval/innovapp-docs.eval-set.json -c innovapp-docs -k 10 --rerank \
  --baseline docs/eval/baselines/innovapp-docs.rerank.baseline.json
```

Al final del reporte dice si es comparable. Si no lo es, tabula exactamente qué campos
cambiaron, para que nadie atribuya al retrieval una diferencia que viene de la
configuración. Un baseline anterior a este formato se reporta como
**«sin procedencia — incomparable»** en vez de compararse a ciegas.

## Reproducibilidad: qué es estable y qué no

Medido el 2026-08-21 con dos corridas consecutivas del mismo commit sobre
`innovapp-docs` (26 preguntas):

- **El recall es reproducible.** `hit_any_at_k` y `hit_full_at_k` salieron
  byte-idénticos en las 26.
- **`top_score` no lo es del todo.** Varió en 2 de 26 (0.0157 vs 0.0183 y 0.6805 vs
  0.6849). Las dos son preguntas **sin respuesta correcta en el corpus** — una marcada
  como regresión conocida y otra fuera de dominio— donde los candidatos del top están
  efectivamente empatados y el orden entre empates de Qdrant no es determinista
  (qdrant/qdrant#6814).

Consecuencia práctica: compara recall, no `top_score`. El campo se conserva porque es
útil para diagnosticar, pero un cambio suyo en una pregunta sin ground-truth no
significa nada.

## Estado al 2026-08-21

| Baseline | Preguntas | recall@1 | recall@5 | recall@10 |
|---|---|---|---|---|
| `bsuite-auditorias.norerank` | 16 | 25% | 69% | 69% |
| `innovapp-docs.norerank` | 22 | 9% | 18% | 36% |
| `innovapp-docs.rerank` | 22 | 18% | 55% | 55% |
| `tickets-microservice.norerank` | 19 | 5% | 16% | 16% |

Los de `innovapp-docs` y `bsuite-auditorias` coinciden con las corridas históricas
registradas en `docs/analisis-futuro/`, lo que da confianza en el runner.
`tickets-microservice` es su primer baseline.
