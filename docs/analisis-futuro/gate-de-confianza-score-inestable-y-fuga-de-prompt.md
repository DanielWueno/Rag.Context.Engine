# El gate de confianza: score de rerank no invariante al TopK + fuga del ejemplo negativo

> **Estado: investigado el 2026-08-22, nada implementado.** Salió de un caso real sobre
> `wiki-solis` en el que dos preguntas sobre InoVapp devolvieron respuestas inservibles. La
> investigación encontró tres cosas encadenadas, y la segunda invalida parcialmente la primera:
> no tiene sentido afinar el prompt de banda media mientras el número que decide la banda se
> mueve 0.13 según el `TopK`. **Leer el orden del plan antes de tocar nada.**

## Cómo se llegó aquí

Dos consultas reales contra `wiki-solis` (686 chunks, 100 % Markdown):

| Consulta | Score rerank | Banda | Resultado |
|---|---|---|---|
| `Procesos, reglas o demas relacionados con innovapp?` | 0.181 | media | "No encontré una coincidencia clara… pero el fragmento más cercano dice que el contexto proporcionado no tiene una relevancia alta para la pregunta." |
| `InnovApp?` | 0.788 | alta | Correcta y fundamentada |

La segunda **no era fabricación** (se sospechó y se descartó): el corpus describe InoVapp en
`US-32.1 — Acciones disponibles en InovApp` y `US-33.4 — Lógica y estructura offline para app
móvil`. El sistema funcionó bien ahí.

## Hallazgo 1 — el ejemplo negativo del prompt se copia literal

`LowConfidencePrompt.Addendum` incluye, como **ejemplo de lo que NO hay que hacer**, exactamente
la frase que el modelo emitió:

```
(b) It does NOT address the question → say only that there is nothing relevant…
    EXAMPLE of what NOT to do: "No encontré una coincidencia clara en el contenido
    indexado, pero el fragmento más cercano dice que el contexto proporcionado no
    tiene una relevancia alta para la pregunta."
```

Alguien ya observó este fallo y trató de corregirlo añadiendo el ejemplo. Con `qwen2.5-coder`
(7B) sale al revés: imita el texto concreto y la etiqueta "what NOT to do" no lo frena. Es la
contrapartida de lo aprendido en [[guardrail-banda-baja-conversacional]] — la regla abstracta no
bastaba y hubo que poner un ejemplo concreto; ahora el ejemplo concreto es el problema.

Tasa de reproducción medida: **2 de 3** consultas que cayeron en banda media emitieron la frase.

## Hallazgo 2 — el score del cross-encoder NO es invariante al `TopK` (el grave)

Misma consulta, mismo chunk ganador (`US-33.4`), sólo cambia `TopK`:

```
 topK rep  mejor score  sección del #1
    5   1       0.7046  US-33.4 — Lógica y estructura offline para app móvil
    5   2       0.7046  US-33.4
   10   1       0.5844  US-33.4        <-- cruza por debajo del umbral 0.60
   10   2       0.5844  US-33.4
   15   1       0.7112  US-33.4
   15   2       0.7112  US-33.4
```

Determinista dentro de cada `TopK`, pero con un salto de **0.13** entre ellos. Y ese salto cruza
`HighConfidenceThreshold = 0.60`, así que la misma pregunta con el mismo corpus obtiene una
respuesta correcta con `topK=5` y la respuesta inútil del Hallazgo 1 con `topK=10`.

**Mecanismo.** El pool de rerank es `3 × TopK`. `RunBatch` hace *padding dinámico al máximo real
del lote* (`OnnxCrossEncoderReRanker.cs:214`) y el modelo es int8, así que el score de un par
depende de la longitud a la que se rellenó su lote — es decir, de sus vecinos. Cambiar `TopK`
cambia el pool, cambia la composición de lotes, cambia el padding, cambia el score.

El código **ya conocía media causa** y la mitigó a medias (`OnnxCrossEncoderReRanker.cs:69-77`):

> El orden de entrada (candidates) NO es determinista […] el padding dinámico + cuantización int8
> hacen que el score de un chunk dependa de sus vecinos de lote […] Reordenamos por una clave
> estable (ChunkId) ANTES de batchear para fijar la composición de los lotes.

