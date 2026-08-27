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

#### Resultado de la medición (2026-08-25)

Barrido real ejecutado con el CLI (`RagEngine.Cli.dll search ... --rerank --output json`) contra el
build Release, sobre 22 consultas reales (11 de `docs/eval/innovapp-docs.eval-set.json` sobre
`innovapp-docs`, 11 manuales sobre `wiki-solis`), `TopK ∈ {3,5,8,10,15,20}` — 132 invocaciones,
ninguna tardó más de ~3.2 s (sin señales de recarga de modelo por invocación). El "chunk ganador"
de cada consulta es el `chunk_id` top-1 de la corrida con `TopK=10`; se rastreó su
`similarity_score` en las otras 5 corridas, registrando "fuera del pool" cuando no aparecía.

#### Tabla de detalle por consulta (score del chunk ganador de k=10, por TopK)

| # | Colección | Consulta | Sección del chunk ganador | k=3 | k=5 | k=8 | k=10 | k=15 | k=20 |
|---|---|---|---|---|---|---|---|---|---|
| 1 | innovapp-docs | ¿Quién puede cancelar un ticket que está en estatus Registrado? | Reglas de negocio | fuera del pool | fuera del pool | 0.9951 | 0.9970 | 0.9963 | 0.9934 |
| 2 | innovapp-docs | ¿Qué es el Folio Único de Atención? | Reglas de negocio | 0.9991 | 0.9991 | 0.9992 | 0.9982 | 0.9991 | 0.9988 |
| 3 | innovapp-docs | ¿Cómo se marca una notificación como leída? | Reglas de negocio | 0.9984 | 0.9988 | 0.9986 | 0.9989 | 0.9988 | 0.9984 |
| 4 | innovapp-docs | ¿Qué permiso se necesita para marcar un ticket como 'Ing. en Traslado'? | Objetivo | 0.9972 | 0.9954 | 0.9965 | 0.9976 | 0.9975 | 0.9956 |
| 5 | innovapp-docs | Si levanté un ticket por error y ya no lo necesito, ¿lo puedo borrar yo mismo? | US-2.1 — Cancelación de la propia petición | fuera del pool | fuera del pool | 0.8435 | 0.9250 | 0.8778 | 0.7873 |
| 6 | innovapp-docs | ¿En qué momento me sale la pregunta de qué tan contento quedé con el servicio? | Descripción | fuera del pool | fuera del pool | fuera del pool | 0.0157 | 0.0145 | 0.0151 |
| 7 | innovapp-docs | ¿Me avisan por correo cuando resuelven mi problema? | US-43.1 — Generación de la notificación in-app | 0.0834 | 0.0760 | 0.0955 | 0.0674 | 0.0675 | 0.1060 |
| 8 | innovapp-docs | ¿Necesito un permiso especial para decir que el técnico ya va en camino a atender mi ticket? | Descripción | fuera del pool | fuera del pool | fuera del pool | 0.8501 | 0.8932 | 0.9075 |
| 9 | innovapp-docs | ¿Puedo cancelar mi ticket en cualquier momento? | Criterios de Aceptación | 0.2967 | 0.3288 | 0.2899 | 0.3657 | 0.3165 | 0.2813 |
| 10 | innovapp-docs | ¿Qué significa que un nodo del timeline esté 'en curso' comparado con uno que ya pasó pero no terminó el proceso? | Componentes del Timeline de Trayectoria | fuera del pool | fuera del pool | 0.6864 | 0.7160 | 0.7627 | 0.7219 |
| 11 | innovapp-docs | ¿Cómo solicito mis vacaciones o días económicos? | 5.1 — Regresión de Ticket (ciclo completo) | 0.0298 | 0.0407 | 0.0300 | 0.0271 | 0.0410 | 0.0351 |
| 12 | wiki-solis | InovApp | RF-32 — Alcance funcional de InovApp | fuera del pool | 0.9844 | 0.9767 | 0.9803 | 0.9886 | 0.9913 |
| 13 | wiki-solis | Que puede hacer un auditor desde la app movil? | US-33.4 — Lógica y estructura offline para app móvil | 0.9689 | 0.9539 | 0.9818 | 0.9742 | 0.9797 | 0.9638 |
| 14 | wiki-solis | como sincroniza la aplicacion de campo cuando recupera senal | US-33.4 — Lógica y estructura offline para app móvil | 0.5893 | 0.6554 | 0.6021 | 0.6787 | 0.6643 | 0.5490 |
| 15 | wiki-solis | quien puede dar avance a una revision en curso | Descripción | 0.1036 | 0.1685 | 0.2450 | 0.1732 | 0.1430 | 0.0981 |
| 16 | wiki-solis | Procesos, reglas o demas relacionados con innovapp? | Reglas de negocio | 0.1072 | 0.0861 | 0.0827 | 0.1420 | 0.0655 | 0.1004 |
| 17 | wiki-solis | Se menciona algo de un sistema movil, una api, microservicio, innovapp? | US-33.4 — Lógica y estructura offline para app móvil | 0.0109 | 0.0094 | 0.0245 | 0.0242 | 0.0033 | 0.0064 |
| 18 | wiki-solis | Que informacion hay sobre integraciones? | Riesgos (RGO) | 0.0266 | 0.0262 | 0.0325 | 0.0264 | 0.0166 | 0.0150 |
| 19 | wiki-solis | procedimiento para renovar el pasaporte | RF-15 — Transición del Ticket a "Proceso" con la Persona Asignada del wizard y su historial de asignaciones | fuera del pool | fuera del pool | 0.0198 | 0.0150 | 0.0138 | 0.0177 |
| 20 | wiki-solis | receta de paella valenciana | Mapa de Estados | fuera del pool | 0.0114 | 0.0109 | 0.0068 | 0.0125 | 0.0092 |
| 21 | wiki-solis | Segmentación por departamento en catálogos, procesos y monitores? | RF-13 — Segmentación por departamento en catálogos, procesos y monitores | 0.9998 | 0.9998 | 0.9998 | 0.9998 | 0.9998 | 0.9999 |
| 22 | wiki-solis | Que reglas se siguen para poder aplica la firma? | US-31.2 — Trazabilidad de la firma en bitácora | 0.0591 | 0.0472 | 0.0407 | 0.0445 | 0.0468 | 0.0463 |

