# 15.2.2 — Comparación de alternativas locales de resolución

**Resultado: hipótesis nula. Experimento completo, ninguna alternativa elegible.**
No se promueve nada; el control sin expansión sigue siendo el comportamiento de producción
y el grafo persistido queda desactivado.

Fecha de medición: 2026-09-22 · motor `f8ef366` · corpus `8b6a06b4` (revisión sellada por
15.2.1) · índice `bsuite-repo`, 23.058 puntos, huella `85f4c2fe…` idéntica antes y después
de la corrida · .NET 10.0.10 · macOS Arm64, 12 núcleos.

## Qué se midió

Cinco brazos sobre el instrumento congelado en 15.2.1, con la misma configuración efectiva
(TopK 10, min-score 0,10, sin rerank, fusión 1,0/1,3/2,5 y RrfK 60). La única variable es el
mecanismo de expansión:

| Brazo | Mecanismo | Papel |
|---|---|---|
| `no-expansion` | Sin segundo salto | Control de recall y latencia |
| `name-join` | Salto por nombre de símbolo (6.a en producción) | Control de precisión |
| `syntax` | Cualificación sintáctica: tipo declarado del receptor, sin compilar | Alternativa |
| `semantic` | `SemanticModel` de Roslyn con referencias restauradas | Alternativa |
| `graph` | Aristas versionadas persistidas en SQLite, con ACL por salto | Alternativa |

Cobertura ejecutada: 2.520 llamadas de recuperación (84 consultas × 1 calentamiento + 5
réplicas × 5 brazos, en el orden rotado que fija el protocolo) y 120 resoluciones de
precisión (30 oportunidades × 4 brazos con expansión). Ninguna llamada falló. Las cinco
réplicas devolvieron los mismos ids en el mismo orden en todos los brazos.

## Resultados

| Brazo | Precisión @1 | Precisión sobre devueltos | Ganancia neta de salto | Pérdidas fuera de salto | Fuera de dominio con ids nuevos | p95 | Δp95 | Construcción |
|---|---|---|---|---|---|---|---|---|
| `no-expansion` | — | — | 0 | 0 | 0 | 99,4 ms | — | 0 s |
| `name-join` | **5/30** | 17 % | 0 | 2 | 8 de 8 | 103,7 ms | +4,2 ms | 0 s |
| `syntax` | **17/30** | 61 % | 0 | 1 | 6 de 8 | 100,3 ms | +0,8 ms | 24,4 s |
| `semantic` | **22/30** | 73 % | +2 | 2 | 6 de 8 | 100,3 ms | +0,9 ms | 36,1 s |
| `graph` | **22/30** | 73 % | +2 | 2 | 6 de 8 | 101,8 ms | +2,3 ms | 36,3 s |

Umbrales de promoción: ≥27/30 destinos correctos, ganancia neta de salto ≥5, **cero**
pérdidas fuera del subconjunto, **cero** ids nuevos en las preguntas fuera de dominio y
Δp95 ≤150 ms. Ningún brazo los cumple; el único que todos superan es el de latencia.

## Lo que sí queda demostrado

**Cualificar el destino corrige el problema de precisión de 6.d.** El join por nombre
acierta 5 de 30 — consistente con los 6/30 de la auditoría histórica, que es la validación
de que el banco reproduce el control. Añadir el tipo declarante lleva la precisión a 22/30
(73 % sobre los devueltos, frente al 17 % del control). Las colisiones entre homónimos eran
un problema real y tienen solución local, sin Neo4j, sin servidores y sin LLM.

**Esa precisión no se convierte en recall.** Es el hallazgo que decide el ítem. Con 22/30
destinos bien resueltos, `semantic` y `graph` sólo ganan dos consultas de salto (`q55`,
`q63`) frente a las cinco exigidas, y ambas pierden `q25` y `q76` fuera del subconjunto.
Resolver mejor a dónde saltar no implica que el chunk de destino le falte a la respuesta:
en la mayoría de las preguntas de salto la fusión primaria ya recuperaba evidencia
suficiente, y la expansión reordena el pool sin añadir lo que faltaba.

**Todas las expansiones ensucian las preguntas fuera de dominio.** Seis de las ocho reciben
ids que el control no devolvía. El guardia de ruido no admite ninguno, y ningún mecanismo de
cualificación lo evita: el salto se dispara igual porque la semilla tiene símbolos
consumidos, aunque la pregunta no tenga nada que ver con el corpus.

**La latencia no es el obstáculo.** El brazo más caro añade 4,2 ms de p95 sobre un
presupuesto de 150 ms. Descartar estas alternativas por coste de consulta habría sido una
conclusión equivocada.

