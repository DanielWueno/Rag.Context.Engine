# Línea base de calidad de respuesta

`rag eval` mide **recall**: si el chunk correcto aparece en el top-K. Eso no dice
nada de lo que el usuario percibe. Estas tres cosas se venían juzgando a ojo:

1. ¿Responde cosas que no están en el corpus?
2. ¿Explica, o es tan corta que no dice nada?
3. ¿Sigue siendo técnica cuando se pidió lenguaje simple?

`infra/quality-baseline.py` re-corre el bundle de preguntas **reales**
(`replicate-env/data/questions/`, 427 registros → 143 únicas) contra la API y
guarda las respuestas completas junto a métricas mecánicas. La generación es la
parte cara (~8-11 s por pregunta); guardar las respuestas permite volver a
puntuarlas —con otra heurística, con un juez LLM o a mano— sin pagar otra corrida.

## Las tres posturas

La métrica central no es binaria. El motor casi nunca fabrica; lo que hace es
avisar que no encontró coincidencia clara y **entregar contenido igual**. Eso se
lee como responder fuera del corpus, así que se cuenta aparte:

| Postura | Qué significa |
|---|---|
| `rechazo_pleno` | Declina y no entrega contenido del corpus. |
| `banda_baja` | Avisa que la coincidencia es débil y aun así relata contenido. |
| `directo` | Responde sin reservas. |

`directo_con_score_bajo_pct` es el proxy más cercano a "responde fuera del
corpus": ni rechazo, ni aviso, con evidencia débil.

## Cómo comparar dos configuraciones

Se levantan dos instancias del **mismo binario** cambiando solo la bandera, y se
corre el arnés contra cada puerto:

```bash
ASPNETCORE_URLS=http://localhost:5083 \
  RagGeneration__EnableSimpleModeResumenContext=true \
  dotnet run --project src/RagEngine.Api &

ASPNETCORE_URLS=http://localhost:5081 \
  RagGeneration__EnableSimpleModeResumenContext=false \
  dotnet run --project src/RagEngine.Api &

python3 infra/quality-baseline.py --base-url http://localhost:5083 \
  --collection innovapp-docs --limit 20 --force-mode Simple --etiqueta ab-ON
```

Antes de confiar en un A/B, **verifica que la bandera se aplicó**. Una variable
mal nombrada produce un falso "no hay diferencia". La forma barata de comprobar
el mecanismo: arrancar con `RagGeneration__EnableSimpleModeSanitizer=false`, que
imprime un aviso al arranque. Si ese aviso sale, la convención
`RagGeneration__<Propiedad>` funciona.

## Límites conocidos

Las métricas son heurísticas, no un juez. Los contadores de léxico optimista y
vago señalan candidatos para revisión manual, no veredictos. La detección de
postura es por frases: al construirla, "que hora es" quedó mal clasificada como
`directo` porque el rechazo real —"no tengo la capacidad de proporcionar la hora
actual"— no estaba en la lista. Fue un falso negativo de la métrica, no un fallo
del motor. Si aparece un patrón de rechazo nuevo, hay que agregarlo.


## Línea base al 2026-08-21 (antes de la re-ingesta)

`linea-base-post-ajustes.json` — 40 preguntas reales en modo Simple (20 de
`innovapp-docs`, 20 de `bsuite-repo`), con el índice **anterior** a la re-ingesta y
los prompts ya ajustados. Es el archivo contra el que hay que comparar después.

| Métrica | Antes de los ajustes | Línea base actual |
|---|---|---|
| Rechazo pleno | 15,0% | **30,0%** |
| Banda baja | 40,0% | 32,5% |
| Directo con score bajo | 22,5% | **15,0%** |
| Palabras (mediana) | 63 | 54,5 |
| Respuestas cortas (<40 palabras) | 12,5% | 35,0% |
| Simple con tecnicismos | 7,5% | 7,5% |

El salto en "respuestas cortas" es **deseado**: son los rechazos de 28-52 palabras
que sustituyeron a fabricaciones de 222-349. No confundir brevedad con vaguedad
sin leer las respuestas.

Con n=40, un delta porcentual aislado cae dentro del ruido (error estándar ≈7 pp).
Lo que sostiene estos cambios es el comportamiento **caso por caso**, verificable
en el detalle del artefacto.

### Lo que sigue abierto

- La frase degenerada de banda baja aparece en **5 de 40**: "el fragmento más
  cercano dice que el contexto proporcionado no tiene una relevancia alta". El
  caso (b) del addendum no bastó.
