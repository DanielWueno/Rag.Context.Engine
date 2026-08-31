# Umbral de decisión de las olas 5 y 6 — bsuite-repo

Este documento fija, **antes** de tocar el retriever, con qué número se acepta o se
rechaza el trabajo de la Ola 5 (contrato estructural de símbolos) y la Ola 6 (two-hop
en tiempo de consulta). Se escribe junto con el instrumento y no después de ver los
resultados: un umbral elegido a posteriori no decide nada.

## El instrumento

- **Ground truth:** `docs/eval/bsuite-repo.eval-set.json` — 55 preguntas, 47 con
  chunk-ancla y 8 negativos adversariales sin ancla.
- **Línea base:** `docs/eval/baselines/bsuite-repo.norerank.baseline.json`, sin rerank,
  `k=10`, `min_score=0.10`, con el bloque `provenance` completo.
- **Colección:** `bsuite-repo` (22.592 puntos, 20.475 chunks de C#, 1.773 de JavaScript,
  146 secciones de Markdown, 198 de texto plano).

Cada ancla se verificó dos veces: que el literal está en el archivo fuente **y** que
está dentro del `content` de un chunk realmente indexado. Un ancla que sólo existe en
el archivo pero no sobrevive al chunker mide ruido, no recall. Los 8 negativos se
verificaron contra el contenido indexado, no contra el repositorio.

## Línea base medida (2026-08-31, commit fb34424, `chunking_contract_version` 2)

| Categoría | n | recall@1 | recall@3 | recall@5 | recall@10 |
|---|---|---|---|---|---|
| `literal` | 12 | 3 (25%) | 5 (42%) | 7 (58%) | **7 (58%)** |
| `parafraseada` | 18 | 4 (22%) | 7 (39%) | 7 (39%) | **8 (44%)** |
| `simbolo` | 12 | 1 (8%) | 1 (8%) | 1 (8%) | **2 (17%)** |
| `ambigua` | 5 | 1 (20%) | 2 (40%) | 2 (40%) | **2 (40%)** |
| **TOTAL** | **47** | 9 (19%) | 15 (32%) | 17 (36%) | **19 (40%)** |

Los 8 negativos no tienen ground truth de recall; su `top_score` quedó entre 0.041 y
0.059, solapado con el de varias preguntas que sí aciertan. **Este eval-set no sirve
para calibrar el gate de confianza** — para eso está `gate-bandas.labeled-set.json`.

**Reproducibilidad:** dos corridas consecutivas del mismo commit dieron `hit_any_at_k`,
`hit_full_at_k` y `top_score` idénticos en las 55 preguntas. En esta colección el ruido
de corrida a corrida es cero, así que cualquier diferencia posterior es del motor.

## Por qué `simbolo` es la categoría que decide

Las 12 preguntas de `simbolo` nombran un símbolo concreto de C# —una firma de método,
una interfaz, una declaración de clase, una propiedad— y hoy aciertan 2 de 12 dentro de
top-10. Es exactamente lo que la Ola 5 declara que va a arreglar al publicar
`defined_symbols` y `consumed_symbols` en el payload y en el encabezado embebido. Si esa
ola no mueve esta categoría, no hizo lo que dice hacer, por más que el total no empeore.

Dos hallazgos del etiquetado que acotan lo que la ola tiene que arreglar, ambos
verificados contra el índice:

1. **El chunker normaliza la declaración de tipo.** `public class X : Base` se indexa
   como `public class X: Base` (sin el espacio antes de los dos puntos). Una búsqueda o
   un ancla que use la forma del archivo no coincide con lo indexado.
2. **La declaración del tipo de nivel superior puede no estar indexada.** En
   `IProvider.cs` no existe ningún chunk `Class` para `ICombProvider`: sólo se indexan
   sus miembros. Lo mismo pasa con `IEntidadReport`. Quien pregunte por la interfaz por
   su nombre no tiene contra qué acertar.

## Umbrales

Se miden siempre con `rag eval --eval-set docs/eval/bsuite-repo.eval-set.json -c bsuite-repo -k 10`
y comparando contra el baseline con `--baseline`. Si el reporte de comparabilidad dice
**INCOMPARABLE**, el número no cuenta: hay que regenerar la línea base primero.

**Ola 5 — se acepta si y sólo si se cumplen las dos:**

- **No-regresión:** recall@10 total ≥ **18 de 47** (la línea base menos una pregunta).
  Es el margen de la ola, que ya lo declara así, y aquí la tolerancia es de etiquetado,
  no de ruido: el ruido medido es cero. La misma regla aplica a `bsuite-auditorias`,
  `innovapp-docs` y `tickets-microservice`, cada uno contra su propio baseline.
- **Mejora en lo que la ola dice arreglar:** `simbolo` recall@10 ≥ **5 de 12**
  (de 2 a 5, +3 preguntas). Tres preguntas están fuera del margen de ±1 con el que se
  juzga todo lo demás, así que no se puede confundir con re-etiquetado ni con empates de
  orden en Qdrant.

Si sale no-regresión sin mejora, la ola se queda como deuda: el contrato estructural
está publicado pero no se está usando, y hay que decirlo en el `resultado` del ítem en
vez de darlo por bueno.

**Ola 6 — se acepta si y sólo si se cumplen las dos:**

- **No-regresión:** recall@10 total ≥ el mejor valor alcanzado al cerrar la Ola 5, menos
  una pregunta.
- **Mejora en preguntas de salto:** sobre el eval-set de salto que produzca el ítem
  `6.c-preguntas-de-salto` —que aún no existe— recall@10 ≥ **+25 puntos** sobre su propia
  línea base sin expansión por símbolo. El umbral se fija en puntos y no en preguntas
  porque el tamaño de ese conjunto todavía no está decidido; el ítem 6.c debe convertirlo
  a un conteo absoluto en cuanto se sepa su `n`, con la misma regla de ±1 pregunta.

El segundo salto además tiene presupuesto: el ítem `6.b-presupuesto-del-segundo-salto`
lo acota. Una mejora de recall comprada con una latencia que nadie declaró no es una
mejora aceptada.

## Qué invalida esta línea base

Cualquier diferencia en los campos de comparabilidad de `provenance`: `eval_set_hash`,
modelo de embeddings y sus dimensiones, `max_sequence_length`, modelo de cross-encoder,
los tres pesos de la fusión RRF, `rrf_k`, `chunking_contract_version` y
`resumen_prompt_version`. El commit y el estado del árbol se registran como contexto y
no invalidan por sí solos — ver `docs/eval/README.md`.

En particular, la Ola 5 **va a** cambiar `chunking_contract_version` al añadir símbolos
al encabezado embebido. Eso hace incomparable esta línea base por diseño: el orden es
re-ingestar, regenerar el baseline con el mismo eval-set (`eval_set_hash` no cambia) y
recién entonces comparar recall contra el número de esta tabla. Comparar antes de
re-ingestar sólo mide una colección a medio migrar.

## Regenerar

```bash
dotnet run --project src/RagEngine.Cli --configuration Release -- eval \
  --eval-set docs/eval/bsuite-repo.eval-set.json -c bsuite-repo -k 10 \
  --json > docs/eval/baselines/bsuite-repo.norerank.baseline.json
```
