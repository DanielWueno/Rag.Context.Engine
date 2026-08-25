# Instrumentación de las consultas del piloto, y feedback del usuario (copiar / like / dislike)

## Cómo nació este documento

Arrancó como "añadir copiar y pulgar arriba/abajo al chat, porque el pulgar es la vía más barata
de convertir uso real en datos etiquetados". La primera versión de este documento defendía eso
en cinco ítems.

Se midió antes de implementar, y la medición refutó la premisa central. **Lo que sigue no es el
plan original corregido: es el orden invertido.** El cuello de botella del piloto no es la falta
de señal humana — es que el instrumento que ya recoge señal está tirando el dato a la basura.
El feedback UI sigue en el plan, pero al final y condicionado.

Se conserva a propósito el registro de lo refutado (§ "Lo que se cayó"), porque el error de la
primera versión es de un tipo que este repo ya cometió antes.

## Lo medido, antes de proponer

Sobre los 27 ficheros `logs/rag-api-*.json` (2026-07-21 a 08-25):

| Métrica | Valor |
|---|---|
| `QueryEvent` de tipo `ask` | **836** |
| De ellos, en los 2 días de corridas del arnés (08-07: 230, 08-21: 244) | 474 (57%) |
| Asks fuera de días de arnés (tráfico "de humano") | **362** (~15/día) |
| Preguntas distintas | **218** |
| Preguntas hechas una sola vez | **101** |
| Modo: Simple / Technical / sin declarar | **646 / 34 / 156** |
| Ítems de eval que existen hoy | **61** (innovapp-docs 26, tickets-microservice 19, bsuite-auditorias 16) |
| Preguntas reales ya cosechadas de los logs en `replicate-env/data/questions/` | **415** (bsuite-repo 230, innovapp-docs 115, wiki-solis 70) |

Y el dato que ordena todo lo demás — **qué guarda el log sobre los chunks recuperados**:

| Modo | Fuentes logueadas | Con `File` no-nulo |
|---|---|---|
| Simple | 4.400 | **131** |
| Technical | 265 | 265 |
| sin declarar | 1.123 | 1.123 |

En el modo que usa el 95% del tráfico real, **el log guarda un score y nada más**.

## Hallazgos verificados

### 1. El log no registra qué se recuperó, justo en el modo que domina el tráfico

La proyección de los tres endpoints que loguean es
`sources.Select(s => new { s.File, s.Section, s.StartLine, s.EndLine, s.Score })`
(`Program.cs:364`, `:450`, `:587`). `sources` son `SourceDto`, no `RetrievalResult`, y en
`ResponseMode.Simple` el DTO se construye con `SourceDto.Redacted`, que pone `File`, `Section`,
`StartLine` y `EndLine` en `null` (`Contracts.cs`). El front manda Simple por defecto
(`index.html:276`: el toggle solo se marca si `localStorage` ya dice `technical`).

**La redacción existe para proteger al cliente** — para que un lector no técnico no confunda una
ruta de archivo con la respuesta. Se está aplicando también al **log**, donde no protege a nadie
y destruye la única información que permite auditar el retrieval a posteriori.

Peor: cuando `ShouldSuppressSources` corta (meta-pregunta, banda baja) `sources` queda en `[]`
(`Program.cs:429`, `:524`) y el log no registra **ninguna** fuente, ni siquiera el score — aunque
el retrieval sí corrió y sí devolvió chunks.

Consecuencia directa: **la afirmación "la tubería de persistencia ya existe, solo falta una llave
de join" era falsa.** No hay nada a lo que hacer join para el 95% del tráfico.

### 2. La llave de correlación ya existe; no hay que inventar ninguna

Cada línea del log ya trae, puestos por el `CompactJsonFormatter` y el middleware de ASP.NET:
`@t`, `@mt`, `@tr`, `@sp`, `TraceId`, `SpanId`, `ParentId`, `ConnectionId`, `RequestId`,
`RequestPath`. Verificado sobre los `QueryEvent` reales.

`@tr` / `TraceId` es único por request. Un `queryId` GUID nuevo sería el **cuarto** identificador
de un sistema que ya tiene tres desconectados — hay además un `correlationId` propio generado en
`QdrantSemanticRetriever.cs:55`. Lo único que falta es **emitir al front el que ya existe**.

### 3. El log se borra solo, y con él el activo más valioso del piloto

`rollingInterval: RollingInterval.Day` + `retainedFileCountLimit: 30` (`Program.cs:48-49`), sin
override: no hay sección `Serilog` en `appsettings.json`.

