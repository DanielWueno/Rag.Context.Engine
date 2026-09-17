# Eval-sets y baselines

## Qué hay aquí

- `*.eval-set.json` — ground-truth: pregunta, categoría, archivo esperado, sección y
  fragmentos que la respuesta correcta debe contener.
- `baselines/*.baseline.json` — resultado de correr un eval-set, **con la procedencia
  de la corrida**.
- `bsuite-repo.umbral-de-decision.md` — el umbral con el que se acepta o se rechaza el
  trabajo de las olas 5 y 6, fijado antes de tocar el retriever, junto con los dos
  hallazgos del chunker que salieron al etiquetar.
- `gate-bandas.labeled-set.json` — conjunto para calibrar las bandas del gate de
  confianza: 65 consultas etiquetadas `presente` / `ausente` según si el corpus contiene
  la respuesta, con negativos adversariales (adyacentes al dominio, cross-corpus y meta,
  no sólo fuera de dominio). Cada ausencia lleva en `evidencia` el `grep` que la
  confirma contra el contenido indexado. Es ground-truth de **abstención**, no de
  recall: aquí no importa qué chunk sale, sino si el motor debe responder.
- `gate-bandas.scores.json` — score del resultado #1 de cada una de esas consultas, con
  `CrossEncoder:StableGateScore` encendida y apagada. Es el control histórico;
  `infra/gate-bandas-barrido.py` lo reanaliza sin sobrescribirlo. Las mediciones nuevas
  exigen `--medir --salida NUEVA.json` y guardan identidad efectiva y hash del conjunto.
- `quality/*.json` — corridas de `infra/quality-baseline.py`: respuestas completas de la
  API más métricas mecánicas de calidad. No miden recall; miden lo que ve el usuario.
- `quality/5.h-filtro-*.json` — excepción al punto anterior: A/B de recall del filtro
  de declaraciones cortas en dos bandas. El archivo `5.h-filtro-protocolo.json`
  registra cobertura, procedencia y límites; no reemplaza los baselines de tres
  bandas ni autoriza activar el experimento por defecto.
- `quality/11.2/` — experimento de ventanas del encoder 256/128/512, dos ingestas
  independientes por brazo, denso-solo y fusión nativa de dos bandas por separado.
  `protocolo.json` y su SHA256 se congelan antes de medir; `report.json` conserva
  pérdidas individuales, símbolos, estratos de truncamiento y latencias.
  `*.baseline.json` son salidas del `EvalCommand` real; `*.trace.jsonl` distinguen
  la escala del score y guardan los resultados recuperados. El host aislado en
  `infra/experimento-11-2/` reutiliza los comandos y la instrumentación de 11.1,
  sin modificar producción. **B limita la ventana a 128, no cambia los cortes**;
  este ensayo no demuestra una estrategia nueva de segmentación sin truncamiento.
  No incluye resúmenes ni sustituye el baseline histórico de tres bandas.
- `quality/4.4-antes-media.json` y `quality/4.4-despues-todas.json` — respuestas del CLI
  antes y después de reescribir la rama (b) de `LowConfidencePrompt.Addendum`, con las
  consultas seleccionadas por la banda que les da su score en `gate-bandas.scores.json`.
  Las produce y las puntúa `infra/fuga-banda-media-barrido.py`, que sale con código 1 si
  detecta la frase fugada o su forma general (relevar un enunciado sobre el contexto en
  vez de contenido).

## El problema que resuelve la procedencia

Antes existía un solo baseline y guardaba colección, `top_k`, `rerank` y `min_score`.
No guardaba el commit, ni el modelo de embeddings, ni los pesos RRF, ni la versión del
chunker. Los pesos se recalibraron después de generarlo (sparse 1.0→1.3, resumen
1.0→2.5, +16 puntos de recall@10), así que ese archivo dejó de ser comparable con
cualquier corrida nueva — y no había forma de saberlo mirándolo.

Ahora cada baseline lleva un bloque `provenance` con los campos que determinan si
dos números son comparables:

| Campo | Por qué invalida la comparación |
|---|---|
| `git_commit`, `git_dirty` | Un baseline generado con el árbol sucio no es reproducible, y se dice. |
| `eval_set_hash` | Detecta el re-etiquetado: corregir el ground-truth cambia el recall sin que cambie el motor. |
| `embedding_model`, `embedding_dimensions`, `embedding_max_sequence_length` | Otro modelo son otros vectores; exige re-ingesta. |
| `cross_encoder_model` | El rerank cambia el orden final. |
| `cross_encoder` | Identidad de los bytes cargados: SHA-256 de ONNX/tokenizer, binario, arquitectura, StableGateScore, ventana y lote. Un baseline antiguo con sólo nombre no declara esta evidencia. |
| `gate_calibration` | Perfil efectivo de umbrales vinculado a esa identidad, o null para el gate global sin vínculo. No acredita calidad de respuesta ni recalibración. |
| `weight_codigo`, `weight_sparse`, `weight_resumen`, `rrf_k` | Los pesos de la fusión RRF a 3 bandas mueven el recall varios puntos. |
| `chunking_contract_version` | Si los chunks del índice se generaron con otro criterio de corte, nada es comparable. |
| `index_short_type_declarations` | Declara el experimento de admisión de tipos cortos; un baseline antiguo sin este campo lo reporta como no declarado. |
| `resumen_prompt_version` | El tercer vector de la fusión se genera con ese prompt; cambiarlo cambia lo indexado. |

