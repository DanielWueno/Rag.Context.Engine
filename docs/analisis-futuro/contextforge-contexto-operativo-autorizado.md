# ContextForge y contexto operativo autorizado para RagEngine

> Analisis original del 2026-10-05, commit `55b9d87`, ficha `10.5`.
> Reconciliacion del 2026-10-07: `10.7-reconciliar-mcp-multiapi-y-k8s`, **ola 23**.
> Prioridad posterior del usuario: `10.8-separar-servidor-contextforge-del-consumo-rag`.
> **No hay integracion implementada, despliegue comprobado ni adopcion de ContextForge.**

La [integracion de ramas](integracion-rama-local-2026-10-05.md) habia diferido este
analisis y reservado la ola 23. Se recuperan sus diez fichas sin fusionar el historial
original: `20.1`-`20.10` locales pasan a `23.1`-`23.10`. Las olas 20/21 actuales conservan
sus capacidades de servidor de IA y dev/QA. La ola 22 de conversaciones sigue reservada.
El resultado historico de `10.5` no se reescribe; este documento actualiza el planteamiento,
no acredita haber ejecutado las pruebas que propone.

## 1. Decision y alcance

**Entrega inmediata acordada:** dejar ContextForge servido en `reyma-dev` como gateway
MCP compartido y utilizable por clientes autorizados de otros equipos/personas,
independientemente de RagEngine, en el proyecto hermano **Mcp.ContextForge**.
La secuencia propietaria es `1.1` -> `1.2` -> `1.3` en su ledger; las referencias
`23.11` -> `23.12` -> `23.13` de este repositorio quedan solo como trazabilidad.
La autenticacion, permisos, acceso privado con TLS, persistencia, respaldo y operacion
son parte de esa entrega; no se posponen junto con las configuraciones de consumo.
La instalacion no queda autorizada por esta decision de prioridad.

**Entrega posterior:** integrar el consumo en RagEngine, seleccionar APIs de negocio
y medir su aporte. Los siguientes apartados 3-9 conservan ese planteamiento y sus
criterios; no son prerrequisitos del servidor compartido. Un gateway disponible
no supone que RagEngine ya lo consuma ni que cualquier API este conectada.

Ampliar RagEngine para responder preguntas con **evidencia del indice, de APIs autorizadas
o de ambas**, sin sustituir su retrieval ni convertir cada pregunta en un agente autonomo.
ContextForge es candidato a proporcionar conectividad MCP, catalogo y controles comunes;
el acceso HTTP directo es el control de comparacion, no una alternativa prohibida.

Casos de entrada aportados por el usuario:

- Un solicitante pregunta por que su ticket aun no es atendido.
- Un agente de soporte pregunta por sus pendientes.
- Una persona solicita informacion que una API permite consultar con su identidad.

Capacidad de producto: **consultas de solo lectura a varias APIs y dominios**, con identidad,
procedencia y estados de fallo explicitos. Tickets es el primer piloto, no la forma del
puerto comun. Activos es un segundo dominio sintetico para demostrar extensibilidad, no
una API empresarial elegida o cuyo contrato ya se conozca. Las operaciones de escritura, asignacion,
cierre, envio de mensajes y ejecucion de comandos quedan fuera. Tambien quedan fuera un
agente general, nuevas bases de datos corporativas y la ingesta automatica de conversaciones
o tickets. Conectores y formatos adicionales se conservan con sus condiciones de entrada.

La preparacion, contratos, fixtures y comparacion se hacen localmente sin IDP corporativo.
El piloto real requiere autorizacion y contratos comprobados; compartir usuarios entre
sistemas **no demuestra** que compartan audiencia de tokens, roles, permisos por objeto
ni capacidades de delegacion.

## 2. Evidencia, fuentes e incertidumbre

Lectura original en `7d5ce88`; contraste actual sobre `c0357d1`. Los cambios concurrentes
del CI (`19.6`, tests y `docs/operaciones.md`) quedan fuera de esta reconciliacion.

| Evidencia por lectura | Consecuencia |
|---|---|
| [RagGenerationService](../../src/RagEngine.Core/Services/Generation/RagGenerationService.cs) recupera chunks, aplica gate y genera contexto; no consulta herramientas operativas. | El enrutamiento y la combinacion de evidencia requieren desarrollo nuestro. |
| [IRagGenerationService](../../src/RagEngine.Core/Abstractions/IRagGenerationService.cs) ya devuelve [GenerationEvent](../../src/RagEngine.Core/Domain/GenerationEvent.cs), con contexto, deltas y resultado; `9.3` esta hecho. | Extender la evidencia existente sin rehacer `9.3`, duplicar retrieval ni disfrazar una respuesta API de chunk con score. |
| El puerto ya exige `RetrievalContext`; su existencia no demuestra delegacion a una API corporativa. | La identidad operativa debe venir del host autenticado, nunca de pregunta, historial o argumentos del modelo. |
| El [analisis de LightRAG](lightrag-rag-anything-y-los-huecos-del-motor.md), secciones 4.2-4.4, describe IdentityServer y permisos corporativos. | Es procedencia de septiembre, no verificacion actual de APIs; `12.2` sigue bloqueado. `12.11`, `13.3` y `13.4` estan hechos y se reutilizan. |
| La propuesta de conversaciones se conserva en `5cc44a5`, diferida segun el documento de integracion. | No depende de implementar memoria ni de portar la ola 22; las respuestas anteriores no acreditan estado actual. |

