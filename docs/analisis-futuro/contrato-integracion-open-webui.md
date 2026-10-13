# Contrato de integracion de Open WebUI con RagEngine

Fecha: 2026-10-13. Ficha: `24.2-contrato-de-integracion-open-webui`.
Base local revisada: `5bbd21a` (cierre de 24.1).
Estado: **decision adoptada; contrato no implementado ni desplegado**.

## 1. Decision

Elegir un **Pipe de Open WebUI como modelo dedicado RagEngine**, cliente HTTP
delgado de la API existente. La primera implementacion consumira `/api/ask`
con `responseMode=technical` y mostrara su respuesta y referencias sin una
segunda generacion. No sera un agente que decide si consulta el RAG.
La CLI y la API actuales siguen siendo entradas independientes.

**Desactivar el RAG propio de Open WebUI para ese recorrido**, incluidos
Knowledge, adjuntos documentales y recuperacion implicita. No admitir cargas
ad hoc en el perfil RagEngine. Esto es una decision para la implementacion,
no una afirmacion sobre la configuracion actualmente desplegada.

No adoptar ahora Tool OpenAPI, endpoint compatible OpenAI ni MCP como contrato
principal de este frontend. La Tool Python de 24.1 queda como PoC/control,
no se transforma ni se retira en esta ficha. MCP conserva su caso independiente
en `23.10-publicar-busqueda-como-mcp`; no es prerrequisito del Pipe.

La razon determinante es conservar la autoridad de RagEngine sobre retrieval,
gate, respuesta, ACL y auditoria: la seleccion explicita de un modelo Pipe
evita depender del function calling del modelo local y de una segunda sintesis
que pueda contestar pese a una declinacion. Es una propiedad del contrato
propuesto, pendiente de demostracion extremo a extremo, no una mejora medida.

## 2. Evidencia y limites de partida

| Fuente local | Hecho observado por lectura | Limite |
|---|---|---|
| `poc/open-webui/rag_engine_tool.py`, `Tools`, `rag_ask`, `ensure_collection` | Es una **Workspace Tool Python**, no un servidor OpenAPI. Comprueba `/api/collections`, pide `/api/ask` en modo tecnico y formatea archivos/lineas. | Sus metodos de UI devuelven errores como texto `ERROR:`. El modelo que llama a la Tool puede reinterpretarlo. |
| `poc/open-webui/verify_tool.py` y `cases.json` | El verificador llama a `rag_ask` directamente. 24.1 registra dos corridas 8/8 sobre `micro-repo`, con un modelo 7B. | No prueba seleccion de herramienta, renderizado de citas ni respuesta final en Open WebUI. No se repitieron esas corridas en 24.2. |
| `src/RagEngine.Api/Contracts.cs` | `RagQueryRequest` contiene query, collection, parametros de retrieval, responseMode e history; `RagAskResponse` contiene answer y sources. `SourceDto` distingue citas tecnicas y fuentes redactadas. | El JSON publico no incluye un resultado tipado de declinacion. No inventar ese campo ni inferirlo solo del numero de fuentes. |
| `src/RagEngine.Api/Program.cs`, handlers de consulta | Ya existen `/api/search`, `/api/ask`, `/api/ask/stream` y `/api/collections`, con autorizacion y auditoria en el motor. SSE emite status, sources, token, error y done. | No hay publicacion OpenAPI ni servidor `/v1/chat/completions` en este proyecto API. Su cliente de Ollama no lo convierte en servidor OpenAI. |
| Ledger 24.1 y README del PoC | Registra citas en modo tecnico y una declinacion ES que aun traia fuentes; el 403 no se volvio a probar en ese PoC. | El 8/8 es evidencia historica acotada, no prueba empresarial ni garantia general de no alucinacion. |

El matcher OOC de 24.1 acepta cero fuentes o ciertas frases de declinacion.
Una frase negativa aislada no prueba ausencia de afirmaciones inventadas: no
convertir ese matcher en politica del Pipe ni ampliar retrospectivamente el
alcance de su resultado. Ademas, el codigo actual vacia fuentes cuando recibe
`GenerationOutcome.ModelDeclined`; no es correcto generalizar el comentario del
PoC a "toda declinacion conserva siempre top-K". El adaptador debe conservar
el texto de la API, tanto con fuentes como sin ellas.

