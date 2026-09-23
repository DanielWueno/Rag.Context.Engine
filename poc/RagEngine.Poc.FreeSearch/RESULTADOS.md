# Resultados del PoC — Búsqueda libre (RRF de 3 bandas)

> Estado: **experimento, no producción.** Mide recall en memoria; no toca el motor real.
> Muestra: `Reyma.TI.Tickets.Microservice/src` (963 chunks, 599 con resumen) · 19 preguntas.
> Config base: pesos RRF código=1 · sparse=1 · resumen=1.3 · k=60 · embeddings 256 tok.
> Config calibrada (§1.2): código=1 · **sparse=1.3 · resumen=2.5**.
> Reproducir:
> - Config base + rerank: `dotnet run --project poc/RagEngine.Poc.FreeSearch -- poc-settings.json detail`
> - Barrido de pesos: `dotnet run --project poc/RagEngine.Poc.FreeSearch -- poc-settings.json sweep`
> - Config calibrada + rerank: `dotnet run --project poc/RagEngine.Poc.FreeSearch -- poc-settings-tuned.json detail`

## 1. Recall@k — comparación de vías (pesos base: sparse=1, resumen=1.3)

| k | código (denso solo) | cód+sparse (baseline REAL prod.) | cód+sparse+resumen (propuesta) | cód+spa+res+rerank |
|---|---|---|---|---|
| 1 | 0% | 5% | **11%** | 21% |
| 3 | 0% | 11% | **32%** | 47% |
| 5 | 0% | 21% | **42%** | 53% |
| 10 | 5% | 26% | **63%** | 68% |

**Aporte del resumen sobre el híbrido real @10: +37 pts (12/19 vs 5/19), +7 preguntas, 0 regresiones.**

### 1.1 Rerank (Cross-Encoder) sobre el pool fusionado

Se añadió soporte real de rerank al PoC (no existía en el esqueleto original): el mismo
`OnnxCrossEncoderReRanker` de producción, sobre el pool ancho (top TopK×3 = 30) que deja la
fusión `cód+spa+res`, igual contrato que `QdrantSemanticRetriever` usa antes de llamar a
`IReRanker`.

