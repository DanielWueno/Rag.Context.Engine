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

## Control post-5.h y pre-símbolos, y ratificación de 11.2 (2026-09-14)

Reconciliación del ítem `11.4-rebaseline-olas-5-y-6`, desbloqueado porque
`11.2-experimento-3-brazos` cerró `hecho` el 2026-09-14 con resultado **nulo**
(ningún brazo A/B/C alcanzó ≥2 aciertos netos en denso; ver su `resultado` en el
ledger). Por la `_condicion_de_reentrada` de 11.4: 11.2 nulo → se ratifica el
control existente **sin fabricar un cierre de** `11.3-presupuesto-en-tokens-reales`
(sigue bloqueado, no se le atribuye ningún cambio de código ni de configuración).

**Reproducibilidad verificada de nuevo:** dos corridas de
`rag eval --eval-set docs/eval/bsuite-repo.eval-set.json -c bsuite-repo -k 10 --json`
sobre la colección `bsuite-repo` en vivo (22.592 puntos) dieron `hit_any_at_k`,
`hit_full_at_k` y `top_score` **idénticos en las 55 preguntas** (0 diffs) y
recall@10 idéntico a la línea base de esta tabla: total 19/47, `simbolo` 2/12.
Sigue sin ruido de corrida a corrida.

**Aviso de procedencia — la versión de contrato NO prueba re-ingesta:** las dos
corridas de hoy reportan `chunking_contract_version: 3` (la constante de código
tras el fix de 5.g) e `index_short_type_declarations: false`, mientras que la
línea base archivada arriba quedó grabada con `chunking_contract_version: 2` y
sin ese campo. Que el número suba **no significa que `bsuite-repo` fue
reingestado**: `5.e-salto-de-contrato-y-rebaseline` sigue `bloqueado` (bloqueado
por `5.h`, que a su vez sigue `bloqueado`, sin promover) y los recall idénticos
de esta corrida contra la línea base histórica lo confirman empíricamente — si
el índice llevara los símbolos/fixes de las olas 5.g/5.h, `simbolo` no seguiría
en 2/12. **Regla para cualquier A/B futuro:** no aceptar `chunking_contract_version`
del `provenance` como evidencia de que una colección fue reconstruida; cruzar
siempre contra el estado real de `5.e` en el ledger antes de comparar.

**Control declarado para Ola 5 y Ola 6:** hasta que `5.e` cierre `hecho`, el
control post-5.h/pre-símbolos vigente **es esta misma línea base** (recall@10
total 19/47, `simbolo` 2/12), no una nueva medición — no hay nada que
regenerar porque el índice servido no cambió. El día que `5.e` reingeste, el
próximo control debe grabar su propio `provenance` completo (incluyendo
`index_short_type_declarations` y el hash de índice) antes de comparar Ola 5.

**Truncamiento (11.2) queda separado de este control:** las seis colecciones de
`11.2-experimento-3-brazos` fueron efímeras y ya se eliminaron (ver su
`resultado`); ninguna se promovió ni se usa como control de Ola 5/6. El
resultado nulo de 11.2 no habilita ni bloquea nada de esta tabla más allá de no
perseguir 11.3.

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
  (de 2 a 5, +3 preguntas), medido **contra el control post-5.h/pre-símbolos fijado
  arriba** (2/12), no contra un control que ya incluya la excepción de 5.h. Tres
  preguntas están fuera del margen de ±1 con el que se juzga todo lo demás, así que
  no se puede confundir con re-etiquetado ni con empates de orden en Qdrant.
  Si para entonces `5.h` ya se promovió (declaraciones cortas indexadas), su propia
  ganancia de cobertura (p. ej. `ICombProvider`/`IEntidadReport` recuperables) no
  cuenta como parte de estas +3: hay que aislar en el reporte por pregunta cuáles
  aciertos vienen del filtro de longitud y cuáles de `defined_symbols`/
  `consumed_symbols`, para no atribuirle a la ola equivocada un acierto que ya
  resolvía la otra.

Si sale no-regresión sin mejora, la ola se queda como deuda: el contrato estructural
está publicado pero no se está usando, y hay que decirlo en el `resultado` del ítem en
vez de darlo por bueno.

**Ola 6 — se acepta si y sólo si se cumplen las dos:**

- **No-regresión:** recall@10 total ≥ el mejor valor alcanzado al cerrar la Ola 5, menos
  una pregunta.
- **Mejora en preguntas de salto (umbral fijado por 11.4, 2026-09-14):** sobre el
  eval-set de salto que produzca el ítem `6.c-preguntas-de-salto` —que aún no
  existe—, fijar primero su `n` respondible y calcular
  **`umbral = max(4, ceil(0.25 * n))`** preguntas netas de ganancia (aciertos nuevos
  menos pérdidas) en recall@10 sobre su propia línea base sin expansión por
  símbolo, ANTES de ejecutar 6.a. El umbral vive en preguntas netas, no en puntos
  porcentuales, para no depender de qué tan grande resulte el eval-set de salto y
  para poder aplicar la misma regla de ±1 pregunta de no-regresión que el resto de
  esta tabla. Ejemplos: `n=12` → umbral 4; `n=20` → umbral 5; `n=40` → umbral 10.
  El ítem 6.c fija el `n` real y hereda este umbral tal cual; no debe re-derivar
  ni renegociar la fórmula a posteriori con el resultado ya visto.

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