Ordenar por `ChunkId` fija la composición **para un pool dado**. No la hace invariante al tamaño
del pool. Emparenta con [[rag-engine-retrieval-no-determinismo-qdrant]], que cerró el
no-determinismo entre corridas idénticas pero no este eje.

**Consecuencia de diseño:** los umbrales 0.05 / 0.60 se comparan contra un número que no es una
propiedad del par (consulta, chunk) sino del lote en que le tocó viajar. Cualquier calibración de
umbrales hecha sobre esta base es arena movediza.

## Hallazgo 3 — la banda media es demasiado ancha

Distribución medida sobre 10 consultas contra `wiki-solis` (`topK=10`):

| Tipo | Consulta | Score | Banda | Fuga | Útil |
|---|---|---|---|---|---|
| directa | `InovApp` | 0.978 | alta | no | sí |
| directa | `Que puede hacer un auditor desde la app movil?` | 0.965 | alta | no | sí |
| parafraseada | `como sincroniza la aplicacion de campo cuando recupera senal` | 0.584 | media | **sí** | **no** |
| parafraseada | `quien puede dar avance a una revision en curso` | 0.157 | media | no | no |
| meta | `Procesos, reglas o demas relacionados con innovapp?` | 0.181 | media | **sí** | no |
| meta | `Se menciona algo de un sistema movil, una api, microservicio, innovapp?` | 0.018 | sin grounding | no | correcto rechazar |
| meta | `Que informacion hay sobre integraciones?` | 0.026 | sin grounding | no | correcto rechazar |
| fuera de dominio | `procedimiento para renovar el pasaporte` | 0.013 | sin grounding | no | correcto rechazar |
| fuera de dominio | `receta de paella valenciana` | 0.009 | sin grounding | no | correcto rechazar |

Lecturas:

- **La banda sin grounding funciona bien.** Todo lo genuinamente ausente cae ≤ 0.026 y se rechaza
  con un mensaje sensato. No tocar.
- **La banda media, 0.05–0.60, mete en el mismo saco** un 0.074 (basura real) y un 0.584 (el
  chunk correcto). Es donde se pierden las respuestas buenas.
- Hay un hueco natural aparente entre ruido (≤ ~0.18) y acierto (≥ ~0.58), pero **n=10 no
  autoriza a mover un umbral**: hace falta medirlo sobre un eval-set etiquetado.

## Plan

**Registrado en el ledger como Ola 4** (`ejecucion-plan.estado.json`), así que `/plan-estado` y
`/plan-siguiente` lo ven. Los ids son `4.1-medir-invarianza-topk`, `4.2-score-de-gate-estable`,
`4.3-recalibrar-umbrales-banda`, `4.4-fuga-ejemplo-negativo-prompt`,
`4.5-regresion-bandas-y-estabilidad` y `4.6-limpiar-huerfanos-bsuite-repo`. Este documento es el
razonamiento; el ledger es el estado.

> Ojo con el orden: `/plan-siguiente` toma el primer `pendiente` respetando el orden de olas, así
> que llegará a la Ola 4 después de los 8 pendientes de las olas 1-3. Si el gate se considera más
> urgente que la deuda estructural, hay que mover los ítems o la ola.

Un ítem por sesión, commit al cerrar cada uno. El orden importa: 2 antes que 4, porque recalibrar
o afinar el prompt sobre un score inestable es tirar el trabajo.

### Ítem 1 — Cuantificar la no-invarianza al `TopK` (investigación, ~15 min máquina)

`n=1` no basta. Barrer `TopK ∈ {3,5,8,10,15,20}` sobre ≥ 20 consultas de `wiki-solis` e
`innovapp-docs`, registrando el score del **mismo** chunk ganador.

```bash
# la colección y el eval-set salen de docs/eval/innovapp-docs.eval-set.json
for k in 3 5 8 10 15 20; do
  curl -s -X POST http://localhost:5080/api/search -H 'Content-Type: application/json' \
    -d "{\"query\":\"<Q>\",\"collection\":\"wiki-solis\",\"topK\":$k,\"rerank\":true}" \
  | python3 -c "import sys,json;r=json.load(sys.stdin);print($k, round(r[0]['score'],4), r[0]['section'])"
done
```