Son **30 archivos, no 30 días**: solo rota los días con actividad. Al ritmo medido (27 archivos
para 35 días de calendario) eso son ~39 días reales — pero en cuanto el uso sea diario, son 30.
Tampoco hay `fileSizeLimitBytes`, y un día de arnés llega a decenas de MB.

O sea: **las 218 preguntas reales distintas que el piloto ya produjo están en cola de borrado**,
y son el insumo del ítem `5.0` del ledger, que bloquea las olas 5 y 6.

### 4. `ContentHash` es estable; `ChunkId` no

| Campo | Derivación | ¿Estable? |
|---|---|---|
| `ChunkId` | `DeterministicGuid.CreateForChunk(artifact.AbsolutePath, startLine, hash)` — `ChunkBuilder.cs:86` | **No.** Ruta absoluta + línea de inicio |
| `ContentHash` | `SHA256(content)` — `ChunkBuilder.cs:82`, `ContentHasher.cs:16` | **Sí.** Solo contenido |

Se verificó el punto que podía tumbar esto: **el `Content` que se hashea no lleva cabecera con
ruta ni con líneas en ninguna de las 4 estrategias de chunking.** La cabecera (`HeaderPrefix`, con
repo y ruta relativa, y en Fallback además `// Lines: N-M`) va solo en `EnrichedContent`. El caso
sospechoso era Markdown, donde `content == enrichedContent`, pero su cabecera es
`[Sección: ...]`, sin ruta ni líneas.

Importa porque el ítem `8.f-identidad-de-chunk-sin-ruta-absoluta` del ledger, ya pendiente, **va
a cambiar el `ChunkId` de todos los chunks del corpus**. Su propio campo `rollback` lo corrobora:
"no toca content, así que la caché de resúmenes acierta al 100%".

Dos límites que la primera versión omitió:

- **La PK del `SummaryCache` es `(content_hash, prompt_version)`** (`SummaryCache.cs:54`), no
  `content_hash` solo. El join contra el resumen de negocio se rompe si cambia
  `resumen_prompt_version`.
- `ContentHash` identifica **contenido, no ubicación**: dos chunks de texto idéntico colisionan.
  Cuántos hay es medible y no se ha medido (ver criterio del ítem 2).

### 5. Con `rerank=true` se pierde el score de retrieval — y el chunk expulsado no se loguea

`OnnxCrossEncoderReRanker.cs:87` devuelve `candidate with { SimilarityScore = score }`: el score
de fusión RRF se sobrescribe con el sigmoide del cross-encoder. El front manda `rerank: true`
fijo en las dos llamadas (`index.html:444`, `:493`), así que el 100% del tráfico del piloto tiene
score de cross-encoder y cero información de qué había encontrado el retrieval.

**Y el caso que más interesa medir no es capturable por esta vía.** El reranker hace `.Take(topK)`
sobre un pool de `TopK*3`: un chunk que estaba en el top-k pre-rerank y que el rerank expulsó
**no está en la lista devuelta**, así que no se loguea, por muchos campos que se añadan.

Ese daño **ya se puede medir hoy, con cero código**: `rag eval --json` emite `hit_any_at_k` por
pregunta (`EvalCommand.cs:435`) y existen baselines `*.norerank.baseline.json`. Dos corridas y un
diff dan el número. Es un experimento, no una feature.

### 6. Los ranks por banda solo existen en la rama de 3 vectores

`FuseWithRrf` calcula `denseRank`/`sparseRank`/`resumenRank`
(`QdrantSemanticRetriever.cs:213-215`, aplicados en `:227-229`) y los descarta. Pero esa función
solo corre si la colección tiene el tercer vector `dense-resumen` (`:95`); para las de 2 vectores
la fusión es `Fusion.Rrf` **nativa de Qdrant** dentro de un solo `QueryAsync` (`:118`), y los
rangos por rama se calculan en el servidor y no vuelven.

Cobertura real: `bsuite-repo` y `bsuite-auditorias-test` tienen resumen; `wiki-solis`,
`rag-engine`, `innovapp-docs` no. Provenance por banda sería parcial por diseño. **Sale del plan
como código de producción** (ver § "Lo que se cayó").

### 7. `navigator.clipboard` no funciona en este host desde la LAN

La API escucha en `http://0.0.0.0:5080` (`Program.cs:114`, condicional a que `urls` y
`ASPNETCORE_URLS` estén vacíos), y nunca HTTPS en ninguna configuración. Los navegadores exponen
`navigator.clipboard` solo en *secure context*: HTTPS o `localhost`.