- Una pregunta empeoró (372 → 524 palabras) y sigue fabricando.
- Los saludos "Hola" y "Continua" mantienen 93 palabras con identificadores
  PascalCase: van por un camino de código que estos prompts no tocan.

## Instrumento de desambiguación local (15.2.1)

`15.2/protocol.json` fija el contrato anterior a los prototipos de 15.2.2.
`freeze.json` sella instrumentos, corpus, índice, anclas y etiquetas por SHA256.
Las 30 oportunidades de `opportunities.json` son **semillas controladas de fuente**:
20 entradas de las consultas de salto y 10 dependencias internas. No reconstruyen
las 30 expansiones históricas de 6.d ni representan las semillas naturales del
retrieval. La auditoría histórica permanece intacta. Una mejora de resolución
condicionada a estas semillas necesita además ganar el A/B de las **84 consultas**.

Verificar la preparación, sin correr retrieval ni iniciar servicios:

```bash
python3 docs/eval/quality/15.2/accept.py --verify-instrument \
  --corpus /ruta/al/checkout/BusinessSuite.Xaf
```

Exige la revisión y los hashes del corpus congelado, toda la evidencia de fuente
y los tests adversariales; no omite comprobaciones si falta el corpus.
`source-evidence.json` conserva el contenido indexado y extractos de fuente
independientes: los rangos reales se localizaron por contenido porque algunos
rangos de payload están desplazados. El hash de la revisión actual del corpus
**no se presenta como revisión de ingesta**. El índice se capturó dos veces en
lectura, con huellas de payloads y vectores, sin almacenar vectores nuevos.

Para 15.2.2, el runner debe producir un JSON real y ejecutar:

```bash
python3 docs/eval/quality/15.2/accept.py --experiment /ruta/experimento.json \
  --output /ruta/comparacion.json
```

Contrato del JSON (implementado en `accept.compare`, sin valores por defecto):

| Campo | Evidencia requerida |
|---|---|
| `kind`, `freeze_sha256` | `measured_experiment` y SHA256 del archivo `freeze.json` |
| `parameters` | Opciones **efectivas**, exactamente `protocol.json.parameters`, sin override de perfil |
| `engine_commit`, `runner_sha256`, `command`, `runtime`, `hardware` | Revisión completa, hash del runner, argv ejecutado y entorno |
| `arms` | Las cinco claves `no-expansion`, `name-join`, `syntax`, `semantic`, `graph` |
| Cada brazo | `implementation_sha256`, `index_before/after`, `corpus_before/after`, `collection_config_before/after`, `models_before/after`, `build_seconds`, `update_seconds`, `machine_seconds`, `precision` |
| Huellas | Índice y corpus según `preparation.json`; configuración de colección mediante `capture.digest(collection_config)`; modelos mediante el mapa `preparation.json.models` |
| `precision` | Vacío solamente para `no-expansion`; para los otros, 30 filas en orden de oportunidades |
| Cada fila de precisión | `opportunity_id`, `query_id`, `question`, `seed_ids`, `seed_version`, `succeeded`, `elapsed_ms`, `candidates` |
| `candidates` / `hits` | Lista ordenada de objetos `{id, content_hash}` efectivamente devueltos, sin duplicados; máximo 20 / 10 |
| `runs` | Las 2.520 llamadas en el orden exacto que entrega `accept.schedule(bundle)`: 420 calentamientos y 2.100 mediciones |
| Cada llamada | Campos de `schedule`: `sequence`, `phase`, `replica` (base 0), `query_id`, `question`, `arm`; además `succeeded`, `elapsed_ms`, `hits` |
| `graph_execution` | `null` si no se ejecutó; obligatorio si el grafo supera las métricas. Incluye `implementation_sha256`, `runner_sha256`, `command`, `seconds`, `observations` |
| `observations` | Mapa escenario → observaciones reales tras cada operación de `graph-fixtures.json`, con todas las claves del esperado |

El runner de precisión recibe consulta, semilla e índice, **nunca los destinos
esperados**. Puntúa únicamente el primer candidato; rechazos no son aciertos y
un destino no acredita dos oportunidades de la misma consulta. La medición de
recall usa el retrieval normal, sin inyectar semillas del oráculo. Debe registrar
las huellas antes/después desde los recursos reales, no copiarlas del protocolo.
`capture.py` sirve para comprobar el índice local y modelos; sus dos lecturas
iguales detectan drift, pero no son una transacción/snapshot atómico de Qdrant.