El primer parrafo del README del PoC dice que RAG y backends estan desactivados,
pero su comando habilita Ollama y no demuestra desactivacion de Knowledge.
Se registra la discrepancia, sin modificar el PoC ni afirmar estado remoto.
Su imagen `:main` es flotante: **version y digest desplegados no comprobados
en esta ficha**. Antes de implementar se fijaran version/digest y compatibilidad
de plugins en una instancia de prueba; leer documentacion actual no los acredita.

## 3. Un mismo escenario para todas las alternativas

Control: exactamente `poc/open-webui/cases.json` de `5bbd21a`, coleccion aislada
`micro-repo`, mismos datos, parametros/gate y LLM autorizado de hasta 14B
(7B para reproducir el control historico). Ninguna alternativa exige reingesta,
un modelo mayor, otro corpus o acceso directo a Qdrant.

| Casos de 24.1 | Oraculo comun, sin cambios |
|---|---|
| ES1, EN1: roles de tickets | Respuesta no vacia y al menos una fuente cuyo archivo contenga `Asignaci`. |
| ES2, EN2: creacion de tickets | Respuesta no vacia y al menos una fuente con `CreateTicketCommandHandler`. |
| OOC-ES: paella; OOC-EN: capital de Australia | No producir respuesta afirmativa inventada; conservar la declinacion del motor, aunque tenga fuentes. |
| ERR-COLECCION | Coleccion inexistente produce error explicito, nunca respuesta generica exitosa. |
| ERR-API-CAIDA | Dependencia caida produce error explicito, nunca vacio presentado como exito. |

El 403 por coleccion no autorizada es un negativo **adicional**, no un noveno
caso ejecutado por el verificador de 24.1. Su referencia historica son
`CollectionAuthorizationVisibilityHttpHarnessTests` y
`CollectionManifestHttpHarnessTests`; eso no prueba propagacion por Open WebUI.

Comparacion de diseno bajo ese mismo escenario; **no se ejecutaron cuatro
brazos ni se midieron latencias nuevas**:

| Alternativa | ES1/EN1 y ES2/EN2: respuesta y citas | OOC-ES/OOC-EN: gate | ERR-COLECCION/ERR-API-CAIDA y 403 | Resultado |
|---|---|---|---|---|
| Tool OpenAPI | Publicar esquema acotado para ask/search; ask debe usar modo tecnico. El modelo anfitrion selecciona la herramienta y compone la respuesta final, que puede perder o alterar citas. | Un prompt para "usar siempre la herramienta" no obliga a ejecutarla ni a respetar una declinacion; search solo tampoco aplica el gate de generacion. | Contrato HTTP conserva estados, pero hay que comprobar como el cliente y el modelo presentan el fallo. Preflight de coleccion no sustituye ACL. | No elegida para respuesta RAG autoritativa; valida candidata para consulta opcional de un agente futuro. |
| **Pipe dedicado** | Ruta determinista a ask y renderizado de su answer/sources, sin otra llamada generativa en Open WebUI. Referencias visibles en texto aun sin eventos de citas. | Presentar answer sin reinterpretarlo ni responder con conocimiento del modelo anfitrion. No corregir el gate en el adaptador. | Error de transporte/HTTP/esquema es fallo del turno; ausencia en listado es error generico; un 403 de ask sigue siendo denegacion. | **Elegido**: menor superficie nueva para conservar el escenario completo. |
| Endpoint compatible OpenAI | Exige adaptar `/v1/models` y `/v1/chat/completions`, mensajes y respuestas. Las fuentes de RagEngine necesitan convencion propia o referencias en el texto. | Puede enrutar deterministamente a ask igual que el Pipe, sin otra sintesis. | Necesita traduccion de errores al contrato OpenAI y compatibilidad de streaming/cancelacion, ademas del preflight/ACL. | Diferido: buena opcion si aparecen varios frontends compatibles, coste innecesario para uno solo. |
| MCP | 23.10 propone **rag_search**, no ask: devuelve fragmentos, no la respuesta de 24.1. El cliente debe sintetizarla y preservar referencias. Agregar ask ampliaria ese contrato. | rag_search no ejecuta el gate de generacion de RagEngine; no es equivalente a ask. Un agente que lo use no cumple por si solo este escenario. | Probar envoltura de error MCP, limites, autenticacion y cancelacion; nunca convertir fallo en array vacio exitoso. | No elegido; mantener publicacion para otros consumidores bajo las puertas de 23.10. |