**El grafo no gana nada por existir.** Iguala a `semantic` en las cinco métricas —misma
precisión, misma ganancia, mismas pérdidas— y cuesta más: +2,3 ms de p95 frente a +0,9 ms,
más un segundo estado durable que mantener sincronizado con el índice. Su contrato sí se
ejecutó por completo y salió limpio (0 errores sobre los 15 pasos de `graph-fixtures.json`:
alta, actualización atómica, interrupción antes del commit, reinicio, replay idempotente,
borrado sin huérfanos y autorización comprobada en cada salto). Su no-promoción es por
métricas, no por evidencia ausente.

## Condición para reabrir

Las tres alternativas quedan conservadas con su condición de entrada, no descartadas. Lo que
falló no fue el mecanismo de resolución sino su efecto sobre el recall del eval-set actual.
Reabrir exige evidencia nueva de al menos uno de estos frentes:

- Un conjunto de preguntas donde el destino del salto **no** esté ya cubierto por la fusión
  primaria; en el eval-set actual, 11 de las 20 consultas de salto ya acertaban sin expansión.
- Un criterio de disparo que apague el salto cuando la consulta no pertenece al dominio, que
  es lo que hoy rompe el guardia de ruido en todos los brazos.
- Una revisión de las pérdidas `q25` y `q76`, para separar un fallo del mecanismo de un
  efecto del reordenamiento del pool.

## Procedencia y reejecución

- Evidencia medida: `experiment-2026-09-22.json.gz` (SHA-256 del contenido sin comprimir
  `517f7bbd25cdffbe3486062ad22da3eac3ceea1416fdfaf4866d66fa139f14b5`).
- Veredicto del comparador sellado: `verdict-2026-09-22.json`.
- Observaciones del contrato del grafo: `graph-contract-observations-2026-09-22.json`.
- Puerta de aceptación: `python3 docs/eval/quality/15.2/runner/verify.py`. Recalcula
  precisión, recall, latencia y contrato sobre la evidencia persistida y falla si falta
  evidencia, si la cobertura no llega a 2.520 llamadas, si el contrato del grafo no se
  ejecutó o si la conclusión archivada deja de coincidir con la recalculada. No necesita
  Qdrant; sí necesita el corpus.
- Banco de pruebas: `docs/eval/quality/15.2/runner/`, fuera de `RagEngine.slnx` a propósito.
  Sus pruebas (`runner.tests/`, 19 casos) se ejecutan con
  `dotnet test docs/eval/quality/15.2/runner.tests/RunnerTests.csproj`.

## Estado del código de producción

El único cambio en `src/` es la costura `ISymbolExpansionQualifier`, que permite acotar el
segundo salto a destinos cualificados. **Está inerte**: se inyecta como `IEnumerable` y sin
implementación registrada —el caso de producción— `QdrantSemanticRetriever` conserva
exactamente el comportamiento de 6.a. Existe porque los cinco brazos debían medirse a través
del mismo camino de recuperación; sin ella, la comparación habría sido contra una
reimplementación del retriever y no contra producción.

Rollback: revertir esa costura y el directorio `runner/`. No hay estado que deshacer — el
experimento no escribió en el índice, ni en la caché de resúmenes, ni en el corpus, y el
grafo vivió en una base SQLite aislada del scratchpad.

## Limitaciones declaradas

- El brazo semántico compila los 16 proyectos del corpus; 144 errores de compilación
  persisten, concentrados en `REYMA.Facturacion.CFDI.Reportes` (87) y
  `BusinessSuite.Xaf.Blazor.Server` (38), ninguno de los cuales aloja las 30 oportunidades.
  Los cuatro proyectos que sí las alojan compilan con 8 errores (`Backbone`), 2 (`Compras`)
  y 0 (`Computo`, `GestionProyectos`). 20.054 de 50.786
  invocaciones resuelven a un ensamblado del corpus; el resto son framework y DevExpress.
- 366 chunks (sintáctico) y 204 (semántico) no se pudieron localizar en su archivo por
  coincidencia única de contenido, y quedan fuera de la cualificación. Los metadatos de línea
  del payload no se usaron para suplirlos: tienen desfases conocidos.
- Las huellas de índice, corpus, configuración y modelos se capturaron antes y después de la
  corrida completa, no entre brazo y brazo: el calendario del protocolo los intercala, así
  que un corchete por brazo no existe. Ningún brazo escribe, y ambas capturas coinciden.
- El banco de precisión reproduce el salto con semilla explícita fuera de `SearchAsync`,
  porque el puerto público de Core no expone una expansión con semilla dada. Usa el mismo
  filtro, el mismo límite de 20 y el mismo desempate determinista que producción.