#### Tabla resumen: rango (max-min) por consulta

| # | Colección | Consulta | Rango (max-min) | Fuera del pool (de 6) |
|---|---|---|---|---|
| 1 | innovapp-docs | ¿Quién puede cancelar un ticket que está en estatus Registrado? | 0.0036 | 2 |
| 2 | innovapp-docs | ¿Qué es el Folio Único de Atención? | 0.0010 | 0 |
| 3 | innovapp-docs | ¿Cómo se marca una notificación como leída? | 0.0005 | 0 |
| 4 | innovapp-docs | ¿Qué permiso se necesita para marcar un ticket como 'Ing. en Traslado'? | 0.0022 | 0 |
| 5 | innovapp-docs | Si levanté un ticket por error y ya no lo necesito, ¿lo puedo borrar yo mismo? | 0.1377 | 2 |
| 6 | innovapp-docs | ¿En qué momento me sale la pregunta de qué tan contento quedé con el servicio? | 0.0013 | 3 |
| 7 | innovapp-docs | ¿Me avisan por correo cuando resuelven mi problema? | 0.0386 | 0 |
| 8 | innovapp-docs | ¿Necesito un permiso especial para decir que el técnico ya va en camino a atender mi ticket? | 0.0574 | 3 |
| 9 | innovapp-docs | ¿Puedo cancelar mi ticket en cualquier momento? | 0.0843 | 0 |
| 10 | innovapp-docs | ¿Qué significa que un nodo del timeline esté 'en curso' comparado con uno que ya pasó pero no terminó el proceso? | 0.0764 | 2 |
| 11 | innovapp-docs | ¿Cómo solicito mis vacaciones o días económicos? | 0.0139 | 0 |
| 12 | wiki-solis | InovApp | 0.0145 | 1 |
| 13 | wiki-solis | Que puede hacer un auditor desde la app movil? | 0.0279 | 0 |
| 14 | wiki-solis | como sincroniza la aplicacion de campo cuando recupera senal | 0.1297 | 0 |
| 15 | wiki-solis | quien puede dar avance a una revision en curso | 0.1470 | 0 |
| 16 | wiki-solis | Procesos, reglas o demas relacionados con innovapp? | 0.0765 | 0 |
| 17 | wiki-solis | Se menciona algo de un sistema movil, una api, microservicio, innovapp? | 0.0212 | 0 |
| 18 | wiki-solis | Que informacion hay sobre integraciones? | 0.0175 | 0 |
| 19 | wiki-solis | procedimiento para renovar el pasaporte | 0.0060 | 2 |
| 20 | wiki-solis | receta de paella valenciana | 0.0057 | 1 |
| 21 | wiki-solis | Segmentación por departamento en catálogos, procesos y monitores? | 0.0001 | 0 |
| 22 | wiki-solis | Que reglas se siguen para poder aplica la firma? | 0.0183 | 0 |