La Tool Python actual comparte con Tool OpenAPI el riesgo del modelo anfitrion,
pero no su forma de distribucion: ejecuta Python dentro de Open WebUI, no
descubre un esquema HTTP. El 8/8 de aquella no acredita esta ultima.

### Coste de mantenimiento y riesgos

Escala cualitativa relativa al escenario anterior; no son horas medidas ni un
presupuesto autorizado de implementacion. Responsable del adaptador: mantenedor
RagEngine; configuracion, permisos y actualizaciones: administrador Open WebUI.

| Alternativa | Trabajo inicial y superficie recurrente | Coste relativo y riesgo dominante |
|---|---|---|
| Tool OpenAPI | Esquema publico acotado, operation IDs/DTOs/errores y pruebas de importacion; revisar API y comportamiento de tool calling en cada cambio de cliente/modelo. Puede usar la API existente sin otro proceso si se publica el esquema alli. | Medio. Desacopla Python, pero suma seleccion/sintesis LLM y posible perdida de citas/gate. Elegir conexion administrativa backend, no abrir la API al navegador por conveniencia. |
| Pipe | Un plugin asincrono pequeno: mapeo de mensajes, HTTP, renderizado y errores; pruebas contra la version fijada de Open WebUI y los DTOs de RagEngine. Sin nuevo servicio de inferencia ni protocolo en Core. | Bajo-medio. Acoplamiento a plugins/eventos y ejecucion de Python con permisos del servidor; solo codigo revisado e instalacion administrativa. |
| OpenAI compatible | Fachada versionada, catalogo de modelos, mensajes, capacidades no soportadas, chunks/terminacion y errores; regresion para cada cliente soportado. | Alto inicialmente. Mas reusable, pero riesgo de prometer compatibilidad total y ocultar fuentes o errores en una conversion parcial. |
| MCP | Adaptador de 23.10, SDK/transporte, esquemas/resultados, concesiones y catalogos; gateway solo si su propia evaluacion lo justifica. | Alto para este escenario sin adaptador existente; puede amortizarse entre otros consumidores. Riesgos de recursion, egreso y mezcla de identidades, ademas del LLM anfitrion. |

El Pipe evita *por contrato* una segunda generacion para el turno RAG; esto no
cuantifica ahorro de memoria/tiempo ni elimina tareas auxiliares de Open WebUI.
Deben desactivarse o aislarse titulacion, sugerencias y demas tareas que pudieran
volver a llamar al Pipe/modelo o enviar texto a otro proveedor.

## 4. Contrato elegido, pendiente de implementacion

```text
Usuario selecciona modelo RagEngine en Open WebUI
  -> Pipe asincrono, configuracion administrativa acotada
  -> GET /api/collections (comprobacion de visibilidad)
  -> POST /api/ask (query, collection, responseMode=technical, history)
  -> respuesta + fuentes del mismo resultado
  -> renderizado determinista, sin LLM posterior ni RAG de Open WebUI
```

**Entrada y limites.** URL y coleccion se configuran del lado servidor para el
perfil; no son argumentos que el modelo o un documento puedan cambiar. En el
primer perfil se conserva una coleccion aislada, sin catalogo global publicado
como herramienta. Query es el ultimo mensaje de usuario de texto; history lleva
solo turnos previos user/assistant del chat, sin duplicar query. No reenviar
mensajes system/tool como instrucciones al motor ni mezclar otros chats. El
historial no concede permisos ni sustituye evidencia. Adjuntos, contenido
multimodal y entradas no soportadas se rechazan explicitamente, no se ignoran.
Los limites de tamano/turnos deben fijarse antes de implementar y rechazarse con
error visible; no se introduce aqui un truncamiento silencioso.

**Retrieval y respuesta.** No sobreescribir topK/minScore/rerank ni prompts/gate
para reproducir los casos. No hacer search para reconstruir las citas despues
de ask: podrian pertenecer a otra recuperacion. Mostrar answer tal como se recibe,
incluida una declinacion; sources conserva orden y archivo/lineas disponibles,
sin inventar URLs ni completar campos nulos. Las referencias son datos no
confiables que requieren escape seguro para el renderizador, no HTML ejecutable.
El contenido tecnico puede contener codigo: acceso al modelo/perfil no autoriza
distribuirlo fuera del destino aprobado.