Fuentes externas consultadas: ContextForge **v1.0.11**, commit
[`077071bbb43599dd5ab9372ebdbb9a8e686a9816`](https://github.com/IBM/mcp-context-forge/commit/077071bbb43599dd5ab9372ebdbb9a8e686a9816).
Es una referencia fijada para evaluar, no una recomendacion de desplegar esa version sin
revisar su mantenimiento y cambios posteriores.

| Fuente oficial | Aporte comprobado documentalmente | Lo que no demuestra |
|---|---|---|
| [Invocacion y validacion](https://github.com/IBM/mcp-context-forge/blob/v1.0.11/docs/docs/architecture/tool-invocation-and-validation.md) | Adaptacion REST y normalizacion de resultados MCP; OpenAPI se transforma en herramientas REST. | Compatibilidad del esquema y errores de nuestra API concreta. |
| [REST passthrough](https://github.com/IBM/mcp-context-forge/blob/v1.0.11/docs/docs/using/rest-passthrough.md) | Mapeos, allowlists y timeouts por herramienta. | Que un mapeo de cabeceras constituya delegacion segura. No copiar ejemplos que reciben secretos como argumentos del modelo. |
| [OAuth](https://github.com/IBM/mcp-context-forge/blob/v1.0.11/docs/docs/manage/oauth.md) y [RBAC](https://github.com/IBM/mcp-context-forge/blob/v1.0.11/docs/docs/manage/rbac.md) | Flujos de usuario/maquina y control sobre recursos/acciones del gateway. | Que sus equipos/roles equivalgan a permisos corporativos sobre tickets. La audiencia derivada puede ser solo orientativa; para el piloto se exige configuracion explicita y validacion por la API destino. |
| [Plugins](https://github.com/IBM/mcp-context-forge/blob/v1.0.11/docs/docs/architecture/plugins.md) y [OpenTelemetry](https://github.com/IBM/mcp-context-forge/blob/v1.0.11/docs/docs/architecture/observability-otel.md) | Puntos de extension y trazas de llamadas. | Instrumentacion interna de RagEngine ni propagacion por todas las variantes de sesiones; la documentacion distingue conexiones agrupadas. |
| [Cache de herramientas](https://github.com/IBM/mcp-context-forge/blob/v1.0.11/plugins/cached_tool_result/README.md) | Cache en memoria con indicacion de hit. | Evitar la llamada: el propio plugin declara que no la corta. |
| [Autorizacion MCP 2025-11-25](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization) | Roles OAuth y autorizacion del transporte HTTP. | Permisos de negocio, API corporativa compatible o acceso autorizado por conocer un numero de ticket. |

No se consultaron tickets, permisos de personas ni endpoints corporativos. Faltan OpenAPI
o contrato equivalente, semantica de estados/pendientes, campos visibles, paginacion,
flujos de delegacion y comportamiento de revocacion. Esas incertidumbres bloquean la
conexion real, no el trabajo local con datos sinteticos.

## 3. Casos y promesas de respuesta

| Caso | Fuente y resultado esperado | Negativo obligatorio |
|---|---|---|
| C1: estado de mi ticket | API: estado, asignacion visible, ultima actividad y hora de consulta. | Ticket ajeno e inexistente no se distinguen por detalles de la respuesta. |
| C2: por que no se atiende | Motivo explicito visible en la API/historial, si existe. Si falta: estado conocido y motivo no disponible. | Antiguedad, prioridad o ausencia de asignacion no prueban desinteres, sobrecarga ni causa de demora. |
| C3: mis pendientes como soporte | Tickets asignados al actor y considerados pendientes por el contrato de la API. | No confundir asignados a mi con creados por mi o pendientes de todo el equipo. |
| C4: mis solicitudes pendientes | Tickets del solicitante autenticado. | Cambiar un nombre en la pregunta no cambia al actor autorizado. |
| C5: estado mas explicacion de procedimiento | API para el estado actual; documento/codigo para explicar la regla general, cada uno con su cita y version. | Una regla del repositorio no prueba que ese binario este desplegado ni por que ocurrio ese caso. |
| C6: API fuera de servicio | Disponibilidad/limitacion explicita; parte documental separada si procede. | Un timeout, 403 o pagina incompleta nunca se convierten en lista vacia o en una respuesta completa. |

Si "mis pendientes" admite mas de una relacion para el actor, pedir que elija
**asignados** o **solicitados** antes de consultar. No inferir permisos ni prioridades
de un titulo laboral. El primer alcance no consulta pendientes de otra persona o equipo.
Anadirlos exige caso y autorizacion especificos, no un parametro `userId` libre.

## 4. Arquitectura propuesta y reutilizacion

```text
Cliente
  -> host RagEngine: autentica y construye contexto autorizado
  -> ruta explicita: indice | operativo | mixto
     -> retrieval actual con ACL del puerto (indice)
     -> puerto operativo acotado (solo lectura)
        -> adaptador HTTP directo, o cliente MCP + ContextForge
        -> API origen: autoridad final sobre registros/campos
  -> evidencia tipada y seleccionada
  -> respuesta determinista de estado/lista, o sintesis mixta acotada
  -> API/UI: fuentes, vigencia, completitud y resultado del turno
```

La base comun representa capacidades registradas, no `InvokeAnyTool`, SQL libre
ni una URL arbitraria. Comparte contexto autorizado, presupuesto, cancelacion, errores
y envoltura de evidencia. Cada conector registra operaciones con esquemas de entrada/salida
y permisos propios; el coordinador no contiene un switch por proveedor ni depende de
`TicketDto`. Un identificador de capacidad solo selecciona una entrada permitida del
catalogo del actor, no permite invocar cualquier herramienta anunciada por un MCP.

Primer catalogo propuesto, **no endpoints de una API ya comprobada**:
`get_ticket(ticketId)` y `list_my_pending_tickets(relation, cursor)`.
La relacion puede ser `assigned` o `requested`; la identidad se resuelve fuera de los
argumentos. El detalle incluye solo el motivo/historial visible necesario, no todo el ticket.
El segundo conector sintetico ofrece `get_asset(assetId)` y `list_my_assets(cursor)`,
con identificadores y campos distintos. Anadirlo solo debe requerir adaptador, esquemas,
registro y pruebas, no editar el coordinador ni el contrato comun. Cada API real futura
necesita paquete aprobado y oraculo propios; tener un conector no autoriza otro dominio.

Separar el contrato de evidencia del adaptador evita acoplar el Core al SDK o esquema de
ContextForge. HTTP directo y gateway deben implementar la misma semantica observable.
No se cambia ONNX, Qdrant, la cache de resumenes ni el esquema del indice.

El primer enrutamiento es **explicito y determinista** (modo/operacion y parametros
validados, con aclaracion si falta un identificador o relacion). No se presupone que el
LLM local soporte function calling fiable. La seleccion autonoma futura necesita su propio
caso y eval; los datos de herramientas nunca pueden solicitar mas herramientas o privilegios.

El modo actual permanece como default. `operativo` no exige Qdrant; `mixto` combina solo
fuentes autorizadas. El gate de similitud decide sobre chunks, no sobre hechos de una API:
un score denso bajo no debe borrar un estado operativo comprobado. No desactivar por ello
el gate ni rebajar sus umbrales. Tampoco ejecutar dos veces retrieval o la API para dibujar
las citas: respuesta y fuentes salen del mismo resultado estructurado (`9.3`).

El modo Simple conserva referencias de ticket/fecha legibles, sin exponer notas internas
ni convertirlas en codigo. API JSON, SSE, UI y CLI reciben estados coherentes. Antes de
abrir SSE, usar errores HTTP conforme al contrato; despues, evento de error y cierre,
sin un `done` de exito ni reintento de fragmentos emitidos. La falta del evento terminal
no acredita una respuesta completa.

Publicar `rag_search` para clientes MCP es otra direccion, condicional (`23.10`).
Separar catalogos de entrada/salida: una consulta enriquecida no puede llamar por gateway
a su propio `rag_ask` ni crear recursion. No exponer ingesta, administracion o listados
globales de colecciones como consecuencia de registrar una herramienta.

## 5. Identidad y permisos de extremo a extremo

Se distinguen tres permisos: **usar RagEngine**, **ejecutar una herramienta** y **leer el
registro/campo en la API origen**. Los tres son necesarios y ninguno sustituye a los otros.

1. Reusar `12.2` para autenticacion corporativa de entrada. Comprobar emisor, firma,
   audiencia, expiracion, identidad estable y organizacion efectiva; un claim ausente o
   ambiguo no concede acceso. Los actores locales sinteticos solo existen en fixtures.
2. El host establece actor y organizacion desde contexto validado. Usuario, prompt,
   historial, cabeceras libres y respuestas de herramientas no pueden alterarlos.
3. Para la API destino usar credencial delegada al usuario y a la audiencia correcta.
   Authorization Code con protecciones aplicables o intercambio de tokens solo si el IDP
   lo soporta y el responsable aprueba el flujo. SSO no implica OBO ni token exchange.
   No reenviar sin mas un token emitido para RagEngine al gateway o a Tickets.
4. `client_credentials` identifica una maquina, no a "mi usuario". No usar una cuenta
   privilegiada global mas un `userId` inventado por el modelo para simular permisos.
   Sin delegacion compatible, mantener el piloto bloqueado o limitar expresamente un
   caso de servicio distinto; no implementar un bypass.
5. La API filtra registros y campos antes de entregarlos al motor. Una denegacion no se
   reintenta con otro usuario, tenant o credencial mas potente. Cambiar de pagina mantiene
   el contexto y vuelve a comprobar el permiso; cursor no equivale a autorizacion.
6. Cada turno nuevo vuelve a consultar para hechos actuales. No hay cache de resultados
   operativos en la primera entrega. Revocacion debe alcanzar las siguientes llamadas
   segun el contrato aprobado: si la API no puede garantizar el comportamiento requerido,
   el piloto no se declara aprobado. No reutilizar hechos del historial como fallback.
7. Tokens/secretos nunca son argumentos del modelo ni contenido de prompts, citas o logs.
   Aplicar los limites de `12.8` y `12.9`. Identidad para correlacion no es autorizacion:
   un `baggage` o `traceparent` recibido no concede acceso.

El paquete de entrada de `23.5` debe especificar por entorno: responsable y version del
contrato; emisor/audiencias y registro del cliente; delegacion, refresh y revocacion;
relacion `sub`/usuario/organizacion; permisos propios/asignados/campos; estados que cuentan
como pendientes; semantica de motivo, timestamps, zona horaria y SLA; paginacion y
completitud; 401/403/404/429, cuotas y esquema de errores. No versionar credenciales ni
ejemplos personales. Los nombres corporativos citados por analisis antiguos no suplen
esta confirmacion.

## 6. Evidencia temporal, limites y fallos

El resultado operativo necesita campos equivalentes a:

| Dato | Contrato |
|---|---|
| Origen | `index` o `api`; proveedor, operacion y referencia autorizada al recurso. |
| Vigencia | `observed_at` UTC de consulta, `updated_at` si la fuente lo declara y revision/ETag si existe; ausente significa desconocido. |
| Contenido | Hechos visibles tipados: estado, relacion, lista, motivo documentado o no disponible; sin score de similitud artificial. |
| Resultado | `success`, `partial`, `not_accessible`, `reauth_required`, `unavailable`, `cancelled` o `unsupported`. |
| Completitud | Paginas/elementos observados y cursor; total solo si la API lo certifica. Exito con cero elementos se distingue de fallo. |
| Procedencia de respuesta | Hechos/citas efectivamente usados, correlacion y ruta de consulta; no copiar todo el payload al log. |

No afirmar "no tienes pendientes" sin una consulta completa y autorizada vacia.
Mostrar una lista parcial como tal, sin un total inventado. Una autorizacion perdida
durante paginacion invalida la respuesta operativa del turno; no mostrar paginas previas
como resultado vigente. Los limites observados nunca se convierten en recortes silenciosos.
Si la API no ofrece una instantanea consistente entre paginas, declararlo; un timestamp
de consulta no la crea. Duplicados o cambios detectados durante el recorrido se senalan
como inconsistencia/parcialidad, no como un total exacto.

**Presupuestos iniciales propuestos para el camino operativo** (decisiones de diseno,
no mediciones): maximo 3 intentos HTTP por turno, incluidos reintentos; maximo 2 paginas,
50 elementos por pagina, 64 KiB descomprimidos acumulados y 5 s de plazo total. Timeout por
intento de hasta 2 s sin exceder el plazo restante. A lo sumo un reintento transitorio,
solo antes de publicar resultados, respetando `Retry-After` sin exceder el plazo.
No hay bucle de herramientas. Exceder bytes/esquema devuelve fallo; paginacion limitada
devuelve `partial`. Si el contrato real no cabe, revisar el presupuesto antes del piloto,
no esconder el exceso. Cancelar debe detener nuevas llamadas.

La cuota de la API, el limite de concurrencia por actor y la cota de contexto total del
modelo son adicionales. El fixture usa concurrencia maxima 2 por actor y 429 al excederla
sin trabajo nuevo. La sintesis debe presupuestar sistema + pregunta + historial + evidencia
y salida reservada para el modelo efectivo. `18.1` aplica si se elige el perfil servidor;
no usar la calibracion de embeddings como prueba del contexto del generador. Cuando no
quepa evidencia, informar limitacion o usar salida determinista, no truncamiento oculto.

Datos de tickets se consideran contenido no confiable, no instrucciones. No seguir enlaces,
URLs o comandos contenidos en ellos; solo referencias formadas/validadas por el adaptador.
No incluir secretos o campos no necesarios para contestar. En preguntas mixtas, distinguir
hecho operativo, regla general y explicacion no demostrada. Fallar la API no se subsana con
una afirmacion vieja del historial o con conocimiento general del LLM.

## 7. Oraculo local y comparaciones antes de adoptar

`23.1` conserva el conjunto sintetico original de **48 casos**, expectativas escritas antes de
implementar adaptadores y hashes congelados. Ninguna pregunta o ancla se obtiene de la
salida que se va a evaluar. Cuatro actores: solicitantes A/B de una organizacion, soporte S
de esa organizacion y solicitante C de otra; ademas peticiones sin actor o de maquina.

| Grupo | Casos | Expectativas |
|---|---|---|
| E01-E08 | 8 de estado/motivo, 4 con motivo visible y 4 sin el | Campos exactos, cita/hora; no transformar un motivo desconocido en causa cierta. |
| P01-P08 | 8 de pendientes propios/asignados | IDs/conteos/relacion exactos, vacio completo, paginacion y orden definido por fixture. |
| M01-M08 | 8 mixtos con documento y dato actual | Dos procedencias, discrepancia temporal visible, sin inferir despliegue o causa del codigo. |
| L01-L08 | 8 controles locales de codigo, prosa y meta-intencion | Ninguna llamada operativa; conservar contrato y resultado base con proveedor determinista. |
| R01-R16 | 16 riesgos enumerados abajo | Rechazo/limitacion esperados, sin filtracion ni falsa completitud. |

Riesgos: R01 sin autenticacion, R02 token expirado, R03 audiencia incorrecta, R04 organizacion
ambigua, R05 ticket de otro propietario, R06 otro tenant, R07 suplantacion en argumentos,
R08 revocacion entre llamadas/paginas, R09 timeout, R10 5xx, R11 429, R12 esquema/bytes
invalidos, R13 cursor repetido o pagina incompleta, R14 instrucciones hostiles en la fuente,
R15 concurrencia con dos usuarios y aislamiento de credenciales, R16 cancelacion/fallo
despues de iniciar respuesta. Cada caso exige aserciones sobre invocaciones y resultado,
no solo texto. Las variantes de riesgo se ejecutan tambien en el adaptador/gateway.
Dentro de cada grupo, mitad ES y mitad EN.

La ampliacion multidominio agrega **D01-D08**, cuatro ES y cuatro EN, sin quitar casos
ni cambiar denominadores o umbrales de E/P/M/L/R. Los ocho casos de activos son: recurso
propio, lista vacia completa, otro propietario, otro tenant, revocacion, indisponibilidad,
esquema distinto/invalido y contenido hostil que intenta seleccionar otra capacidad o URL.
Cada uno fija campos, operaciones, permisos, evidencia y negativas independientes.
Los subcasos R01-R16 aplicables a transporte/identidad se parametrizan sobre **ambos**
conectores. Hay 56 IDs de escenario, mas esos subcasos de riesgo, no 56 sustitutos del original.
La paridad HTTP/MCP incluye ambos dominios y la prueba de alta del segundo sin modificar
el coordinador. D exige 8/8 contratos correctos como condicion adicional de promocion.

Dos comparaciones distintas:

**Aporte funcional (`23.8`).** A = indice solamente; B = indice + fuente operativa simulada
con adaptador HTTP directo. Mismos documentos, preguntas, permisos, reloj, modelo/binario,
prompts de control y configuracion; solo difiere la capacidad operativa. Los nuevos prompts
de B quedan versionados. No esperar que A invente tickets: abstenerse es conducta correcta,
pero no una respuesta factual util. Medir utilidad con evidencia, no llamar recall@10 a
este resultado.

Umbrales de promocion propuestos para B (y para el gateway si se evalua ese brazo):

- 48/48 contratos estructurados correctos y todos los subcasos de permisos/presupuestos.
- De E/P/M, al menos 22/24 respuestas utiles y sustentadas, y al menos 8 mas que A.
  Responder honestamente que el motivo no esta documentado cuenta como util si devuelve
  el estado visible requerido; fallar a proposito no cuenta como una mejora.
- 8/8 controles L sin regresion y cero llamadas operativas. Ejecutar tambien los
  controles de regresion existentes relevantes; no reemplazarlos por estos ocho.
- 16/16 riesgos correctos, cero datos no autorizados, cero causas inventadas y cero
  errores convertidos en exito, incluso fuera del grupo R.

Las respuestas de estado/lista son deterministas. Para el texto mixto, usar rubrica
por afirmacion con su evidencia y evaluacion humana registrada, no un juez LLM como unico
oraculo ni busqueda de palabras como proxy. Persistir respuestas, criterios, fallos y
dictamen de las 48 preguntas; sin la revision requerida no se cierra el experimento.
Separar **corrida valida** (cobertura y evidencia completas) de **promocion aprobada**
(umbrales superados). Un resultado nulo medido puede cerrar la evaluacion con
`no_promover`, nunca habilitar el piloto. Una corrida incompleta no puede cerrarse como
hipotesis nula. No modificar criterios tras ver los resultados.

**Aporte del gateway (`23.4`).** HTTP directo frente a ContextForge para las mismas
operaciones y mocks, sin LLM, IDP corporativo ni datos reales. Exigir 100% de equivalencia
de campos/estados/permisos/paginacion, incluidas respuestas vacias, fallidas y concurrentes.
Demostrar dos catalogos de herramientas distintos y denegacion cruzada: configuracion,
no solo una pantalla administrativa.

Tras 20 llamadas de calentamiento por brazo, medir 200 invocaciones por brazo a
concurrencia 4 con cuatro actores autorizados (uno por llamada simultanea), mismo host
y upstream, intercalando brazos. Los negativos de cuota se miden aparte; no confundir
sus 429 esperados con errores de la carga valida. Publicar p50/p95, errores, RSS y configuracion.
Umbral inicial de elegibilidad: incremento p95 del gateway <=250 ms y cero errores
adicionales en llamadas validas. Es un presupuesto predefinido para este experimento,
no rendimiento garantizado. Medir ambos brazos si cambia version/instrumento.
Ausencia del contenedor o pruebas omitidas es fallo, no resultado nulo.

Si ContextForge preserva contratos y aporta controles/catalogo utiles dentro del presupuesto,
queda elegible **para el consumo de RagEngine**; no es la puerta de instalacion del servicio
compartido. Si no aporta o incumple, conservar HTTP directo para RagEngine y registrar
el resultado negativo sin frenar las capacidades operativas. Un resultado negativo de
rendimiento/valor para RAG no obliga a retirar el servicio a otros consumidores.
Un defecto real de aislamiento/autorizacion se registra y bloquea la capacidad afectada;
no se ignora por separar las entregas ni autoriza a detener servicios ajenos sin aprobacion.
No hacer un fork por defecto.
Si resulta elegible, `23.8` verifica tambien las 48 preguntas pasando por el gateway; una
regresion impide promover ese transporte. No transferir al gateway todo el prompt/historial.
Se ejecutan tambien los ocho D y los riesgos parametrizados en ambos brazos. No se usa
su resultado para compensar fallos de los 48 casos originales.

Costes futuros: fixtures/puertos 0,1-0,3 h maquina por ficha; prueba gateway estimada 0,5 h;
eval funcional hasta 1,5 h y piloto hasta 1,5 h, **no autorizadas por esta reconciliacion**.
Confirmar antes de ejecutar cualquier ficha >1 h y reestimar tras muestra. No se requieren
reingestas ni regeneracion de resumenes.

## 8. Correspondencia con el ledger

La ola 23 ocupa la reserva de la integracion, sin adelantar prioridades de otras olas.
El servidor se ejecuta en el proyecto independiente Mcp.ContextForge (`1.1` -> `1.2` ->
`1.3`); aqui `23.11`-`23.13` quedan bloqueados como referencias externas no ejecutables.
El consumo RAG, incluido `23.14`, se retoma despues. El arranque del proyecto externo
no requiere cerrar el CI `19.6` de este repo; no se cambia ese trabajo ni se mezclan commits.
La ola 22 sigue reservada para conversaciones. `bloqueado_por` sigue siendo un solo ID. Cuando hay requisitos
independientes, el estado es `bloqueado` y su reentrada exige comprobarlos todos: las notas
`_dependencias_adicionales` no son un planificador ni desbloquean automaticamente.

| Capacidad / entrega | Ficha que la gobierna |
|---|---|
| Analisis original / recuperacion / separacion de entregas | `10.5-planteamiento-contexto-operativo-mcp` / `10.7-reconciliar-mcp-multiapi-y-k8s` / `10.8-separar-servidor-contextforge-del-consumo-rag` |
| Oraculo E/P/M/L/R mas D, dos fuentes sinteticas y expectativas | `23.1-oraculo-operativo-local` |
| Puerto multi-API de solo lectura, identidad sintetica y resultado tipado | `23.2-puerto-y-adaptador-operativo-local` |
| Ruta opt-in, respuesta determinista/mixta y superficies API/SSE/UI/CLI | `23.3-rutas-operativas-y-evidencia-mixta` |
| Comparacion directa/gateway, catalogos y decision de transporte | `23.4-comparar-contextforge-con-http` |
| Contrato aprobado de API e identidad delegada | `23.5-contrato-api-y-delegacion-real` |
| Adaptador de tickets real, aun sin habilitarlo a usuarios | `23.6-adaptador-tickets-autorizado` |
| Auditoria y trazas de las operaciones nuevas | `23.7-trazas-y-auditoria-operativas` |
| Evaluacion funcional con fuentes vivas simuladas | `23.8-evaluar-complemento-operativo` |
| Piloto real autorizado, reversible y de solo lectura | `23.9-piloto-operativo-autorizado` |
| Exponer retrieval propio a un cliente MCP autorizado, si hay demanda | `23.10-publicar-busqueda-como-mcp` |
| Paquete Kubernetes del servidor, trasladado | Referencia `23.11-preparar-paquete-k8s`; propietario Mcp.ContextForge/1.1. |
| Contrato de entorno del servidor, trasladado | Referencia `23.12-habilitar-entorno-reyma-dev`; propietario Mcp.ContextForge/1.2. |
| Servir gateway compartido y probar recuperacion, trasladado | Referencia `23.13-desplegar-contextforge-en-reyma-dev`; propietario Mcp.ContextForge/1.3. |
| Validar RAG + MCP + dos APIs sinteticas en el entorno de pruebas | `23.14-validar-rag-mcp-en-reyma-dev` |
| Migracion de RagEngine/Qdrant a Kubernetes, si se decide | `21.4-k8s-de-pruebas`; no se duplica en la ola 23. |
| ACL de colecciones y contexto obligatorio de retrieval | `5.f-revivir-collection-manifest`, `7.b-validar-coleccion-en-la-api`, `9.1-puertos-del-vector-store` |
| Resultado de generacion unico y errores de streaming | `9.3-resultado-estructurado-de-generacion`, `8.e-resiliencia-en-generacion` |
| Identidad, secretos, minimizacion y retencion | `12.2-identidad-y-auditoria`, `12.8-secretos-y-tls`, `12.9-retencion-del-log` |
| Auditoria base, exportadores y spans internos | `12.11-auditoria-local-con-actor`, `13.3-opentelemetry`, `13.4-trazas-del-turno` |
| Consola propia de colecciones, ingestas y evaluacion | `13.6-consola-de-administracion`; la UI del gateway no la sustituye. |
| Contexto de generacion si se usa perfil servidor | `18.1-contexto-efectivo-de-los-prompts-del-motor` |
| Extractores externos, incluido DOCX del ecosistema ContextForge | `15.3-inventario-de-formatos-no-ingeribles`, `15.4-parsing-binario-local`, `15.5-modalidades-y-procedencia` |

No se amplian ni cierran las fichas anteriores por este documento. Sus alcances y
condiciones siguen vigentes. El parser DOCX se conserva como candidato documental, no
se reemplaza la comparacion ya requerida en `15.4` ni se instala su servidor.
Excepcion documental explicita: `21.4` registra que el usuario ya informa un cluster de
pruebas, pero no se desbloquea sin el resto de condiciones. El piloto `23.9` queda al final
del calendario de la ola, despues de `23.14`; sus dependencias son hacia atras.

Las fichas nuevas no publican comandos ficticios. Su preparacion exige construir el
runner apropiado, verificar que selecciona casos y falla si faltan fixtures/infraestructura/
dictamen, y registrar el comando real antes de medir. Tener un oraculo definido y tests
aun inexistentes no bloquea `23.1` ni `23.2`.

## 9. Piloto, rollback y extensiones conservadas

El piloto no se habilita hasta cerrar contrato, adaptador, evaluacion y controles
corporativos. Su responsable debe aprobar actores, entorno, operaciones, datos,
presupuesto y destino del LLM. Empezar con los casos C1-C4, sin ampliar permisos para
obtener mejores resultados. Casos C5 requieren tambien la coleccion autorizada.
Sin un entorno que permita negativos de acceso controlados, el piloto queda bloqueado.

Medir respuestas correctas por campo frente a la API, denegaciones, reautenticacion,
completitud, latencia y utilidad con los usuarios. Verificar al menos dos usuarios de una
organizacion, uno de otra, un soporte, acceso revocado y dependencia caida en infraestructura
de prueba, nunca derribando el servicio compartido. Retencion y limpieza deben tener
propietario; no versionar datos personales para probar que se hizo el piloto.

Rollback: apagar la capacidad operativa y volver al camino del indice; las preguntas
operativas deben informar que la fuente no esta habilitada, no responder con estado viejo.
Desregistrar herramientas/revocar concesiones exclusivas y retirar solo recursos aislados
del experimento. Conservar auditoria segun politica; no borrar registros compartidos ni
restaurar cuentas con mas privilegios. No hay indice que revertir porque los tickets no
se ingieren. Si en el futuro se indexan, se exigira otro contrato de ACL, vigencia,
retencion e indice reversible.

Otros sistemas (activos, pedidos, documentacion, etc.) implementan la misma base comun
cuando exista caso, API autorizada y oraculo propios. Cache operativa, seleccion automatica
de herramientas, A2A y acciones de escritura quedan como posibilidades no habilitadas:
requieren evidencia de necesidad y fichas especificas, no se activan por estar en el catalogo
de ContextForge. La primera meta es **responder mejor con informacion permitida y vigente**,
no convertir RagEngine en una plataforma general de agentes.
Esta es la meta de la integracion posterior de RAG; la entrega inmediata del servicio
compartido tiene aceptacion independiente en la seccion 10.

## 10. Servidor compartido: proyecto independiente Mcp.ContextForge

Por peticion del usuario del 2026-10-07, la preparacion, configuracion y operacion
del gateway pertenecen al repositorio local hermano **Mcp.ContextForge**, no a
RagEngine. Se creo en `D:\\git\\Mcp.ContextForge`, sin remote/publicacion ni despliegue.
No es un fork de IBM: fijara una version/digest upstream y mantendra su paquete operativo.

| Referencia de origen, no ejecutable aqui | Propietario en Mcp.ContextForge |
|---|---|
| `23.11-preparar-paquete-k8s` | `1.1-preparar-paquete-k8s` |
| `23.12-habilitar-entorno-reyma-dev` | `1.2-habilitar-entorno-reyma-dev` |
| `23.13-desplegar-contextforge-en-reyma-dev` | `1.3-servir-contextforge-compartido` |

El avance operativo vive exclusivamente en `docs\\plan\\ejecucion-plan.estado.json`
del nuevo proyecto. Las fichas de origen se conservan bloqueadas y senaladas como
trazabilidad, sin fingir que el traslado las implemento ni duplicar la ejecucion.
Su foto previa se conserva en `05b57ff` y su transferencia se registra en `10.8`.
La definicion normativa del servidor, fuentes upstream y G01-G12 estan en
`docs\\servicio-compartido.md` del destino; aqui no se mantiene otra copia operativa.

El destino gobierna paquete, namespace/recursos, red/TLS, identidad del gateway,
catalogos, persistencia, respaldo/restauracion, actualizaciones y soporte. Debe
probar G01-G12 con clientes reales desde otro equipo y dos consumidores autorizados,
mas los controles de recursos/red; no basta una UI o pods Ready. Su cierre no requiere
RAG, corpus, LLM, contratos de Tickets ni comparacion `23.4`.

### Contrato que recibe RagEngine

Antes de habilitar `23.14`, el mantenedor RAG y operador MCP deben comprobar:

- Repositorio/ID/commit de entrega y evidencia G01-G12/recuperacion del destino.
- Version/digest del gateway, endpoint/transporte MCP, TLS/confianza y ruta privada
  autorizada, sin publicar direcciones internas innecesariamente.
- Identidad y catalogos/capacidades autorizados al consumidor RAG; mecanismo seguro
  de credenciales, expiracion/revocacion, limites y responsable de altas/soporte.
- Topologia de pruebas y autorizacion de carga/fallos: instancia/release/estado
  aislados cuando se prueben caidas, nunca apagar el gateway de otros consumidores.

`bloqueado_por` sigue usando IDs de este ledger; el arnes no resuelve estados entre
repositorios. `23.14` depende de `23.8` y las capacidades RAG, y sigue bloqueado hasta
comprobar manualmente el contrato externo. No se exige marcar `23.13` hecho aqui.
La evaluacion de consumo conserva sus 56 escenarios, subcasos y umbrales.

Retirar el consumo de RagEngine revoca solo sus concesiones/configuracion, no el
servicio compartido. Un resultado negativo en `23.4` no promueve MCP para RAG pero
no decide el servicio a otros proyectos. Un defecto real de autorizacion/aislamiento
se notifica al operador y bloquea la capacidad afectada sin ocultarlo.

La creacion del proyecto no acredita servicio disponible. No se ha conectado al
cluster ni leido kubeconfig/Secrets. Namespace, alcance, recursos e identidades
permanecen por confirmar en el destino. La unica credencial administrativa del
operador no se comparte con consumidores; Headlamp no es dependencia del servicio.