**Criterio de salida:** distribución del rango `max-min` por consulta. Si la mediana del rango
supera 0.05, el gate por umbral absoluto no es defendible tal como está y el ítem 2 es obligatorio.

### Ítem 2 — Hacer el score invariante a la composición del lote (implementación)

Tres opciones, medir coste antes de elegir:

1. **Padding fijo** a `MaxSequenceLength` (512). Invariante por construcción. Coste: la inferencia
   escala con `seqLen`, así que puede ser varias veces más lenta — hoy un pool de 30 tarda
   ~1.000 ms; hay que medir cuánto sube.
2. **Lotes por longitud**: agrupar candidatos de longitud similar. Reduce el padding y estabiliza,
   pero **no** garantiza invarianza si el pool cambia.
3. **Puntuar el candidato ganador solo**, en un lote de tamaño 1, tras el rerank. Sólo estabiliza
   el número que alimenta el gate, que es lo que importa aquí. Una inferencia extra por consulta.

**Recomendada la 3** por relación coste/beneficio: el ranking puede seguir con padding dinámico
(el orden relativo apenas cambia), y el gate pasa a leer un score reproducible.

**Criterio de salida:** el score del chunk #1 idéntico (±0.001) para todo `TopK` del barrido del
ítem 1. **Rollback:** dejarlo tras una bandera de configuración `CrossEncoder:StableGateScore`
(default off hasta validar) para poder volver sin recompilar.

### Ítem 3 — Recalibrar los umbrales con datos, no a ojo (investigación)

Sólo después del ítem 2. Sobre `docs/eval/innovapp-docs.eval-set.json` y el de `bsuite-auditorias`,
etiquetar cada consulta como "el corpus SÍ contiene la respuesta" / "no la contiene" y trazar la
distribución de scores de ambos grupos.

**Criterio de salida:** umbrales que maximicen (respuestas correctas entregadas) sin aumentar las
fabricaciones respecto a la línea base actual, medido con `infra/quality-baseline.py`. Incluir
negativos adversariales, no sólo positivos.

### Ítem 4 — Arreglar la fuga del ejemplo negativo (implementación, pequeño)

Dos vías, no excluyentes:

- **A.** Reescribir la rama (b) del addendum para **describir** la forma prohibida en vez de
  citarla, y añadir un ejemplo **positivo** del texto deseado. Los modelos pequeños copian lo que
  ven; hay que enseñarles el bueno.
- **B.** Sacar la decisión (b) del LLM: si el score queda en la parte baja de la banda media, que
  el código devuelva directamente el mensaje corto, como ya hace la banda sin grounding.
  Determinista. Depende del ítem 3 para saber dónde cortar.

**Coste de tocar el prompt:** `PromptHashesTests` fallará; hay que actualizar el hash de
`LowConfidencePrompt` y justificarlo en el commit. **No** invalida la caché de resúmenes — sólo el
prompt de resumen alimenta `prompt_version`.

**Criterio de salida:** 0 apariciones de la frase fugada en un barrido de ≥ 20 consultas de banda
media, y ninguna regresión en las bandas alta y sin-grounding.

### Ítem 5 — Regresión permanente

Añadir al arnés una prueba que barra las tres bandas y falle si aparece la frase fugada o si el
score del #1 varía con `TopK`. Sin esto, los ítems 2 y 4 se re-rompen en silencio.

## Cabos sueltos menores

- **El corpus escribe el nombre de dos formas**: `InnoVapp` en los documentos técnicos y `InovApp`
  en `requerimiento-funcional.md`. Para la rama densa es casi indiferente; para la dispersa son
  términos distintos y una consulta con una grafía pierde señal de los documentos con la otra. Es
  del wiki, no del motor.
- **`Simple-mode resumen coverage 0% (0/10) — degrading to raw content`** en `wiki-solis`: la
  colección se ingestó sin `--con-resumen`, así que el modo Simple cae a contenido crudo. Con el
  cambio de fuentes de prosa ya no es un problema de presentación, pero conviene decidir si un
  corpus de documentación necesita resumen o si el propio texto ya cumple ese papel.