**Presentacion.** Referencias textuales deterministas son el minimo comparable
con 24.1. Los eventos nativos `source`/`citation` son una mejora opcional sujeta
a prueba en la version fijada; no condicionan que se vea archivo/linea. Si no
hay event emitter (por ejemplo, llamada sin contexto UI), la referencia textual
permanece. No afirmar que el 8/8 ya demuestra tarjetas de citas.

**Errores.** Conservar el preflight de coleccion del PoC mientras la API pueda
dar respuesta generica para un nombre inexistente. Ausente/no visible se presenta
sin revelar si existe; autorizar de nuevo en ask es responsabilidad de la API,
pues la visibilidad puede cambiar entre llamadas. Un 401/403, 429, timeout,
error 5xx o JSON incompatible es un fallo distinguible de "sin evidencia".
No responder como exito con `ERROR:` ni mandar el fallo a un LLM para que lo
interprete; usar el mecanismo de error del turno, sin cuerpos internos/secretos.
Nunca reintentar con otra identidad, otra coleccion o acceso anonimo.

**Transporte.** Primera entrega: JSON completo de `/api/ask`; aunque Open WebUI
solicite stream, no fingir tokens en vivo de RagEngine. Establecer timeout total
y cancelar la llamada HTTP al cancelar el turno. El paso a `/api/ask/stream`
requiere alcance posterior: traducir status/sources/token/error/done; sources
puede reemplazarse por vacio, error o EOF sin done no es exito, y no se reinicia
un stream parcialmente emitido. No pasar este SSE propio como si ya fuera SSE
compatible OpenAI.

**Identidad y datos.** El acceso local no equivale a identidad empresarial.
`__user__`, cabeceras de identidad y permisos de Open WebUI no constituyen un
token aceptable para RagEngine. 24.3 y 12.2 deben resolver emisor, audiencia,
grupos/delegacion y negativos antes de multiusuario corporativo; no suplantar
con una cuenta global. No entregar credenciales de Qdrant, cache ni filesystem
al plugin. Preguntas/respuestas almacenadas en chats son otra copia de datos:
su acceso, retencion y egreso requieren control, aunque el indice no se duplique.
El rollback documental de esta ficha no borra chats ni cambia esas politicas.

## 5. Destino del RAG propio y relacion con MCP

Para el perfil dedicado: sin Knowledge, subida/indexacion de documentos, contexto
de archivos, busqueda web ni herramientas que inyecten otra evidencia. RagEngine
es el unico recuperador/generador. Desactivar la ruta requiere verificar las
capacidades/permisos efectivos del modelo y de la instancia en su version
fijada, no asumir que ocultar un boton o quitar un backend LLM desactiva RAG.
Si no se puede aislar por perfil, se necesitara una instancia dedicada autorizada,
no modificar automaticamente la compartida.

Cargas ad hoc quedan **fuera por ahora**, no descartadas permanentemente. Una
futura decision puede habilitarlas en otro perfil claramente rotulado, con
almacenamiento, permisos, retencion y evaluacion propios. No copiar corpus ni
chunks de RagEngine al indice de Open WebUI, ni presentar sus respuestas como
si hubieran pasado el gate/ACL/auditoria del motor.

23.10 publica busqueda **hacia clientes MCP autorizados**; la ola 23 tambien
preve consumir APIs operativas, que es otra direccion. El Pipe no implementa ni
desbloquea ninguna de las dos. Conservar las dependencias 23.4/9.1/9.3/12.2 y
la aprobacion de destino/actores de 23.10. No llamar desde RagEngine a su propio
rag_search/rag_ask por un gateway; no exponer ingesta, administracion ni inventario
global como consecuencia de conectar un frontend. Open WebUI documenta MCP
Streamable HTTP nativo; no se presupone que todo MCP necesite mcpo/ContextForge
ni que esa capacidad exista en la imagen desplegada.

## 6. Aceptacion documental, trabajo posterior y rollback