**Mediana del rango sobre las 22 consultas (todas tuvieron al menos un score válido): 0.0179.**
**7 de 22 consultas (32 %) superan individualmente el umbral de 0.05**, con casos hasta 0.147
(#15, `quien puede dar avance a una revision en curso`) y 0.138 (#5, cancelar ticket propio).

**Conclusión:** la *mediana* (0.0179) **no supera** los 0.05 del criterio literal del ítem, así que
el ítem 2 (ítem 4.2 del ledger) no queda disparado automáticamente por ese criterio estricto. Pero
la distribución es de cola larga y no despreciable: casi un tercio de las consultas cruza el umbral
individualmente, con rangos hasta ~3× el corte, y el patrón repite el hallazgo n=1 original
(`como sincroniza...` → US-33.4, rango 0.1297 en esta corrida — no reproduce los valores exactos del
Hallazgo 2 original, que salieron de otra corrida anterior; el score exacto no es reproducible bit a
bit entre ejecuciones, algo ya documentado como no-determinismo de Qdrant en empates de score, pero
la magnitud de la variación por `TopK` sí es consistente). Dado que el criterio del ítem se definió
sobre la mediana y esta no lo cruza, el ítem 4.2 **no pasa a obligatorio por el criterio literal**;
sin embargo, con un tercio de consultas mostrando inestabilidad de esta magnitud, el gate por umbral
absoluto sigue sin ser plenamente defendible para el subconjunto de cola larga, y conviene tratar el
ítem 4.2 como recomendado aunque no obligatorio, o acotar su alcance a estabilizar sólo el score que
alimenta el gate (opción 3 del ítem 2) sin necesidad de resolver todo el batching.

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

#### Resultado de la implementación (2026-08-25)

Implementada la **opción 3**. `CrossEncoderOptions.StableGateScore` (default `false`) añade, al final
de `OnnxCrossEncoderReRanker.ReRankAsync`, una segunda llamada a `ScorePairs` con una lista de **un
solo elemento** — el ganador —, y ese valor sustituye el score de la posición #1. Con un lote de uno,
`seqLen` es la longitud del propio par y deja de depender del vecino más largo del lote, y por tanto
del `TopK`, que es quien fija el tamaño del pool (`3 × TopK`).

Decisión explícita: **no se reordena**. El ranking lo sigue decidiendo la pasada por lotes y el
ganador se queda en #1 aunque su score estable caiga por debajo del de la posición #2. Estabilizar el
gate no es rehacer el ranking; eso exigiría puntuar los `TopK` en lotes de 1, que no es lo que pide
este ítem.

##### Verificación del criterio

Se repitió el barrido del ítem 1 — las mismas 22 consultas × `TopK ∈ {3,5,8,10,15,20}` — dos veces,
con la bandera apagada y encendida: 264 invocaciones del CLI Release. Para cada consulta se agrupan
las 6 corridas por el `chunk_id` que quedó en #1 y se mide el rango (max−min) **dentro de cada
grupo** — comparar entre chunks distintos no diría nada, porque son pares distintos.

| # | Colección | Consulta | chunks distintos en #1 | rango máx `off` | rango máx `on` |
|---|---|---|---|---|---|
| 1 | innovapp-docs | ¿Quién puede cancelar un ticket que está en estatus Registrado? | 2 | 0.043225 | **0.000000** |
| 2 | innovapp-docs | ¿Qué es el Folio Único de Atención? | 1 | 0.000995 | **0.000000** |
| 3 | innovapp-docs | ¿Cómo se marca una notificación como leída? | 1 | 0.000498 | **0.000000** |
| 4 | innovapp-docs | ¿Qué permiso se necesita para marcar un ticket como 'Ing. en Traslado'? | 1 | 0.002232 | **0.000000** |
| 5 | innovapp-docs | Si levanté un ticket por error y ya no lo necesito, ¿lo puedo borrar yo mismo? | 2 | 0.137687 | **0.000000** |
| 6 | innovapp-docs | ¿En qué momento me sale la pregunta de qué tan contento quedé con el servicio? | 4 | 0.005341 | **0.000000** |
| 7 | innovapp-docs | ¿Me avisan por correo cuando resuelven mi problema? | 1 | 0.039669 | **0.000000** |
| 8 | innovapp-docs | ¿Necesito un permiso especial para decir que el técnico ya va en camino a atender mi ticket? | 4 | 0.057412 | **0.000000** |
| 9 | innovapp-docs | ¿Puedo cancelar mi ticket en cualquier momento? | 2 | 0.084326 | **0.000000** |
| 10 | innovapp-docs | ¿Qué significa que un nodo del timeline esté 'en curso' comparado con uno que ya pasó pero no terminó el proceso? | 2 | 0.078000 | **0.000000** |
| 11 | innovapp-docs | ¿Cómo solicito mis vacaciones o días económicos? | 1 | 0.013869 | **0.000000** |
| 12 | wiki-solis | InovApp | 2 | 0.014542 | **0.000000** |
| 13 | wiki-solis | Que puede hacer un auditor desde la app movil? | 1 | 0.027927 | **0.000000** |
| 14 | wiki-solis | como sincroniza la aplicacion de campo cuando recupera senal | 1 | 0.129684 | **0.000000** |
| 15 | wiki-solis | quien puede dar avance a una revision en curso | 1 | 0.146995 | **0.000000** |
| 16 | wiki-solis | Procesos, reglas o demas relacionados con innovapp? | 3 | 0.059242 | **0.000000** |
| 17 | wiki-solis | Se menciona algo de un sistema movil, una api, microservicio, innovapp? | 2 | 0.045540 | **0.000000** |
| 18 | wiki-solis | Que informacion hay sobre integraciones? | 3 | 0.007380 | **0.000000** |
| 19 | wiki-solis | procedimiento para renovar el pasaporte | 5 | 0.004858 | **0.000000** |
| 20 | wiki-solis | receta de paella valenciana | 2 | 0.005712 | **0.000000** |
| 21 | wiki-solis | Segmentación por departamento en catálogos, procesos y monitores? | 1 | 0.000072 | **0.000000** |
| 22 | wiki-solis | Que reglas se siguen para poder aplica la firma? | 1 | 0.018347 | **0.000000** |

**Con la bandera encendida el rango es 0,000000 — idéntico bit a bit — en las 22 consultas**, muy por
debajo de los ±0,001 del criterio. Con la bandera apagada, 19 de 22 lo superan, con mediana 0,0231 y
máximo 0,1470.

Las 22 consultas tuvieron al menos un grupo comparable; en 12 de ellas el mismo chunk quedó en #1 en
las 6 corridas, así que la comparación cubre el barrido entero y no un par de puntos sueltos. Que en
las otras 10 cambie el chunk de #1 según el `TopK` es variación del *ranking* — el pool es distinto —,
no del score, y queda fuera del alcance de este ítem.

##### Score del #1 por `TopK`, con la bandera encendida

| # | Consulta | k=3 | k=5 | k=8 | k=10 | k=15 | k=20 |
|---|---|---|---|---|---|---|---|
| 1 | ¿Quién puede cancelar un ticket que está en estatus Registrado? | 0.955106 \* | 0.955106 \* | 0.994751 | 0.994751 | 0.994751 | 0.994751 |
| 2 | ¿Qué es el Folio Único de Atención? | 0.999119 | 0.999119 | 0.999119 | 0.999119 | 0.999119 | 0.999119 |
| 3 | ¿Cómo se marca una notificación como leída? | 0.998820 | 0.998820 | 0.998820 | 0.998820 | 0.998820 | 0.998820 |
| 4 | ¿Qué permiso se necesita para marcar un ticket como 'Ing. en Traslado'? | 0.996576 | 0.996576 | 0.996576 | 0.996576 | 0.996576 | 0.996576 |
| 5 | Si levanté un ticket por error y ya no lo necesito, ¿lo puedo borrar yo mismo? | 0.396912 \* | 0.396912 \* | 0.899764 | 0.899764 | 0.899764 | 0.899764 |
| 6 | ¿En qué momento me sale la pregunta de qué tan contento quedé con el servicio? | 0.010405 \* | 0.015336 \* | 0.015336 \* | 0.014766 | 0.014766 | 0.068448 \* |
| 7 | ¿Me avisan por correo cuando resuelven mi problema? | 0.086543 | 0.086543 | 0.086543 | 0.086543 | 0.086543 | 0.086543 |
| 8 | ¿Necesito un permiso especial para decir que el técnico ya va en camino a atender mi ticket? | 0.133344 \* | 0.406648 \* | 0.577610 \* | 0.874198 | 0.874198 | 0.874198 |
| 9 | ¿Puedo cancelar mi ticket en cualquier momento? | 0.390129 | 0.390129 | 0.260304 \* | 0.390129 | 0.390129 | 0.390129 |
| 10 | ¿Qué significa que un nodo del timeline esté 'en curso' comparado con uno que ya pasó pero no terminó el proceso? | 0.579922 \* | 0.579922 \* | 0.579922 \* | 0.754444 | 0.754444 | 0.754444 |
| 11 | ¿Cómo solicito mis vacaciones o días económicos? | 0.031463 | 0.031463 | 0.031463 | 0.031463 | 0.031463 | 0.031463 |
| 12 | InovApp | 0.954958 \* | 0.954958 \* | 0.982839 | 0.982839 | 0.982839 | 0.982839 |
| 13 | Que puede hacer un auditor desde la app movil? | 0.974393 | 0.974393 | 0.974393 | 0.974393 | 0.974393 | 0.974393 |
| 14 | como sincroniza la aplicacion de campo cuando recupera senal | 0.595818 | 0.595818 | 0.595818 | 0.595818 | 0.595818 | 0.595818 |
| 15 | quien puede dar avance a una revision en curso | 0.115435 | 0.115435 | 0.115435 | 0.115435 | 0.115435 | 0.115435 |
| 16 | Procesos, reglas o demas relacionados con innovapp? | 0.093822 | 0.093822 | 0.093822 | 0.093822 | 0.142734 \* | 0.162562 \* |
| 17 | Se menciona algo de un sistema movil, una api, microservicio, innovapp? | 0.013944 \* | 0.013944 \* | 0.013944 \* | 0.012593 | 0.013944 \* | 0.013944 \* |
| 18 | Que informacion hay sobre integraciones? | 0.028385 | 0.028385 | 0.028385 | 0.028385 | 0.022511 \* | 0.027714 \* |
| 19 | procedimiento para renovar el pasaporte | 0.012818 \* | 0.019069 \* | 0.022635 | 0.022635 | 0.017014 \* | 0.018528 \* |
| 20 | receta de paella valenciana | 0.003452 \* | 0.010758 | 0.010758 | 0.010758 | 0.010758 | 0.010758 |
| 21 | Segmentación por departamento en catálogos, procesos y monitores? | 0.999794 | 0.999794 | 0.999794 | 0.999794 | 0.999794 | 0.999794 |
| 22 | Que reglas se siguen para poder aplica la firma? | 0.041969 | 0.041969 | 0.041969 | 0.041969 | 0.041969 | 0.041969 |

\* = el chunk que quedó en #1 no es el mismo que en `k=10`; su score no es comparable con el de esa
fila, y por eso el criterio se evalúa por grupos de chunk idéntico.

##### Coste

Medido sobre los mismos 264 re-ranks, leyendo `ElapsedMs` del log estructurado
(`logs/rag-engine-20260825.json`, evento `Cross-encoder re-ranked`):

| Pool (`3 × TopK`) | n `off` | mediana `off` | n `on` | mediana `on` | Δ |
|---|---|---|---|---|---|
| 9 | 22 | 583 ms | 22 | 602 ms | +20 ms (+3,3 %) |
| 15 | 22 | 766 ms | 22 | 747 ms | −20 ms (−2,5 %) |
| 24 | 22 | 972 ms | 22 | 964 ms | −8 ms (−0,8 %) |
| 30 | 22 | 1.092 ms | 22 | 1.088 ms | −4 ms (−0,4 %) |
| 45 | 22 | 1.446 ms | 22 | 1.421 ms | −24 ms (−1,7 %) |
| 60 | 22 | 1.824 ms | 22 | 1.771 ms | −52 ms (−2,9 %) |

El coste **aislado** de la inferencia extra sí se puede medir directamente, porque se cronometra
aparte (`StableGateMs`): **mediana 15 ms, p90 24 ms, máximo 33 ms** (n=132). Sobre el pool de 30 que
cita el ítem —1.092 ms de mediana— eso es un **+1,4 %**, y queda por debajo del ruido de corrida a
corrida del propio re-rank: de ahí que varias filas de la tabla salgan en negativo. La referencia de
"~1.000 ms para un pool de 30" del ítem 1 se confirma (1.092 ms medidos).

##### Estado de la bandera

Queda en `false` por defecto, y no por prudencia genérica: `LowConfidenceThreshold` y
`HighConfidenceThreshold` están calibrados sobre el score por lotes, y el score estable no es el mismo
número (p. ej. consulta 5, `k=10`: 0,9250 por lotes → 0,899764 estable). Encenderla sin recalibrar
mueve las bandas. Esa recalibración es el ítem 4.3, que debe correrse con la bandera encendida.

### Ítem 3 — Recalibrar los umbrales con datos, no a ojo (investigación)

Sólo después del ítem 2. Sobre `docs/eval/innovapp-docs.eval-set.json` y el de `bsuite-auditorias`,
etiquetar cada consulta como "el corpus SÍ contiene la respuesta" / "no la contiene" y trazar la
distribución de scores de ambos grupos.

**Criterio de salida:** umbrales que maximicen (respuestas correctas entregadas) sin aumentar las
fabricaciones respecto a la línea base actual, medido con `infra/quality-baseline.py`. Incluir
negativos adversariales, no sólo positivos.

#### Resultado de la recalibración (2026-08-26)

**Conclusión: los umbrales no se mueven.** `LowConfidenceThreshold` sigue en 0,05 y
`HighConfidenceThreshold` en 0,60. Lo que sí cambia es que `CrossEncoder:StableGateScore` queda
**encendida**: la calibración se hizo sobre el score estable y confirma que esos dos valores siguen
siendo los mejores del barrido con él.

##### El conjunto etiquetado

`docs/eval/gate-bandas.labeled-set.json` — 65 consultas, 38 con la respuesta presente en el corpus y
27 ausentes:

| Origen | presente | ausente |
|---|---|---|
| `innovapp-docs.eval-set.json` (26 preguntas ya etiquetadas) | 22 | 4 |
| `bsuite-auditorias.eval-set.json` (16 preguntas) | 16 | 0 |
| Negativos añadidos por este ítem | — | 23 |

Los negativos no son sólo "receta de paella". Cuatro categorías, con 3–5 ejemplares cada una:

- **adyacente-ausente** (10): vocabulario del corpus, funcionalidad que no existe. *"¿Me pueden
  avisar por WhatsApp o SMS cuando cambie mi ticket?"* contra un corpus que documenta a fondo las
  notificaciones por correo e in-app.
- **cross-corpus** (6): la respuesta existe, pero en otra colección. Preguntas positivas de
  `innovapp-docs` disparadas contra `bsuite-auditorias-test` y al revés.
- **meta** (3): preguntas sobre el corpus, no contestables con un chunk.
- **fuera-de-dominio** (8): control duro, ancla el extremo bajo de la distribución.

Cada ausencia está verificada con `grep` contra el **contenido realmente indexado en Qdrant**
—volcado de los 743 chunks de `innovapp-docs` y los 1.677 de `bsuite-auditorias-test`—, no contra el
repositorio fuente. La evidencia concreta va en el campo `evidencia` de cada fila.

##### El score no separa las dos clases

Medido con `infra/gate-bandas-barrido.py --medir` (65 consultas × 2 modos de bandera, `TopK`=10):

| | AUC | Solape |
|---|---|---|
| score por lotes | 0,874 | [0,0157 – 0,8956] contiene 18/38 positivos y 21/27 negativos |
| score estable | 0,868 | [0,0148 – 0,9195] contiene 21/38 positivos y 22/27 negativos |

No hay hueco. El positivo *"Si escojo 'otro' como razón para cancelar, ¿tengo que explicar por qué?"*
puntúa 0,6555 y tres negativos caen a 0,656 / 0,6721 / 0,6763 — dentro de dos centésimas. El negativo
más alto, *"¿Cómo se envía por correo el informe de auditoría al auditado?"* (0,9195), supera a 17 de
los 38 positivos. Cualquier corte único acierta como máximo el 78–81 % del conjunto.

Esto invalida la lectura optimista del Hallazgo 3: el hueco que se intuía entre ruido (≤ ~0,18) y
acierto (≥ ~0,58) con n=10 **no existe** con n=65. Era un artefacto del tamaño de muestra.

##### El barrido, con las definiciones del criterio

*Respuesta correcta entregada* = positivo con score ≥ alto, o sea respondido **sin** el matiz de banda
media; contar el matiz como entrega sería contar el problema como solución. *Fabricación* = ausente
con score ≥ alto.

| alto (bajo = 0,05) | entregadas | fabricaciones |
|---|---|---|
| 0,45 | 27 | 6 |
| 0,50 | 26 | 6 |
| 0,55 – 0,65 | 26 | 5 |
| 0,68 – 0,70 | 25 | 2 |
| 0,75 | 25 | 1 |
| 0,90 | 18 | 1 |

Línea base (0,05 / 0,60 sobre score por lotes): 26 entregadas, 5 fabricaciones. **Ninguno de los 132
pares (bajo, alto) del barrido entrega más que la línea base sin fabricar más.** Bajar el alto compra
una respuesta correcta al precio de una fabricación; subirlo compra tres o cuatro fabricaciones menos
al precio de una respuesta correcta. 0,60 está en la frontera, no en un mal sitio.

El umbral bajo tampoco se mueve: 0,05 pierde 1 positivo de 38 y rechaza 11 de 27 negativos; bajarlo a
0,02 recupera ese positivo pero deja pasar 6 negativos más. Coincide con lo que ya decía el ítem —la
banda sin grounding funciona y no había que tocarla— pero ahora está medido, no supuesto.

##### Verificación con `infra/quality-baseline.py`

73 preguntas reales (68 de `innovapp-docs` + 5 de `bsuite-auditorias-test`), tres corridas contra la
API con generación completa:

| Corrida | rechazo pleno | banda baja | directo | fabricaciones\* |
|---|---|---|---|---|
| línea base — lotes, 0,05 / 0,60 | 24,7 % | 20,5 % | 54,8 % | 5 |
| **estable, 0,05 / 0,60** | 24,7 % | **15,1 %** | **60,3 %** | **5** |
| estable, 0,05 / 0,45 | 26,0 % | 15,1 % | 58,9 % | 5 |

\* respuestas sin reservas cuyo score real quedó por debajo de 0,60. Es el
`directo_con_score_bajo_pct` del script **descontando las filas sin fuentes**: ese porcentaje mete en
el mismo saco 25–28 turnos conversacionales que nunca tuvieron chunks (score `None` → 0), que son la
banda sin grounding haciendo su trabajo, no fabricaciones del gate.

Encender la bandera con los mismos umbrales sube las respuestas sin reservas de 54,8 % a 60,3 % y baja
los matices inútiles de 20,5 % a 15,1 %, con las fabricaciones iguales (5 → 5). Tres consultas pierden
grounding al pasar al score estable —*"Existe contenido para la bitacora de defectos?"*, *"Puedes
ayudarme a redactar un ticket?"* y *"para las ordenes de compra como realizo una factura?"*, todas con
score base entre 0,052 y 0,073— y las tres son casos cuya respuesta **no** está en el corpus
(`grep -i 'factur'` = 0 en `innovapp-docs`). Ninguna consulta gana grounding indebidamente.

Bajar el alto a 0,45, que era la hipótesis de origen del ítem —"la banda media es demasiado ancha, ahí
se pierden las respuestas buenas"—, **no rescata nada**: 58,9 % de respuestas directas frente a 60,3 %,
dentro del ruido de generación, y en el conjunto etiquetado sube las fabricaciones de 5 a 6.

##### Qué queda para el ítem 4

El caso que abrió esta investigación —*"puedo cancelar cualquier ticket?"*— pasa de este texto:

> No encontré una coincidencia clara en el contenido indexado, pero el fragmento más cercano dice que
> el contexto proporcionado no tiene una relevancia alta para la pregunta.

a una respuesta fundamentada sobre las reglas de validación y cierre. Pero eso pasó porque el score
estable lo empujó de 0,5579 a 0,6283 y cruzó a banda alta, no porque el matiz mejorara. El matiz sigue
copiando el ejemplo negativo del prompt cada vez que se activa. **No es un problema de dónde está el
corte, es del texto del addendum**, y lo arregla el ítem 4.4.

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