Los tests construyen registros **sintéticos exclusivamente en memoria** para
comprobar fronteras, negativos y rechazos. No son mediciones de alternativas.
El fixture de grafo fija expectativas; todavía no implementa ni prueba un almacén
de relaciones real. En 15.2.2 el adaptador consume los inputs del fixture, no sus
salidas esperadas.

Exit `2` significa evidencia incompleta o inválida. Exit `0` distingue
`complete_no_promotion` (resultado nulo completo) de `complete_with_eligible`;
ninguno despliega producto. No volver a sellar el instrumento tras observar
resultados. Cambiarlo exige nueva procedencia y medir todos los brazos de nuevo.
La preparación solo produce `instrument_verified_not_experiment`.

## Calibración por binario (ítem 12.10)

El contrato local vincula los umbrales opcionales del perfil al cross-encoder
efectivo. **No se cambió el binario ni se recalibraron umbrales en este ítem.**

`12.10/verification.json` conserva evidencia de contrato sobre ONNX local,
Qdrant aislado, gate compartido, CLI y comparador con **respuestas sintéticas**.
No es evidencia de calidad de un candidato.

Aceptación local (requiere .NET, Qdrant local y ONNX/tokenizer; falla también si
hay cero tests, omitidos o falta infraestructura):

```bash
python3 infra/verify-gate-calibration.py
```

El comando ejecuta las pruebas y contrasta código, identidad y fixture con la
evidencia guardada, sin reescribirla. `--record` registra la primera aceptación
y rechaza sobrescribir un archivo existente; cualquier actualización posterior
exige preservar el informe anterior y documentar el cambio de instrumento.

Antes de proponer otro binario, congelar ambos brazos, índices, configuración de
retrieval, prompts, configuración/modelo de generación y el labeled-set completo.
Referencia congelada en esta ejecución: **65 preguntas, 38 presentes y 27
ausentes**, SHA-256
`8f5b03af69d0b2c5ec0c90b175aa49df908718851d6ba6c7397684f19b266912`.
No reemplazarlo por las 47 preguntas históricas de recall. Si se actualiza
justificadamente el instrumento, registrar el cambio y medir ambos brazos sobre
la misma versión; no bajar umbrales de aceptación ni omitir preguntas fallidas.

El barrido usa el CLI Release construido con ese código. Ejecutarlo sólo contra
colecciones experimentales y con cachés/auditoría separadas. Sus overrides
`StableGateScore=true/false` también respetan el rechazo de perfiles incompatibles:
preparar los manifiestos de prueba, no quitar la protección en colecciones servidas.

```bash
dotnet build src/RagEngine.Cli -c Release
python3 infra/gate-bandas-barrido.py --medir --salida control.scores.json
python3 infra/gate-bandas-barrido.py --scores control.scores.json
```

Las mediciones nuevas son `kind: "scores_only"`, con hash del labeled-set y
`rows[].estable/lotes.cross_encoder` observados. Fallos del CLI o JSON ausente
son errores, no scores cero; cero resultados exitosos se registra explícitamente.
La relectura del histórico sigue disponible. Los contadores de fabricación y
entrega del **barrido de scores** son proxies históricos; no juzgan la respuesta.

Después del barrido, generar y adjudicar **cada respuesta** de ambos brazos.
El comparador exige archivos con este contrato (snake_case):

| Campo | Evidencia obligatoria |
|---|---|
| `schema_version`, `kind` | `1`, `"measured"`; `"fixture"` sólo con `--fixtures`, nunca para aprobar activación |
| `labeled_set_sha256` | SHA-256 completo de los bytes del labeled-set |
| `cross_encoder` | Identidad efectiva: `model_sha256`, `tokenizer_sha256`, `binary`, `architecture`, `stable_gate_score`, `max_sequence_length`, `batch_size` |
| `calibration` | `cross_encoder` idéntico al observado, `low_confidence_threshold`, `high_confidence_threshold` |
| `controls` | SHA-256 completos: `corpus_sha256`, `retrieval_config_sha256`, `prompts_sha256`, `generation_config_sha256`; iguales entre brazos, incluyen índice/vectorización y versión/pesos del modelo de generación, excluyen sólo la intervención cross-encoder/umbrales |
| `rows` | Una fila por `(coleccion, pregunta)` exacta del labeled-set; conserva `etiqueta` y `categoria`, sin duplicados ni ausentes |
| Cada fila de `rows` | `retrieval_succeeded: true`, `score` finito en [0,1], `result_count` (cero exige score cero), `cross_encoder` observado si hay resultados; `answer`, `answer_label`, `reviewed_answer_sha256` (UTF-8 de la respuesta revisada), `judgement` no vacío |

