# Resultados del PoC — Búsqueda libre (RRF de 3 bandas)

> Estado: **experimento, no producción.** Mide recall en memoria; no toca el motor real.
> Muestra: `Reyma.TI.Tickets.Microservice/src` (963 chunks, 599 con resumen) · 19 preguntas.
> Config: pesos RRF código=1 · sparse=1 · resumen=1.3 · k=60 · embeddings 256 tok.
> Reproducir: `dotnet run --project poc/RagEngine.Poc.FreeSearch -- poc-settings.json detail`

## 1. Recall@k — comparación de tres vías

| k | código (denso solo) | cód+sparse (baseline REAL prod.) | cód+sparse+resumen (propuesta) |
|---|---|---|---|
| 1 | 0% | 5% | **11%** |
| 3 | 0% | 11% | **32%** |
| 5 | 0% | 21% | **42%** |
| 10 | 5% | 26% | **63%** |

**Aporte del resumen sobre el híbrido real @10: +37 pts (12/19 vs 5/19), +7 preguntas, 0 regresiones.**

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

### D. El flagship se quedó cerca
- **P1 (¿cómo levanto un ticket?) #11:** a un puesto del top-10. Un rerank o ajustar pesos probablemente
  lo mete.

## 5. Próximos pasos (para retomar)
1. Re-etiquetar objetivos como "cualquiera de {controller, query, handler}" por concepto → recall real.
2. Inspeccionar resúmenes de los chunks-imán (`Ticket.cs`, `MesaAyuda...`) y de los objetivos difíciles (P11, P13).
3. Evaluar partir `Ticket.cs:19-309` (chunk sobre-amplio).
4. Calibrar pesos RRF y probar `--rerank` (Cross-Encoder) para @1/@5.
5. Sólo entonces: decidir si el costo de la Ruta A (LLM por chunk en ingesta + 2º vector + caché) se justifica.

## 6. Cómo se generó
`dotnet run --project poc/RagEngine.Poc.FreeSearch -- poc-settings.json detail`
(resúmenes cacheados en `poc-summaries-cache.json`; corre en ~16s.)