| Criterio literal de 24.2 | Evidencia en esta decision |
|---|---|
| Alternativas con el mismo escenario de 24.1 | Seccion 3: mismos ocho casos y oraculos; cuatro alternativas, incluida Tool OpenAPI contra Pipe, OpenAI y MCP. |
| Coste de mantenimiento y riesgos | Tabla de superficies y costes relativos; limites de LLM, plugins, protocolo, identidad y version. Sin atribuir cifras medidas. |
| Decidir contrato y destino del RAG propio; resolver solapamiento | Secciones 1, 4 y 5: Pipe, sin RAG propio/ad hoc en ese perfil, 23.10 independiente. |
| No implementar el contrato elegido | Solo este documento y la ficha 24.2 del ledger; ningun plugin, endpoint, contenedor o indice cambiado. |

Esta aceptacion es una revision documental contra fuentes; no se sustituye por
un build, busqueda de palabras o validacion JSON. La validacion de cierre del
arnes comprueba la ficha, no ejecuta los casos ni demuestra viabilidad runtime.

**Trabajo posterior no ejecutado:** preparar una ficha propia de implementacion
con version/digest, limites de entrada/timeout, alcance local y rollback del
plugin/configuracion. No queda implicitamente incluido en 24.3 ni autorizado
por cerrar 24.2. Reusar el fixture sin cambiar corpus/gate y comprobar los ocho
casos en el cliente **y en la respuesta visible de Open WebUI**, mas 403 y
negativos de cancelacion, formato invalido, contexto ajeno/adjuntos y tareas
auxiliares. Contrastar answer y fuentes con el resultado de la misma llamada,
no exigir igualdad entre dos generaciones no deterministas. Las declinaciones
y errores deben permanecer tales en pantalla; verificar que ningun segundo
LLM/recuperador intervino. No cerrar implementacion solo con el verificador Python.

24.3 sigue bloqueado por identidad y aprobaciones; 24.4 por destino de telemetria
autorizado. El trabajo 19.6 en curso permanece intacto por la peticion explicita
de priorizar esta ficha. No se conecto a la Mac, Kubernetes, Ollama ni Qdrant.

**Rollback de 24.2:** revertir su commit (documento y cambio de su ficha), conservando
24.1 y todo historial previo. No borrar volumenes/chats, desinstalar Open WebUI
ni tocar el indice. La futura implementacion necesita su propio rollback.

## 7. Fuentes oficiales consultadas

Consulta: 2026-10-13. Documentacion web mutable, no garantia de compatibilidad
con el contenedor del PoC. Se distinguen sus capacidades documentadas de nuestra
decision; no se ejecutaron ejemplos ni se instalaron plugins externos.

- [Taxonomia de Tools](https://docs.openwebui.com/features/extensibility/plugin/tools/):
  Workspace Python, OpenAPI y MCP son integraciones distintas; tool calling
  depende del modelo.
- [Servidores OpenAPI](https://docs.openwebui.com/features/extensibility/plugin/tools/openapi-servers/open-webui/):
  esquema HTTP y distincion entre conexiones desde navegador y backend.
- [Pipe Functions](https://docs.openwebui.com/features/extensibility/plugin/functions/pipe/):
  modelo personalizado, entrada de chat, asincronia, retornos y manejo de error;
  plugin ejecuta Python en el servidor.
- [Eventos de plugins](https://docs.openwebui.com/features/extensibility/plugin/development/events/):
  eventos de estado y source/citation; requieren contexto y compatibilidad UI.
- [MCP nativo](https://docs.openwebui.com/features/extensibility/mcp/):
  Streamable HTTP desde v0.6.31; conectar una herramienta no asegura que el modelo
  la use bien ni valida permisos de la API destino.
- [Open WebUI](https://docs.openwebui.com/):
  soporta proveedores compatibles OpenAI; ello no adapta automaticamente
  `RagAskResponse` a Chat Completions.
- [Imagenes y versiones](https://docs.openwebui.com/getting-started/quick-start/):
  `main`/`latest` son imagenes flotantes; fijar version para reproducibilidad.

Procedencia local adicional:
`docs/analisis-futuro/contextforge-contexto-operativo-autorizado.md`, secciones
4-5, y fichas 23.10/24.1/24.3/24.4 del ledger. Esas fichas y sus resultados
historicos no se reescriben como parte de esta decision.