Las etiquetas admitidas son `correcta`, `fabricacion`, `abstencion`, `incorrecta`.
`correcta` exige respuesta sustentada y correcta a una pregunta presente;
`fabricacion` incluye afirmaciones no sustentadas aunque el score sea alto;
`abstencion` registra que no entrega la respuesta solicitada. `judgement`
documenta la adjudicación y su evidencia; ni recall ni la banda asignan esa
etiqueta automáticamente. Los hashes detectan cambios, no prueban por sí solos
que la adjudicación humana sea acertada. Conservar respuestas y revisiones bajo
los permisos adecuados, sin secretos ni tokens.

```bash
python3 infra/gate-bandas-barrido.py \
  --comparar control.answers.json candidato.answers.json --salida comparacion.json
```

Exit 0 exige cobertura completa, controles iguales, **ninguna fabricación nueva
por pregunta** y total de correctas no inferior al control. Exit 1 señala
regresión o evidencia inválida/ausente; un total de fabricaciones igual no oculta
una fabricación nueva compensada por otra corregida. El informe conserva todas
las parejas, cruces de banda, pérdidas individuales y abstenciones. Un cambio de
banda no implica por sí solo respuesta correcta/incorrecta.

Activar el nuevo perfil sólo después de esa aprobación. Conservar ambos barridos,
respuestas e informe. Rollback: restaurar binario, tokenizer, parámetros y perfil
como una unidad, reiniciar el host y mantener el control; no mezclar umbrales int8
con fp32. Ningún resultado de este contrato local habilita un despliegue x64.

## Preflight de relaciones (ítem 15.1, parcial)

`15.1/preflight.py` cuenta candidatos del join sintáctico sobre colecciones
locales existentes, sin generar descripciones, consultar LLM, abrir cachés ni
escribir en Qdrant. Lee únicamente metadatos, dos veces por colección, y falla
si cambian entre lecturas, falta infraestructura o los datos son inválidos.
No es un snapshot atómico ni comprueba vectores.

```bash
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover \
  -s docs/eval/quality/15.1 -p 'test_preflight.py' -v
python3 docs/eval/quality/15.1/preflight.py \
  bsuite-repo bsuite-auditorias-test innovapp-docs micro-repo
```

Un candidato es un par dirigido de **chunks distintos** dentro de la misma
colección y `tenant_id`, con intersección entre `consumed_symbols` del origen
y `defined_symbols` del destino. Varios nombres compartidos cuentan una sola
vez por par; los homónimos siguen siendo candidatos, **no relaciones probadas**.
Los campos ausentes se contabilizan: cero candidatos sin símbolos no demuestra
ausencia de relaciones. Los tenant ausentes/vacíos solo se agrupan entre sí.

`15.1/preflight-2026-09-21.json` conserva el censo, sus hashes y procedencia:
3.341.841 candidatos en `bsuite-repo`, 19.134 en `bsuite-auditorias-test`,
0 en `innovapp-docs` (sin campos de símbolos) y 13.542 en `micro-repo`.
**Estos números no son pares de entidades, misses ni llamadas LLM previstas**;
esos valores quedan sin medir. No se deduce un presupuesto a partir del join.

La ejecución experimental quedó bloqueada por autorización de coste pendiente
(2 h provisionales, no autorizadas). Al retomarla, preparar las ≥20 preguntas
de relación y ≥10 negativos con evidencia origen/destino, calibración separada,
hash/n congelados y comparador del criterio literal del ledger. Congelar también
la política de extracción y versiones, contar pares reales y misses en caché
aislada, reestimar y confirmar el presupuesto **antes de generar**. No seleccionar
pares solo porque responden las preguntas de evaluación.

Este preflight **no es la aceptación de 15.1**: no mide A/B, ganancia @10,
no-regresión, fabricación, latencia de retrieval ni invalidación de descripciones.
No hay banda nueva ni promoción de pesos. El control permanece intacto; los
futuros vectores/descripciones deben usar espacio y caché separados, con
rollback por bandera OFF/peso cero sin borrar las bandas existentes.