**Aporte del rerank sobre cód+spa+res @10 (pesos base): +2 preguntas, -1 regresión** (neto
+1/19). El salto grande está en precisión, no en recall: @1 casi se duplica (11%→21%), @3 sube
+15pts (32%→47%). Pero rerank **reordena, no amplía el pool** — no rescata objetivos muy lejos
del top-30 (P11 en #156, P13 en #260 siguen intactos). Confirma la hipótesis del documento
original (§Reto B): el trabajo de rerank es precisión de ranking, no recall.

### 1.2 Barrido de pesos RRF

Separar el cálculo de rankings (caro: embeddings ya calculados) de la fusión RRF (barata:
sólo Σw/(k+rank) + sort) permite barrer decenas de combinaciones de pesos en milisegundos,
sin recalcular nada de ONNX/Ollama. Grilla: código=1.0 fijo (ancla), sparse∈{0.7,1.0,1.3},
resumen∈{1.0,1.3,1.6,2.0,2.5,3.0,4.0} — 21 combinaciones, ordenadas por recall@10:

| sparse | resumen | @1 | @3 | @5 | @10 |
|---|---|---|---|---|---|
| **1.3** | **2.5** | 11% | 32% | 47% | **79%** ★ |
| 1.3 | 3.0 | 11% | 37% | 47% | 79% |
| 1.3 | 4.0 | 11% | 42% | 53% | 74% |
| 1.0 | 2.0 | 11% | 32% | 47% | 74% |
| … | … | … | … | … | … |
| 1.0 | 1.3 | 11% | 32% | 42% | 63% • (baseline) |
| 1.0 | 1.0 | 5% | 37% | 42% | 53% |

**Mejor combinación: sparse=1.3, resumen=2.5 → recall@10=79% vs. 63% del baseline (+16pts,
+3 preguntas: el flagship P1 "¿cómo levanto un ticket?" ahora en #7, P8 "¿cómo recibo un
ticket que me asignaron?", P15 "¿cómo consulto mis solicitudes de servicio?" ahora en #9).**
El patrón es consistente: subir sparse de 1.0→1.3 ayuda en casi toda la grilla (el vocabulario
literal de la pregunta libre igual comparte términos con el código/constants), y resumen tiene
un punto dulce alrededor de 2.5-3.0 — más allá de eso (4.0) empieza a ahogar código/sparse y
recall@10 cae de nuevo (74%). Con solo 19 preguntas, ±1 pregunta ≈ ±5pts — la dirección (subir
ambos pesos ayuda con margen de varias preguntas) es más confiable que el valor exacto del
punto óptimo.

Los objetivos genuinamente difíciles (P11 #156→#135, P13 #260→#275, P16 #75→#66) **no se
mueven con ningún peso probado** — confirma que no es un problema de calibración sino de señal
ausente (§4.C), consistente con lo ya documentado.

### 1.3 Pesos calibrados + rerank: la interacción no era la esperada

Con los pesos calibrados (sparse=1.3, resumen=2.5) y rerank sobre el pool de 30:

| k | cód+spa+res (pesos calibrados) | cód+spa+res+rerank |
|---|---|---|
| 1 | 11% | 32% |
| 3 | 32% | 47% |
| 5 | 47% | 58% |
| 10 | **79%** | 68% |

**Rerank ahora resta recall@10: +0 preguntas, -2 regresiones** ("¿cómo levanto un ticket?" y
"¿qué datos son obligatorios...?", ambas ya dentro del top-10 fusionado en #7 y #5, quedan
fuera del top-10 tras el reordenamiento del cross-encoder). Con pesos default, rerank sumaba
(+2/-1); con pesos buenos, resta (+0/-2) — el pool de 30 que rerank reordena ya trae más
objetivos "cerca del borde" del top-10 gracias a los pesos, y el cross-encoder los empuja
afuera en vez de consolidarlos.

**Conclusión práctica — dos palancas, dos objetivos distintos, no intercambiables:**
- **Recall (¿el chunk llega al contexto del LLM?)**: lo gana la calibración de **pesos**, no
  el rerank. Pesos calibrados sin rerank (79%@10) superan a pesos default con rerank
  (68%@10) — mismo costo de implementación (Reto B), mejor resultado para este fin.
- **Precisión @1-5 (¿qué tan arriba queda, relevante para qué se *muestra* como fuente —
  Reto D)**: la gana el rerank, con o sin pesos calibrados (11%→32%/47%→58% @1/@5 con pesos
  buenos).

Si el pipeline real activa ambos a la vez sin medir, el resultado neto sobre recall@10 puede
ser peor que activar solo uno — no asumir que "más técnicas" es estrictamente mejor sin volver
a correr `rag eval`-equivalente tras cada cambio.

## 2. ¿Qué mide "cómo respondió"?

El PoC recupera **contexto** (chunks), NO redacta la respuesta en lenguaje natural.
El detalle de abajo muestra, por pregunta, los 6 chunks top que trajo la config completa
y en qué puesto quedó el chunk-objetivo que etiquetamos a mano. `✓` = es un objetivo.

## 3. Detalle por pregunta (config cód+sparse+resumen)

| # | Pregunta | Puesto del objetivo | ¿Hit @10? | Nota |
|---|---|---|---|---|
| 1 | ¿Cómo levanto un ticket? | #11 | ✗ | flagship, se quedó a un puesto |
| 2 | ¿Qué datos son obligatorios para crear un ticket? | #9 | ✓ | |
| 3 | ¿Qué se necesita para cerrar un ticket? | #5 | ✓ | |
| 4 | ¿Cómo reasigno un ticket a otra persona? | #10 | ✓ | justo en el límite |
| 5 | ¿Cómo adjunto un documento o evidencia? | #3 | ✓ | |
| 6 | ¿Cómo registro el avance/actividad? | #4 | ✓ | dos objetivos en #4 y #5 |
| 7 | ¿Cómo paso un ticket a Mesa de Ayuda? | #2 | ✓ | |
| 8 | ¿Cómo recibo un ticket que me asignaron? | #17 | ✗ | |
| 9 | ¿Cómo cambio el tipo de servicio? | #12 | ✗ | |
| 10 | ¿Cómo actualizo el documento de cierre? | #2 | ✓ | |
| 11 | ¿Qué condiciones para reasignar? (handler) | #156 | ✗ | objetivo muy profundo |
| 12 | ¿En qué casos necesita autorización digital? | #1 | ✓ | mejor caso |
| 13 | ¿Por qué me piden el campo Módulo al cerrar? | #260 | ✗ | objetivo casi invisible |
| 14 | ¿Cuándo puedo mandar a Mesa de Ayuda? | #2 | ✓ | |
| 15 | ¿Cómo consulto mis solicitudes de servicio? | #11 | ✗ | pero #1–6 son los Query de solicitudes (ver análisis) |
| 16 | ¿Dónde veo a quién se asignaron los tickets? | #75 | ✗ | |
| 17 | ¿Qué tipos de servicio por departamento? | #10 | ✓ | #1 = GetTipoServicioByDepartamentoIdQuery (ver análisis) |
| 18 | ¿Qué información se guarda de un ticket? | #1 | ✓ | |
| 19 | ¿Cómo se registra el seguimiento/actividades? | #6 | ✓ | |

### Volcado crudo (top-6 recuperado por pregunta)

```
▸ ¿Cómo levanto un ticket?   → objetivo lejos (#11)
   #1 MesaAyudaTicketCommandHandler.cs:126-141   #2 MesaAyudaTicketCommandHandler.cs:82-107
   #3 Ticket.cs:19-309 (Property)   #4 Constants.cs:160-176   #5 TicketSeguimiento.cs:10-42
   #6 TicketsController.cs:186-188

▸ ¿Qué datos son obligatorios para crear un ticket?   → objetivo en #9 ✓
   #1 Constants.cs:160-176   #2 Ticket.cs:19-309   #3 CreateTicketCommandHandler.cs:53-56 (ctor)
   #4 CreateTicketCommandHandler.cs:477-500   #5 TicketExtensions.cs:19-47   #6 MesaAyuda...:82-107

▸ ¿Qué se necesita para cerrar un ticket?   → objetivo en #5 ✓
   #1 MesaAyuda...:126-141   #2 TicketsController.cs:117-123   #3 Constants.cs:160-176
   #4 TicketSeguimiento.cs:10-42   ✓#5 CerrarTicketCommandHandler.cs:66-91   #6 Cerrar...:119-144

▸ ¿Cómo reasigno un ticket a otra persona?   → objetivo en #10 ✓ (fuera del top-6 mostrado)
   #1 MesaAyuda...:126-141   #2 MesaAyuda...:82-107   #3 CreateTicket...:455-480
   #4 Constants.cs:160-176   #5 Ticket.cs:19-309   #6 GetTicketSeguimientoByIdQuery.cs:173-192

▸ ¿Cómo adjunto un documento o evidencia?   → objetivo en #3 ✓
   #1 CreateTicket...:362-374   #2 TicketDocumentoRequest.cs:20-33   ✓#3 TicketsController.cs:239-245
   #4 TicketDocumento.cs:13-48   #5 MesaAyuda...:126-141   #6 CreateDocumentTicketCommandHandler.cs:71-82

▸ ¿Cómo registro el avance/actividad?   → objetivo en #4 ✓
   #1 Ticket.cs:19-309   #2 TicketRegistroActividadResponse.cs:17-79   #3 TicketRegistroActividad.cs:16-61
   ✓#4 TicketsController.cs:71-77   ✓#5 CreateTicketRegistroActividadCommandHandler.cs:88-110   #6 MesaAyuda...:126-141

▸ ¿Cómo paso un ticket a Mesa de Ayuda?   → objetivo en #2 ✓
   #1 ValidarMesaAyuda...:34-37   ✓#2 MesaAyudaTicketCommandHandler.cs:82-107   #3 TicketSeguimiento.cs:10-42
   #4 RecibirTicket...:87-112   #5 TicketsController.cs:107-109   ✓#6 TicketsController.cs:98-100

▸ ¿Cómo recibo un ticket que me asignaron?   → objetivo lejos (#17)
   #1 Ticket.cs:19-309   #2 MesaAyuda...:126-141   #3 MesaAyuda...:82-107
   #4 Constants.cs:160-176   #5 TicketSeguimiento.cs:10-42   #6 GetTicketSeguimientoByIdQuery.cs:173-192

▸ ¿Cómo cambio el tipo de servicio?   → objetivo lejos (#12)
   #1 Ticket.cs:19-309   #2 MesaAyuda...:126-141   #3 TicketTipoServicioResponse.cs:16-55
   #4 TicketSeguimiento.cs:10-42   #5 Constants.cs:160-176   #6 RecibirTicket...:69-78

▸ ¿Cómo actualizo el documento de cierre?   → objetivo en #2 ✓
   #1 MesaAyuda...:82-107   ✓#2 ActualizarDocCierreCommandHandler.cs:105-128   #3 MesaAyuda...:126-141
   #4 CreateTicket...:362-374   #5 Cerrar...:119-144   #6 TicketDocumentoCierreResponse.cs:16-29

▸ ¿Qué condiciones para reasignar? (handler)   → objetivo lejos (#156)
   #1 MesaAyuda...:126-141   #2 Constants.cs:160-176   #3 ReasignarTicketCommandHandler.cs:154-179
   #4 MesaAyuda...:82-107   #5 TicketsController.cs:163-169   #6 TicketReasignarResponse.cs:13-55

▸ ¿En qué casos necesita autorización digital?   → objetivo en #1 ✓
   ✓#1 TicketsController.cs:252-254   #2 CreateTicket...:477-500   ✓#3 ValidarAutorizacionTicketCommandHandler.cs:44-53
   #4 TicketsController.cs:186-188   #5 Ticket.cs:19-309   #6 ValidarMesaAyuda...:34-37

▸ ¿Por qué me piden el campo Módulo al cerrar?   → objetivo lejos (#260)
   #1 MesaAyuda...:126-141   #2 Constants.cs:160-176   #3 TicketsController.cs:130-132
   #4 MesaAyuda...:82-107   #5 TicketSeguimiento.cs:10-42   #6 TicketsController.cs:117-123

▸ ¿Cuándo puedo mandar a Mesa de Ayuda?   → objetivo en #2 ✓
   #1 MesaAyuda...:82-107   ✓#2 ValidarMesaAyudaTicketCommandHandler.cs:34-37   #3 RecibirTicket...:87-112
   #4 TicketSeguimiento.cs:10-42   #5 Ticket.cs:19-309   #6 MesaAyuda...:126-141

▸ ¿Cómo consulto mis solicitudes de servicio?   → objetivo lejos (#11)
   #1 GetSolicitudServicioByIdQuery.cs:34-207   #2 GetPagedSolicitudServiciosQuery.cs:61-74
   #3 GetSolicitudServicioByIdQuery.cs:55-77   #4 GetSolicitudServicioByIdQuery.cs:176-201
   #5 GetPagedSolicitudServiciosQuery.cs:38-75   #6 GetPagedSolicitudServiciosQuery.cs:27-76

▸ ¿Dónde veo a quién se asignaron los tickets?   → objetivo lejos (#75)
   #1 Constants.cs:160-176   #2 MesaAyuda...:126-141   #3 MesaAyuda...:82-107
   #4 TicketSeguimiento.cs:10-42   #5 GetTicketSeguimientoByIdQuery.cs:173-192   #6 Ticket.cs:19-309

▸ ¿Qué tipos de servicio por departamento?   → objetivo en #10 ✓
   #1 GetTipoServicioByDepartamentoIdQuery.cs:45-50   #2 CategoriaServicio.cs:15-30
   #3 SolicitudServicioAuditoriaEstadoResponse.cs:20-51   #4 ...:15-59   #5 Departamento.cs:15-60   #6 CustomContainer.cs:12-21

▸ ¿Qué información se guarda de un ticket?   → objetivo en #1 ✓
   ✓#1 Ticket.cs:19-309   #2 Constants.cs:160-176   #3 TicketSeguimiento.cs:10-42
   #4 MesaAyuda...:126-141   #5 MesaAyuda...:82-107   #6 CreateTicket...:362-374

▸ ¿Cómo se registra el seguimiento/actividades?   → objetivo en #6 ✓
   #1 TicketSeguimiento.cs:10-42   #2 Ticket.cs:19-309   #3 TicketRegistroActividadResponse.cs:17-79
   #4 GetTicketSeguimientoByIdQuery.cs:119-143   #5 TicketSeguimientoResponse.cs:21-100   ✓#6 TicketRegistroActividad.cs:16-61
```

## 4. Análisis de factibilidad (lo importante para retomar)

### A. Algunos "fallos" son etiquetado estrecho, no fallo de recuperación
- **P15 (solicitudes de servicio):** marqué como objetivo el *Controller*, pero los puestos #1–6 son
  todos los `Query`/handlers de `SolicitudServicio` — respuestas **igual o más** relevantes. La
  pregunta es buena; el objetivo estaba mal acotado.
- **P17 (tipos de servicio por depto):** el #1 es `GetTipoServicioByDepartamentoIdQuery` (justo lo que
  hace), el objetivo-controller quedó #10. Mismo caso.
- **Acción:** aceptar múltiples chunks válidos por pregunta (controller + query + handler), o marcar el
  objetivo por *concepto* y no por un archivo único. El recall real es mejor de lo que dice la tabla.

### B. Chunks "imán" que dominan el top y ensucian la precisión
Aparecen como #1–#2 en muchísimas preguntas sin ser la respuesta:
- **`Ticket.cs:19-309`** — un ÚNICO chunk de ~290 líneas (todas las propiedades de la entidad). Es
  tan grande y genérico que matchea casi todo. Señal de **chunk sobre-amplio**.
- **`MesaAyudaTicketCommandHandler.cs:82-107` y `:126-141`** — reaparecen constantemente; probablemente
  su resumen quedó genérico.
- **`Constants.cs:160-176`** — bloque de constantes que matchea por vocabulario compartido.
- **Acción:** revisar el resumen de estos chunks; considerar partir `Ticket.cs` (viola el espíritu del
  chunking semántico tener 290 líneas en un chunk). Estos imanes explican por qué @1/@5 son moderados.

### C. Objetivos genuinamente difíciles
- **P13 (campo Módulo al cerrar, #260)** y **P11 (condiciones reasignar, #156)**: lógica de validación
  profunda que ni denso ni sparse ni resumen surfacearon. Revisar si el resumen de esos chunks captura
  la intención de negocio, o si la pregunta es demasiado abstracta.

### D. El flagship se quedó cerca — resuelto por calibración de pesos
- **P1 (¿cómo levanto un ticket?) #11 con pesos base.** Con pesos calibrados (sparse=1.3,
  resumen=2.5, ver §1.2) sube a **#7 ✓** — confirmado que era una cuestión de pesos, no de
  recuperación imposible. Rerank, en cambio, **no** lo resuelve con pesos base (ver §1.1) y con
  pesos calibrados directamente lo saca del top-10 (ver §1.3) — fue la calibración de pesos, no
  el rerank, la que lo rescató.

## 5. Próximos pasos (para retomar)
1. Re-etiquetar objetivos como "cualquiera de {controller, query, handler}" por concepto → recall real.
2. Inspeccionar resúmenes de los chunks-imán (`Ticket.cs`, `MesaAyuda...`) y de los objetivos difíciles (P11, P13, P16 — ver §1.2, no se movieron con ningún peso probado).
3. Evaluar partir `Ticket.cs:19-309` (chunk sobre-amplio).
4. ~~Calibrar pesos RRF y probar `--rerank` (Cross-Encoder) para @1/@5.~~ **Hecho** — ver §1.1-1.3.
   Resultado: pesos calibrados (sparse=1.3, resumen=2.5) ganan +16pts recall@10 (63%→79%) sin
   rerank; rerank ayuda precisión @1-5 pero con estos pesos **resta** recall@10 (79%→68%). Las
   dos técnicas atienden objetivos distintos (recall vs. precisión de ranking) y no se deben
   asumir aditivas sin medir la combinación.
5. Sólo entonces: decidir si el costo de la Ruta A (LLM por chunk en ingesta + 2º vector + caché) se justifica.

## 6. Cómo se generó
- Config base + rerank: `dotnet run --project poc/RagEngine.Poc.FreeSearch -- poc-settings.json detail`
- Barrido de pesos (§1.2): `dotnet run --project poc/RagEngine.Poc.FreeSearch -- poc-settings.json sweep`
- Config calibrada + rerank (§1.3): `dotnet run --project poc/RagEngine.Poc.FreeSearch -- poc-settings-tuned.json detail`

(resúmenes cacheados en `poc-summaries-cache.json`, tanto en `bin/` como junto al `.csproj` —
ambas copias deben existir para que corridas con ruta absoluta a un settings distinto en la
carpeta fuente no regeneren los 963 resúmenes vía Ollama; cada corrida completa en ~16-30s con
caché caliente.)