`rag eval` registra además `cross_encoder`, `gate_calibration` y `score_scale` por
pregunta. Obtiene la identidad del resultado, no releyendo un archivo que podría
haber cambiado después de cargar ONNX; una corrida con identidades/perfiles
mezclados falla. Sin resultados rerankeados no inventa una identidad observada.
El contrato y el comparador de respuestas están documentados en
[calidad](quality/README.md#calibración-por-binario-ítem-1210).

## Verificar que un eval-set siga siendo medible

```bash
python3 infra/verificar-anclas-eval.py \
  --eval-set docs/eval/bsuite-repo.eval-set.json \
  --collection bsuite-repo \
  --repo ~/Documents/Projects/BusinessSuite.Xaf
```

Comprueba, ancla por ancla, que el literal está en el archivo fuente, que el archivo
está indexado y que algún chunk indexado lo contiene con la misma comparación que hace
`rag eval`. Un ancla que sólo existe en el archivo pero no sobrevive al chunker hace que
la pregunta falle siempre sin que nada lo denuncie. Córrelo después de cada re-ingesta
que mueva `chunking_contract_version`.

El contrato 3 (2026-09-12) permite conservar declaraciones cortas de tipos durante
la ingesta con `Ingestion:IndexShortTypeDeclarations=true` (experimental, desactivado
por defecto) e incluye la normalización de `Nombre : Base` de 5.g. El ancla de
`AlmacenComputoEmpleado` se corrigió para usar el sufijo desde `:` con su base e
interfaces: existe literalmente en la fuente y en el nuevo índice, sin depender
del espacio anterior a `:`. Esto cambia `eval_set_hash`; los baselines históricos
se conservan y requieren regeneración en 5.e, no una comparación directa.
Al evaluar, usa el mismo valor de `Ingestion:IndexShortTypeDeclarations` que durante
la ingesta: la procedencia registra la configuración declarada del proceso; todavía
no la recupera del manifiesto de la colección (5.f).

## Regenerar un baseline

```bash
dotnet run --project src/RagEngine.Cli -- eval \
  --eval-set docs/eval/innovapp-docs.eval-set.json -c innovapp-docs -k 10 --rerank \
  --json > docs/eval/baselines/innovapp-docs.rerank.baseline.json
```

Hazlo con el árbol limpio, o el baseline saldrá con `git_dirty: true` y no será
reproducible.

### Verificar la evidencia del experimento 11.2

```bash
python3 infra/experimento-11-2/verify.py
```

Comprueba las seis ingestas (o la exclusión técnica documentada de C), cobertura
de las 55 preguntas y sus anclas, procedencia, estadísticas reales de tokens y
rollback de las diez colecciones servidas mediante hashes de todos sus puntos,
payloads y vectores. Falla si falta evidencia. Un resultado nulo de calidad **no**
es un error del verificador: publica la decisión sin activar 11.3.
El umbral congelado exige que ambas réplicas de un candidato pasen frente a sus
controles; informa pérdidas brutas individuales, sin ocultarlas con ganancias.

`run.py prepare` congeló fuentes oficiales del modelo, corpus y protocolo;
`run.py run` ejecutó los comandos secuenciales registrados en `*.command.json`
y su limpieza en `finally`. No sobrescriben una corrida existente. Los overrides
son variables de entorno de cada subproceso; las cachés SQLite son aisladas y se
retiran tras la prueba. No ejecutar otra ingesta sobre esas rutas de evidencia
ni sobre colecciones servidas para «refrescar» este resultado histórico.

## Comparar contra un baseline

Para automatización usa el [gate nocturno local](../operaciones.md#eval-nocturno-local-ítem-141):
`rag eval --baseline` informa, pero **no devuelve un fallo** por regresión o
incomparabilidad. `infra/eval-nocturno/runner.py` sí distingue esos estados de
una dependencia ausente, conserva IDs/categorías de cada pérdida y no promueve
baselines automáticamente.

El inventario congelado está en `infra/eval-nocturno/inventory.json`: cinco
perfiles sobre cuatro datasets. Mantiene las referencias históricas de 11.4
salvo `bsuite-repo`, que selecciona la referencia ya existente de 6.c con
76 respondibles y 8 negativos. Los baselines antiguos no tienen toda la
procedencia exigida hoy: producen **incomparable**, nunca un verde inferido.
Los resultados nuevos incluyen `nightly_context` con hashes completos de los
modelos, configuración e índice (payloads y vectores, incluido el manifiesto).
Son evidencia candidata, **no nuevas referencias aceptadas**.

El ID de pregunta del gate es el prefijo de 16 caracteres del SHA-256 UTF-8 de
`Question`. Su correspondencia se reconstruye con el dataset de hash congelado;
no depende del orden de filas. Los negativos se comprueban como parte de la
cobertura, pero no entran en recall ni prueban abstención/calidad de respuesta.

```bash
# Comparador manual sin servicios: salida 0/1/2 = ok/regresión/incomparable.
python3 infra/eval-nocturno/runner.py compare \
  --dataset docs/eval/bsuite-repo.eval-set.json \
  --baseline /ruta/referencia.result.json \
  --candidate /ruta/candidato.result.json

# Aceptación de 14.1: controles adversariales + los cinco evals REALES.
# Falla si falta infraestructura, cobertura o la evidencia versionada.
python3 infra/eval-nocturno/accept.py
```

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

## Estado al 2026-08-31

| Baseline | Preguntas | recall@1 | recall@5 | recall@10 |
|---|---|---|---|---|
| `bsuite-auditorias.norerank` | 16 | 25% | 69% | 69% |
| `innovapp-docs.norerank` | 22 | 9% | 18% | 36% |
| `innovapp-docs.rerank` | 22 | 18% | 55% | 55% |
| `tickets-microservice.norerank` | 19 | 5% | 16% | 16% |
| `bsuite-repo.norerank` | 47 | 19% | 36% | 40% |

Los de `innovapp-docs` y `bsuite-auditorias` coinciden con las corridas históricas
registradas en `docs/analisis-futuro/`, lo que da confianza en el runner.
`tickets-microservice` y `bsuite-repo` son sus primeros baselines.

`bsuite-repo` lleva además 8 negativos adversariales sin ancla, que no entran en el
recall. El desglose por categoría y los umbrales de decisión están en
[bsuite-repo.umbral-de-decision.md](bsuite-repo.umbral-de-decision.md).

### Sobre el `git_dirty: true` de `bsuite-repo.norerank`

Se generó en la misma sesión en que nació su eval-set, así que en el momento de la
corrida el árbol tenía sin commitear exactamente los archivos que entran en el commit
que la contiene. No hay forma de que fuera de otra manera: un baseline no puede
preceder a su propio ground-truth. Lo que fija la comparabilidad es `eval_set_hash`, y
ese sí está pinneado. Cualquier regeneración posterior con el árbol limpio da los mismos
números —se verificó corriendo el eval dos veces, con resultado idéntico en las 55
preguntas— y saldrá con `git_dirty: false`.