El botón de copiar funcionaría en la máquina del dev y fallaría en silencio para exactamente los
usuarios para los que se hace. Necesita fallback por `textarea` + `document.execCommand('copy')`,
y la verificación tiene que hacerse **desde otra máquina por `http://`**.

### 8. Un like NO produce un ítem de eval válido, por construcción

El runner puntúa con `item.TargetContentContains.Any(anchor => window.Any(h => h.Content.Contains(anchor, Ordinal)))`
(`EvalCommand.cs:334-335`), tras filtrar por nombre de archivo (`:331`). Y
**`TargetSectionHeader` está declarado (`:415`) y nunca se usa** en la evaluación.

Si el anchor se copia del chunk top-1 que el retrieval devolvió, **el ítem pasa siempre, por
construcción, y mide cero**. El ground truth tiene que salir del documento fuente elegido
independientemente de lo que el retrieval devolvió. Y `Category` — el campo que produce el "12% en
parafraseada" que motivaba el plan original — solo lo puede asignar un humano.

Nota sobre ese 12%: la categoría `parafraseada` de `innovapp-docs` tiene **8 preguntas**. Es
1 acierto de 8, error estándar ~12pp. La primera versión de este documento lo citaba como "la
categoría más débil medida" sin decir el n.

### 9. No se puede saber cuántos humanos usan el piloto

El `QueryEvent` no lleva IP, sesión ni usuario. Las preguntas más repetidas son
"El plan de auditoría se genera en automático…?" ×26, "Cómo funciona la encuesta de satisfacción?"
×25, "¿Qué reglas de validación tiene ServicioCliente?" ×25 — patrón de alguien probando el motor,
no de un equipo trabajando. **No es que el piloto no tenga usuarios: es que no hay dato que
permita afirmar que los tiene.**

Y de eso depende todo el ROI del feedback UI: a 15 asks/día con una tasa de clic de 1-5%
(el rango habitual en herramientas internas) salen **2 a 22 pulgares al mes**. Para los ~100 likes
triados que harían falta para 40 ítems de eval: entre 1 y 3 años.

## Ítems propuestos

Orden deliberado: **el instrumento primero, el feedback al final y condicionado.**

### Ítem 1 — Sink durable del `QueryEvent` + id de sesión

Lo más barato del documento y lo que desbloquea el resto.

- Sink Serilog propio `logs/rag-queries-.jsonl` **sin `retainedFileCountLimit`**, con solo los
  `QueryEvent`, separado del log operativo que sí debe rotar. Con `fileSizeLimitBytes` para que un
  día de arnés no llene el disco.
- Un id de sesión generado en el cliente y guardado en `localStorage`, enviado en el request y
  registrado en el `QueryEvent`. Un campo. Sin él no se puede calcular ninguna tasa de clic ni
  distinguir "5 pulgares de 5 personas" de "5 pulgares del dev".
- **Decisión explícita de retención, no tomada de pasada:** "sin retención" significa conservar
  indefinidamente las preguntas del equipo. Hay que escribir quién es el dueño del fichero, y que
  las corridas del arnés se marquen (`Source: "arnes"`) para poder excluirlas — hoy son el 57% de
  los asks y contaminan cualquier estadística de uso.
- **Verificación:** los 27 ficheros actuales se conservan; tras N días con el sink nuevo, el conteo
  de `QueryEvent` en `rag-queries-*.jsonl` coincide con el del log operativo; una corrida del arnés
  queda marcada y excluible con un filtro `jq`; y el id de sesión distingue dos navegadores
  distintos sobre la misma máquina.
- **Rollback:** quitar el sink. Es aditivo — el log operativo sigue igual y los datos capturados no
  estorban.

### Ítem 2 — La identidad del chunk recuperado, en el log, independiente del `ResponseMode`

Este es el ítem que arregla el hallazgo 1, y **no es "añadir dos campos"**: hay que cambiar la
forma del bloque para que la proyección salga de los `RetrievalResult` (`retrievedSources` /
`results`), no de los `SourceDto`.

- Bloque nuevo `Retrieved` en el `QueryEvent`, con `ContentHash`, `ChunkId`, `RelativeFilePath`,
  `MethodName` y score, **siempre**, con independencia del `ResponseMode` y **también cuando
  `ShouldSuppressSources` vacía las fuentes del cliente**. El bloque `Sources` existente se
  mantiene: registra qué vio el usuario, que es una pregunta distinta de qué se recuperó.
- `SourceDto` **no cambia**, así que no se filtra nada nuevo al cliente y el modo Simple sigue
  intacto. La distinción es log ≠ respuesta.
- Se añade también el score **pre-rerank** y el rango pre-rerank, que hoy se destruyen
  (hallazgo 5). Sobreviven al reranker sin trabajo extra porque usa `candidate with { ... }`.
  Nota de build: `RetrievalResult` se construye en 3 sitios, y `poc/…/RecallEvaluator.cs:231` usa
  argumentos posicionales y **está en `RagEngine.slnx`** — los campos nuevos van como
  `{ get; init; }` en el cuerpo del record, no posicionales, o se rompe el build.
- **Verificación:** tras el cambio, el 100% de los asks en modo Simple registran `ContentHash` de
  todos los chunks recuperados (hoy: 131 de 4.400 tienen ni siquiera la ruta). 20 `ContentHash`
  logueados de ≥2 colecciones resuelven a su chunk por consulta a Qdrant, 20/20. **Escenario de
  riesgo real:** re-ingestar una colección chica desde otra ruta y confirmar que `ContentHash` no
  cambia mientras `ChunkId` sí — exactamente lo que hará `8.f`. **Y medir las colisiones**: contar
  `content_hash` duplicados por colección, para saber si el análisis de chunk-imán es posible con
  esta llave o no.
- **Rollback:** quitar el bloque `Retrieved`. Aditivo sobre JSONL: las líneas viejas y nuevas
  conviven sin migración.

### Ítem 3 — Minar lo ya capturado, en vez de esperar señal nueva

Esto es el ítem `5.0` del ledger, ya priorizado allí, y produce **hoy** el output que el feedback
produciría en 2027.

- Las 415 preguntas de `replicate-env/data/questions/` no tienen ground truth (`{query, topK,
  minScore, rerank, responseMode, historyTurns, sourceLog}`) y ese formato `rag eval` no lo
  deserializa. Falta la conversión y el etiquetado.
- Herramienta de triaje: un comando que muestre pregunta + top-k + fragmento y escriba el ítem
  candidato en formato eval-set. **Sin esto, cualquier captura de señal se acumula en un JSONL
  que nadie abre** — el destino que tuvo el `SourceContext` de OTEL antes del ítem 3.3.
- **El anchor tiene que salir del documento fuente, no del chunk recuperado** (hallazgo 8), o el
  ítem mide cero.
- **Verificación:** los eval-sets pasan de 61 ítems a ≥120, con `parafraseada` en n≥25 en vez de
  n=8. Y `bsuite-repo` deja de ser un eval-set inexistente que los documentos citan como si
  existiera.

### Ítem 4 — Copiar (solo front)

- **Copiar** (solo el texto de la respuesta). Se cuelga en `renderChatLog()`, que ya re-pinta cada
  turno desde `chatHistory`.
- **"Copiar con fuentes" NO se ofrece en modo Simple.** Pegaría rutas y secciones que el proyecto
  se pasó una fase entera redactando (`SourceDto.Redacted`, `ShouldSuppressSources`), y Simple es
  el 95% del tráfico: sería una vía nueva para que soporte pegue `ServicioCliente.cs:41-45` en un
  chat con un cliente. En Technical sí.
- **Decisión a tomar antes de escribir el criterio:** ¿se copia el markdown crudo o el texto
  renderizado? Para el destino real (Teams, correo) es el renderizado — y entonces "comparar byte
  a byte contra el campo `Answer` del log" **falla por diseño**. El criterio depende de esta
  decisión.
- **Verificación:** desde **otra máquina de la LAN por `http://`** — no en `localhost`, el único
  sitio donde el camino que falla parece funcionar. El fallback `execCommand` se ejercita de
  verdad.
- **Rollback:** quitar el botón.

### Ítem 5 — Pulgares, condicionado y con criterio de muerte pre-registrado

**No se empieza hasta que el ítem 1 lleve datos suficientes para saber cuántos humanos hay.**

- `POST /api/feedback` con `{traceId, verdict, comment?}` — usando el `TraceId` que **ya existe**
  (hallazgo 2), no un GUID nuevo. Línea en el sink del ítem 1; el `QueryEvent` ya es durable, así
  que basta el puntero: no hace falta duplicar pregunta/respuesta ni mantener estado en el host.
- Llave de unicidad (un veredicto por `TraceId`, último gana), tope de 2 KB al comentario validado
  en servidor, rate limit, y **flag `EnableFeedback` por `IOptionsMonitor`** para apagarlo con un
  restart de contenedor sin rebuild, con `Warning` al arranque si está apagado — el patrón que ya
  usa este repo.
- **Aviso de privacidad en el front**: qué se guarda y quién lo ve. El host no tiene auth: en la
  LAN, un comentario tipo "esto siempre lo hace mal X" es visible para cualquiera con acceso.
- **Riesgo de calidad de datos, sin solución limpia:** el usuario de soporte en modo Simple —el
  perfil dominante— muchas veces no puede verificar si la respuesta es correcta. Un like suyo es
  un falso positivo que entraría al eval-set como ground truth, contaminando el instrumento con
  el que se mide todo lo demás. Mínimo: los likes de modo Simple se marcan como clase distinta y
  no se promueven a ítem de eval sin verificación contra la fuente.
- **Qué pasa si el POST falla** (sin red, 429): el pulgar no puede pintarse como guardado. Con
  volúmenes de 5/mes, perder 2 en silencio es la mitad del dataset.
- **Criterio de muerte, escrito ANTES de implementar:** si a las 4 semanas con los botones
  desplegados hay **menos de 15 pulgares de ≥2 sesiones distintas**, la hipótesis "el equipo
  califica si le das el botón" queda refutada, se apaga el flag y se abandona la vía UI en favor de
  la minería de logs del ítem 3. Sin este número escrito de antemano, en tres meses habrá 6
  pulgares y alguien propondrá "hay que promocionarlo más".
- **Rollback:** apagar `EnableFeedback`. Los datos capturados quedan.

## Lo que se cayó de la primera versión, y por qué

| Se proponía | Por qué se cae |
|---|---|
| "La tubería ya existe, solo falta la llave de join" | El log no registra la identidad del chunk en el 95% del tráfico (hallazgo 1). Era la premisa central |
| `queryId` GUID nuevo por request | `@tr`/`TraceId`/`RequestId` ya están en cada línea (hallazgo 2). Sería el cuarto identificador |
| Anillo de 500 entradas en memoria (D2) para líneas autocontenidas | Nace de asumir que la caducidad del log es física. Es configuración: con el sink durable del ítem 1 el problema desaparece, y con él el estado en un host stateless, la degradación `resolved:false` y su criterio. Además el anillo se dimensionaba en entradas y **una corrida del arnés (244 asks) lo vaciaba de tráfico humano** |
| Ranks por banda como campos de producción | Cobertura parcial por diseño (hallazgo 6) para contestar una pregunta puntual de análisis. Va a **script offline**, que además puede lanzar las dos ramas por separado y cubrir el 100% de las colecciones |
| Criterios "sobre el eval-set completo de bsuite-repo (89 preguntas)" | **Ese eval-set no existe.** Hay 61 ítems en 3 sets, ninguno de bsuite-repo. Las 89 son del arnés de calidad, que no mide recall — y el ledger ya documenta esta corrección textualmente en `5.0` y en `_reconciliacion`. El documento reincidió en el error que el repo ya tenía escrito |
| "Un like es casi un positivo etiquetado gratis" | El anchor copiado del top-1 recuperado pasa siempre por construcción (hallazgo 8). El ítem de eval mediría cero |
| "El feedback es la vía más barata al fraseo real del usuario" | El fraseo real **ya se cosecha del log**: 415 preguntas en `replicate-env/data/questions/`. El pulgar añade el veredicto, no el fraseo |
| "12% recall en parafraseada" como hecho establecido | Es 1 de 8. La primera versión omitió el n |
| Los 5 ítems "son independientes, ninguno bloquea a otro" | Falso: los criterios de los ítems 4 y 5 originales dependían de un eval-set inexistente, y el de ranks por banda dependía de un score de fusión que el rerank ya había destruido |

## Lo que este plan NO resuelve

- **No hay autenticación**, y este plan no la agrega. Cualquiera en la LAN puede consultar y
  escribir feedback. Aceptable para un piloto interno; **no** si esto sale de la LAN/VPN.
- **No hay política de borrado.** El ítem 1 decide conservar preguntas indefinidamente y el ítem 5
  añade texto libre. Falta el comando de borrado por sesión o por `TraceId`, y quién lo ejecuta.
- **No mide si el feedback valió la pena** — pero a diferencia de la primera versión, el criterio
  de muerte del ítem 5 está pre-registrado en vez de diferido.
- **No toca el ranking ni los pesos.** Cambiar `sparse=1.3` / `resumen=2.5` o la política de
  `rerank=true` por defecto con base en lo capturado es trabajo posterior, y conecta con `7.c`
  (que está bloqueado por `4.2`, porque apagar el rerank cambia qué consultas caen en banda baja).
