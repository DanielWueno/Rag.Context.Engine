# LightRAG / RAG-Anything frente a Rag.Context.Engine

> Evaluación de las dos propuestas externas (HKUDS) como base o como fuente de ideas, y
> auditoría de los huecos del motor actual en seguridad, estabilidad, visibilidad y proceso.
> Fecha: 2026-09-04. Rama: `feat/rag-api-selector-coleccion`, commit `03db9c2`.

> **Reconciliación documental del 2026-09-12:** las propuestas de §7 y las accionables
> de §4–5 están incorporadas/absorbidas en `ejecucion-plan.estado.json`, con
> correspondencia completa en `_trazabilidad_propuestas`. No significa implementación
> ni nueva verificación de los hallazgos fechados. Manda ahora la prioridad local:
> 11.1 → 11.2; el experimento 5.h queda desactivado y bloqueado por calidad, sin impedir
> medir truncamiento. Identidad corporativa y cómputo remoto siguen pendientes sin bloquearlo.
> El ledger corrige el traspaso (rechazo 400, no clamp; misses de caché, no 19 h fijas;
> margen en preguntas, no ±1 pp; longitud entrenada antes de ampliar ventana).
> El análisis histórico y su orden recomendado se conservan como procedencia, no como
> instrucciones para abrir puertos o migrar servicios en el alcance actual.

## Veredicto

El objetivo declarado es un despliegue empresarial multiempresa. Todo lo que sigue se
evalúa contra eso, no contra el uso local de hoy: un motor que sirve consultas sobre
corpus de auditoría sin saber quién pregunta no está "todavía sin autenticación", está
generando un vacío de registro que no se podrá reconstruir.

1. **No adoptar LightRAG ni RAG-Anything como base.** Son Python; el motor es .NET 10 con
   chunkers de Roslyn/TypeScript estrictamente mejores para código que las cuatro
   estrategias de LightRAG, y migrar tiraría la red de seguridad completa (golden masters,
   eval-sets con procedencia, contrato de escala de score, ledger de 10 olas). Lo que
   LightRAG tiene y aquí falta se **añade**; no exige rebase.

2. **En la capa operativa, LightRAG sirvió para poner el requisito sobre la mesa, no como
   modelo.** Su autenticación (API key o usuario/contraseña con bcrypt) y su aislamiento
   (por directorio de trabajo, que su propio README admite que es configuración y no roles)
   están por debajo de lo que ASP.NET Core da de fábrica y muy por debajo de lo que la
   empresa ya opera. Lo que sí vale copiar es su **arquitectura de cuatro roles de
   almacenamiento** (§4.7), que es la forma-objetivo de la Ola 9.

3. **El modelo de autorización no hay que diseñarlo: ya existe.** El IDP es el
   IdentityServer centralizado que ya usa `Reyma.InnovApp` —no la autenticación interna de
   XAF, que queda fuera— y emite scopes con la convención `rym.<área>.<sistema>.<verbo>`, un claim
   `organizational` para la dimensión de empresa, y tiene un `permissions.service`
   centralizado. La consecuencia (§4.3) es que **la ACL de una colección puede exigir el
   scope del sistema que indexa** —el índice del monolito de tickets exige
   `rym.ti.tickets.read`— de modo que la autorización del motor queda derivada de la de los
   sistemas indexados en lugar de ser un sistema paralelo. Eso abarata el aislamiento entre
   empresas hasta dos campos en un record que ya existe, dentro de un ítem ya planificado
   (5.f).

4. **Lo que decide el orden no es la importancia, es la reversibilidad** (§4.1). Hay cosas
   que no son más caras después sino imposibles: la identidad en el registro de auditoría y
   la historia de ingestas. Son también de lo más barato de construir. La consola de
   administración, que es lo genuinamente pesado, es lo único que se puede posponer sin
   pagar intereses — y además no se puede construir antes, porque los datos que mostraría
   todavía no se guardan.

5. **Hay un truncamiento silencioso medido, pero su efecto sobre el recall está sin
   demostrar.** El **54,6% del texto indexado de `bsuite-repo` nunca llega al encoder
   denso** (tokenizador real, 2.000 puntos). Que sea la causa del recall@10 = 40% **no está
   probado**: un PoC previo probó `MaxSequenceLength=512` y no movió nada, aunque midiendo
   desde un suelo del 0% en otro corpus. El experimento que decide está en §1.1.1 y cuesta
   ~1,5 h.

6. **Hoy el stack está abierto en la LAN.** Qdrant en `0.0.0.0:6333` sin API key —
   verificado leyendo 2.000 payloads con código fuente completo, sin credencial— la API sin
   autenticación en `0.0.0.0:5080` por HTTP en claro, y `TopK` sin cota, que con el
   `rerank=true` por defecto convierte un request de 200 bytes en minutos de CPU. Eso se
   cierra en horas y va primero (§8).

---

## 1. Evidencia medida en esta sesión

Todo lo de esta sección se midió contra el entorno real, no se infiere. Los comandos
están al final de cada apartado para que se pueda reproducir.

### 1.1 Truncamiento denso silencioso: 54,6% del texto no se embebe

Dos límites distintos, en unidades distintas, y nadie los reconcilia:

| Constante | Valor | Unidad real | Dónde |
|---|---|---|---|
| `ChunkingOptions.MaxTokensPerChunk` | 512 | tokens *estimados* a 4 chars/token | `Domain/ChunkingOptions.cs:9`, fijado a 512 en `Cli/Commands/IngestCommand.cs:163` |
| `OnnxBrainOptions.MaxSequenceLength` | 256 | tokens **reales** de SentencePiece | `Vectorization/OnnxBrainOptions.cs:49`, `appsettings.json:13` |

El chunker cree que corta a 512 tokens porque divide caracteres entre 4
(`RoslynCSharpChunkingStrategy.cs:25`, `ChunkBuilder.cs:38`). Después,
`EncodeSentencePiece` corta sin avisar en `Math.Min(spmIds.Count, maxTokens - 2)` = 254
tokens (`OnnxVectorizationBrain.cs:224`). No hay log, ni contador, ni advertencia.

**La constante de 4 chars/token es falsa para este corpus.** Medido con el mismo
tokenizador SentencePiece que usa el motor, sobre 2.000 puntos reales de `bsuite-repo`:

| Medida | Valor |
|---|---|
| chars/token real (media) | **2,52** (no 4,0 — la heurística sobreestima la capacidad 1,6×) |
| tokens de `enriched_content` | p50 = 283, p75 = 562, p90 = 1.355, p95 = 1.705, max = 6.064 |
| chunks truncados (> 254 tok) | **1.090 / 2.000 = 54,5%** |
| tokens descartados | **509.393 / 933.678 = 54,6%** |

Por tipo de chunk:

| `chunk_type` | n | p50 tok | p90 tok | % truncado |
|---|---|---|---|---|
| `Property` | 300 | 467 | 660 | **74%** |
| `PlainTextWindow` | 194 | 281 | 443 | 63% |
| `Method` | 1.228 | 295 | 1.698 | 55% |
| `Class` | 204 | 162 | 659 | 35% |
| `Constructor` | 62 | 150 | 229 | 6% |
| `DocumentSection` | 12 | 103 | 228 | 8% |

Consecuencias, en orden de importancia:

- **La rama densa está ciega a la mitad del corpus.** Una pregunta parafraseada sobre una
  regla que vive en el cuerpo de un método largo no tiene contra qué acertar por
  similitud. Encaja con `parafraseada` = 44% y `simbolo` = 17% en la línea base.
- **La rama dispersa NO trunca.** `SparseTokenizer` procesa el texto completo, así que el
  54,6% perdido sigue siendo alcanzable léxicamente. Eso explica por qué la fusión RRF
  ayuda tanto y por qué subir el peso disperso a 1,3 dio ganancia: estaba compensando un
  defecto, no aportando señal complementaria.
- **`Property` al 74% es el chunk-imán otra vez.** El fix de agrupación ya subió recall@10
  de 62% a 69% en `bsuite-auditorias`, pero el problema restante no es el tamaño del grupo:
  es que el grupo, aun "dentro de presupuesto", se embebe cortado a un tercio.
- **La línea base de las olas 5 y 6 mide un motor averiado.** El umbral de decisión de
  `docs/eval/bsuite-repo.umbral-de-decision.md` sigue siendo válido como instrumento
  (es reproducible: dos corridas idénticas), pero si el truncamiento se arregla, hay que
  re-baselinear antes de juzgar la Ola 5, o su ganancia y la del fix se mezclan.

Reproducción:

```bash
curl -s -X POST http://localhost:6333/collections/bsuite-repo/points/scroll \
  -H 'Content-Type: application/json' \
  -d '{"limit":2000,"with_payload":true,"with_vector":false}' -o scroll.json
# y contar tokens con SentencePieceTokenizer.Create(sentencepiece.bpe.model,
# addBeginningOfSentence:false, addEndOfSentence:false), igual que CreateTokenizer()
```

#### Evidencia previa en contra, y por qué no cierra el caso

El PoC de búsqueda libre **ya probó esto y salió negativo**: correr con
`MaxSequenceLength=512` (truncamiento cero respecto al presupuesto del chunker) dejó el
recall del baseline idéntico. Está registrado en `poc/RagEngine.Poc.FreeSearch/README.md:70-73`
y en la nota de sesión correspondiente. Hay que tomarlo en serio: es un experimento hecho, no
una intuición, y sube la probabilidad de que 11.2 salga en cero.

Tres razones por las que no lo considero concluyente, todas verificables en
`poc/RagEngine.Poc.FreeSearch/RESULTADOS.md`:

1. **Se midió desde el suelo.** La rama densa sola en ese PoC daba 0% @1, 0% @3, 0% @5 y 5%
   @10 sobre 19 preguntas. Un cambio que no mueve un 0% no ha demostrado no tener efecto:
   no había rango donde manifestarlo. Es un nulo sobre un instrumento degenerado.
2. **Otro corpus, y el más pobre de los cinco.** El PoC corrió sobre
   `Reyma.TI.Tickets.Microservice/src`: 963 chunks, frente a los 22.986 puntos de
   `bsuite-repo`. Es además el corpus cuyo recall@10 real es el peor medido (16%). El p90
   de 1.355 tokens de §1.1 es de `bsuite-repo`, no de ahí; no hay razón para suponer que un
   microservicio tenga métodos igual de largos que un ERP.
3. **La rama dispersa enmascara el efecto en cualquier medición fusionada.**
   `SparseTokenizer` no trunca. Si se mide el pipeline fusionado, el disperso ya está
   cubriendo el texto que el denso no ve, y arreglar el denso mueve poco por construcción.
   El efecto sólo es visible aislando la rama densa.

Esto **cambia el diseño de 11.2**, no sólo su prioridad: hay que medir la rama densa
**aislada**, sobre un corpus con margen real, y con n suficiente. Medirlo fusionado sobre
`micro-repo` es repetir el experimento que ya salió negativo y llamarlo confirmación.

**Lo que NO se midió y hace falta antes de decidir el arreglo:** si subir
`MaxSequenceLength` a 512 mejora o empeora. El grafo admite 512 posiciones
(`max_position_embeddings: 512`, verificado en el `config.json` local), pero la tarjeta de
sentence-transformers de `paraphrase-multilingual-MiniLM-L12-v2` publica
`max_seq_length: 128` — ese archivo **no está** entre los locales, así que el dato hay que
verificarlo antes de apoyarse en él. Si es 128, el mean-pooling sobre 512 tokens de un
modelo entrenado a 128 diluye más de lo que suma, y el arreglo correcto es bajar el chunk,
no subir la ventana. Son tres brazos y hay que medirlos ([§5.1](#ola-11--arreglar-el-instrumento)).

### 1.2 Superficie de red sin autenticación

| Hallazgo | Verificación |
|---|---|
| **Qdrant en `0.0.0.0:6333-6334` sin API key.** Lectura, borrado de colecciones y upsert de puntos envenenados, sin credencial. | `docker ps` → `qdrant-local 0.0.0.0:6333-6334->6333-6334/tcp`; el `scroll` de §1.1 devolvió 2.000 payloads con `content` de código fuente sin autenticar. La API key es el único mecanismo de auth de Qdrant y no está puesta, así que escritura y borrado están igual de abiertos. |
| **`rag-api` en `0.0.0.0:5080` sin autenticación.** Ni API key, ni usuario/contraseña, ni proxy. | `grep -E "AddAuthentication\|RequireAuthorization\|ApiKey"` en `Api/Program.cs` → sin coincidencias. `docker ps` → `0.0.0.0:5080->5080/tcp`. |
| **CORS restrictivo no protege nada de esto.** La política por defecto sin orígenes (`Program.cs:88-95`) es una buena decisión, pero CORS lo aplica el navegador: `curl` lo ignora. | Lectura de código. |
| **`TopK` y `MinScore` sin cota.** `request.TopK ?? 10` en `/api/search` (`Program.cs:352`) y `/api/ask` (`:412`), sin clamp. Con `rerank=true` —que es el **default silencioso**— el pool del cross-encoder es 3×TopK: un request de 200 bytes con `TopK=100000` compra minutos de CPU y GBs de RAM. | Lectura de código. Es amplificación, no sólo validación floja. |
| **`/api/test-metrics` expuesto sin auth.** Endpoint de depuración en la superficie pública. | `Program.cs:336`. |
| **El log de consultas guarda en claro pregunta, respuesta, rutas y contenido de fuentes** bajo `logs/`, montado desde el contenedor. En estos corpus eso es contenido de negocio (auditorías, tickets). No hay política de retención. | `Program.cs:379,462,598`. |
| Ollama sí está bien: sólo `127.0.0.1:11434`. | `lsof -nP -iTCP -sTCP:LISTEN`. |
| `infra/docker-compose.yml` con rutas absolutas `/Users/DevStudio/...`. El ítem 1.2 eliminó esto de los `appsettings`; el compose quedó fuera del barrido. | Lectura del archivo. |

Nada de esto es una vulnerabilidad exótica: es una superficie de administración sin
puerta, en una máquina que sirve a la LAN. LightRAG, que es un proyecto de investigación,
sí trae API key o usuario/contraseña con bcrypt.

### 1.3 Lo que se verificó y está bien

Para que la lista de arriba no se lea como que el proyecto está flojo:

- **Sí hay limpieza incremental.** `DeleteSupersededPointsAsync` (`QdrantVectorStore.cs:451`)
  borra los puntos obsoletos tras una re-ingesta. La hipótesis de "chunks huérfanos que
  envenenan el índice para siempre" se comprobó y es **falsa**.
- **Límite de body de 1 MB** en Kestrel, con la razón escrita (`Program.cs:99`).
- **`ProblemDetails` + `UseExceptionHandler` + `UseStatusCodePages`**, los tres, con el
  comentario que explica por qué hacen falta los tres.
- **Reanudación de la fase 2** apoyada en `resumen_pending` y en la idempotencia de los IDs
  por hash de contenido.
- **CI real** (`.github/workflows/ci.yml`) que compila y corre la suite, con la decisión de
  *no* poner umbrales de cobertura escrita en la cabecera del archivo.

---

## 2. Qué es cada proyecto

Resumido de sus repos el 2026-09-04. Ambos MIT.

**LightRAG** (HKUDS) — RAG sobre grafo de conocimiento, planteado como alternativa ligera a
Microsoft GraphRAG. Recuperación de doble capa: en la ingesta, un LLM extrae entidades y
relaciones de cada chunk y **escribe una descripción en lenguaje natural de cada una**; esas
descripciones se indexan como vectores propios, separados de los del chunk. En consulta,
cinco modos: `local` (entidades y sus atributos), `global` (temas y cadenas de relaciones
entre documentos), `hybrid` (los dos), `naive` (sólo vectores de chunk, el RAG clásico) y
`mix` (todo junto, el default). Cuatro roles de almacenamiento —KV, vector, grafo y
**estado de documento**— con backends intercambiables (PostgreSQL recomendado como único
backend; también Neo4j, Milvus, Qdrant, MongoDB, Memgraph). Rerank opcional, con el coste
declarado (1-2 s). Servidor REST con dos frontends: `/webui` de administración y
`/workspace` de sólo consulta. Autenticación por API key o usuario/contraseña con bcrypt.
Limitaciones que el propio repo declara: los backends en memoria no sirven para producción,
y **cambiar el modelo de embedding obliga a borrar las tablas de vectores a mano** porque no
hay herramienta de re-embedding.

**RAG-Anything** (HKUDS) — Capa multimodal **construida sobre LightRAG**; se le puede pasar
una instancia de LightRAG ya existente. Su aporte real es la capa de parseo: MinerU
(PDF/imágenes/Office con aceleración GPU), Docling (Office/HTML) y PaddleOCR, más
procesadores por modalidad que convierten imágenes, tablas y ecuaciones en descripciones
textuales y en nodos del grafo. Consulta con VLM opcional. Informe técnico:
arXiv:2510.12323.

---

## 3. Mapa de correspondencias

| Capacidad | Rag.Context.Engine | LightRAG | Quién va delante |
|---|---|---|---|
| Chunking de código | Roslyn AST (C#), parser propio (TS), símbolos `defined`/`consumed` | 4 estrategias genéricas: fijo, recursivo, semántico por vector, semántico por párrafo | **Aquí**, claramente |
| Presupuesto de chunk | 512 tokens *estimados* → truncado real a 254 (§1.1) | Ligado al tokenizador del modelo | **LightRAG** |
| Bandas de recuperación | 3 (código denso, disperso, resumen de negocio) fusionadas por RRF con pesos calibrados | chunk + entidad + relación, modo `mix` | Empate; ver §4.1 |
| Grafo | No hay. La Ola 6 propone un join por nombre en tiempo de consulta | Grafo persistido en la ingesta, con descripciones embebidas | **LightRAG** |
| Contrato de escala de score | 5 escalas declaradas, `IsComparableAcrossQueries()`, invariante de orden fijado en test | No documentado | **Aquí**, y por mucho |
| Evaluación | 5 eval-sets con procedencia (commit, hash de dataset, config), baselines versionados, umbral de decisión escrito *antes* del cambio, negativos adversariales | No hay arnés de eval en el repo | **Aquí**, y por mucho |
| Gate de confianza / anti-fabricación | Gate por bandas calibrado, clasificador semántico de meta-intención, sanitizador determinista del modo Simple, prompt sin anclaje | No hay equivalente | **Aquí** |
| Borrado / actualización incremental | `DeleteSupersededPointsAsync` + IDs por hash de contenido | Borrado selectivo con regeneración del grafo desde caché de LLM | Empate |
| Estado por documento | No existe | Storage dedicado | **LightRAG** |
| Puertos de almacenamiento | 1 clase concreta (`QdrantVectorStore`, sin interfaz — Ola 9) | 4 roles, backends intercambiables | **LightRAG** |
| Aislamiento multi-inquilino | Ninguno: `Collection` es string libre sin dueño | Workspaces por `WORKING_DIR`, que su propio README admite que es configuración, no roles | Ninguno de los dos sirve para lo que hace falta aquí — §4.3 |
| Autenticación | **Ninguna** | API key o usuario/contraseña bcrypt | LightRAG tiene *algo*, pero **no es el modelo**: ver §4.2 |
| Consola de administración | `rag status` / `rag doctor` en CLI | `/webui` + `/workspace` | **LightRAG** |
| Trazas / métricas | 4 instrumentos, `MeterListener` que sólo loguea, cero OpenTelemetry | Estado de pipeline en la webui | Empate, los dos flojos |
| Modalidades | `.cs .ts .tsx .js .jsx .xaml .sql .md .txt .json .xml .csproj` | + PDF, Office, imágenes, tablas, ecuaciones, audio/vídeo (vía RAG-Anything) | **RAG-Anything** |
| Coste de ingesta | ~19 h para 23k puntos con resumen por chunk | 1+ llamada de LLM por chunk para extraer entidades | Empate; los dos caros |

El patrón es nítido: **el motor va delante en todo lo que se puede medir y detrás en todo lo
que se puede operar.**

---

## 4. El fundamento empresarial

Esta sección reemplaza el planteamiento original, que trataba la autenticación, la consola
y los puertos como huecos frente a LightRAG condicionados a que alguien los pidiera. Ese
razonamiento es el que produce la deuda: un motor que sirve consultas sobre corpus de
auditoría sin saber quién pregunta no es un motor "todavía sin autenticación", es un motor
cuyo registro de acceso para ese periodo no se podrá reconstruir nunca.

El objetivo declarado es un despliegue empresarial multiempresa. Lo que sigue se diseña
contra eso, no contra el uso local de hoy.

### 4.1 Las tres clases de coste, que es lo que decide qué se hace ahora

No todo lo pendiente cuesta lo mismo según cuándo se haga. Distinguirlo es lo que evita
tanto la deuda como el sobrediseño.

**Clase A — Irreversible. No es más caro después: es imposible después.**

| Qué | Por qué no se puede recuperar |
|---|---|
| Identidad en el registro de consultas | El log histórico no tiene a quién atribuir. Si el motor sirve seis meses sin identidad, la respuesta de cumplimiento para esos seis meses es "no se sabe", de forma permanente. Hoy el log guarda pregunta, respuesta y fuentes (`Program.cs:379,462,598`) y ningún actor. |
| Historia de ingestas y estado por documento | No se puede reconstruir qué archivo se indexó, cuándo, con qué versión de contrato y si falló, para las ingestas que ocurrieron antes de que existiera la tabla. |
| Procedencia de quién ingestó qué | Mismo problema: en multiempresa, "¿quién metió este código al índice?" no tiene respuesta retroactiva. |

Esto es lo único que justifica interrumpir el trabajo de retrieval. Es también lo más
barato de las tres clases: son campos y una tabla, no una arquitectura.

**Clase B — Mucho más barato ahora porque va montado en trabajo ya planificado.**

| Qué | Vehículo que ya está en el ledger | Coste si se hace aparte después |
|---|---|---|
| ACL por colección (qué empresas y qué roles pueden leerla) | **5.f** — revivir `CollectionManifest`, que ya se escribe al crear la colección y se valida al consultar. Añadir `RequiredScopes[]` y `Tenants[]` al record es el mismo camino de código que validar el hash del modelo, y los scopes ya existen (§4.3). | Diseño propio + migración de las 10 colecciones + decidir el default: cerrado rompe a todos los clientes existentes, abierto significa que la migración nunca termina |
| Punto único donde se aplica el filtro obligatorio de inquilino | **9.1** — puerto del vector store. La regla que hay que fijar ahora: el filtro se aplica en el puerto, no en el endpoint. | Tres endpoints que reimplementan el filtro, y una omisión es una fuga de datos entre empresas |
| Validación de `Collection` sin enumerar lo ajeno | **7.b**, pendiente | Reimplementar la respuesta de error. Ver el aviso de §4.4: el criterio de 7.b **tal como está escrito hoy es una fuga** |
| Perfil de recuperación por colección | **7.a**, pendiente | El perfil y la ACL son el mismo objeto leído en el mismo momento; separarlos duplica la lectura y la caché |
| Segundo backend de almacenamiento | **9.2 / 9.4** | El refactor ocurre bajo presión de fecha, con un índice vivo |
| `tenant` en el payload de cada punto | **5.e** ya va a re-ingestar todo | Bajo, honestamente: Qdrant tiene `set_payload`, así que añadirlo después es una migración de payload por lotes derivada de `repository_name`, no una re-ingesta de 19 h. Se hace en 5.e porque es gratis ahí, no porque después sea imposible |

**Clase C — Cuesta lo mismo cuando se haga.** TLS, gestión de secretos, cotas de `TopK`,
retención del log, consola de administración. No hay razón para adelantarlos salvo el
riesgo que corren mientras no existen — y en el caso de las cotas de `TopK` y de la API key
de Qdrant, ese riesgo es que hoy el stack está abierto en la LAN (§1.2).

**La consecuencia práctica:** el fundamento no se construye entero de golpe. Se **decide y
se escribe el contrato ahora** (clases A y B, que caben dentro de olas ya planificadas), y
se **construyen las superficies después** (clase C). Lo pesado no es la seguridad: es la
consola, y la consola es lo único que de verdad se puede posponer sin pagar intereses.

### 4.2 Identidad: la casa ya tiene el modelo, y no es el de LightRAG

El IDP a usar es el **IdentityServer centralizado que consume `Reyma.InnovApp`**, con sus
propios scopes y configuración. Queda **explícitamente fuera** la autenticación propia de
`BusinessSuite.Xaf`: esa es interna de XAF y no sirve como modelo. De ese proyecto se toma
sólo la convención de base de datos.

| Hecho verificado | Valor | De dónde |
|---|---|---|
| Autoridad OIDC | `https://identity.innovacion.reyma.com.mx/auth/<entorno>` — el entorno va en la ruta, así que hay dev/qa/prod | `Reyma.InnovApp/appsettings.json` |
| Convención de `client_id` | `rym.ti.mobile` — `rym.<área>.<cliente>` | idem |
| Librería para validar el token en una API .NET | `Microsoft.AspNetCore.Authentication.JwtBearer` | convención .NET; presente también en `BusinessSuite.Xaf` |
| Base de datos sancionada | `Npgsql` / Postgres | `BusinessSuite.Xaf` |

Diseño, entonces, y es más corto de lo que yo había escrito:

- **`JwtBearer` con `Authority` configurable**, apuntando al IdentityServer. El día que
  Entra ID sustituya a AD, es un cambio de autoridad y audiencia en configuración, sin
  tocar código: los dos hablan OIDC. No hace falta ninguna abstracción propia de
  autenticación; la abstracción es el estándar.
- **El motor se registra como recurso de API** en el IdentityServer existente, con scopes
  que siguen la convención de la casa (§4.3). Y un cliente aparte para identidad de
  máquina —el CLI, el job de eval nocturno— con el `sub` de esa credencial apareciendo en
  la auditoría igual que un usuario.

**Corrección a mi tabla de §3:** en autenticación LightRAG no es a quién copiar. API key o
usuario/contraseña con bcrypt, y aislamiento por directorio de trabajo, está por debajo de
lo que ASP.NET Core da de fábrica y muy por debajo de lo que la empresa ya opera. LightRAG
puso el requisito sobre la mesa; la implementación es la de la casa.

### 4.3 Autorización: no inventar un modelo, derivarlo del que ya existe

Este es el hallazgo que más cambia el plan. Los scopes que el IdentityServer ya emite hoy:

```
openid  profile  offline_access  organizational
permissions.service.read   identity.service.read
integrity.service.read/write
rym.ti.gateway.read/write     rym.ti.tickets.read/write
rym.ti.reminders.read/write   rym.autorizador.read/write
rym.gerencia.read/write       rym.gerencia_db2.read/write
```

Tres cosas se leen de ahí, y las tres ahorran trabajo:

1. **La convención de scopes existe:** `rym.<área>.<sistema>.<verbo>`. El motor no elige un
   esquema nuevo, se registra como `rym.ti.ragengine.read` / `.write` / `.admin`.
2. **La dimensión de empresa ya tiene un claim:** `organizational`. La frontera multiempresa
   no se inventa en una tabla del motor, se lee del token.
3. **Hay un servicio de permisos centralizado** (`permissions.service.read`). El motor no
   debería tener su propia matriz de permisos si esa matriz ya vive en otro sitio.

Y de ahí sale la decisión de diseño que hace el aislamiento casi gratis:

> **El ACL de una colección no necesita vocabulario nuevo: puede exigir el scope del sistema
> que esa colección indexa.** La colección que indexa el monolito de tickets exige
> `rym.ti.tickets.read`. Quien ya tiene permiso para ver ese sistema puede consultar su
> índice; quien no lo tiene, no. La autorización del motor queda **derivada** de la de los
> sistemas indexados, en lugar de ser un sistema paralelo que alguien tendría que reconciliar
> después.

Eso convierte el campo del manifiesto en `RequiredScopes[]` poblado con nombres de scope que
**ya existen**, no en un modelo de roles propio:

```csharp
public IReadOnlyList<string> RequiredScopes { get; init; } = []; // vacío = sin publicar
public IReadOnlyList<string> Tenants { get; init; } = [];        // claim organizational
```

`RequiredScopes` vacío tiene que significar **sin publicar**, no "todos": es la única
elección que hace que una colección nueva no se filtre por olvido.

**Tres cosas que hay que verificar con quien opera el IdentityServer antes de fijar esto,
porque las estoy infiriendo de nombres de scope, no de documentación:**

- Qué forma tiene el claim `organizational`: una empresa, o varias por usuario. Si es una,
  el filtro es trivial; si son varias, hay que decidir qué pasa con una colección compartida.
- Cómo se consulta el mapa de roles y *permission-roles*. El modelo de la casa es: **el
  acceso de una app a una API se resuelve por `client`, y el acceso a módulos y acciones por
  roles y permission-roles**, con la configuración de seguridad del usuario ya cargada
  desde donde se autentica. Lo que falta saber es si esos roles llegan en el token o hay
  que pedirlos a `permissions.service` en cada turno — de eso depende si el filtro
  intra-colección de §4.4 es una lectura de claims o una llamada de red por consulta.
- Si registrar un recurso de API nuevo y sus scopes es trámite o proyecto. De eso depende
  que `12.2` sean días o semanas, y es la única dependencia externa de todo el plan.

### 4.4 El aislamiento en dos niveles, y cuál de los dos es caro

El alcance declarado es **empresa → sistemas → roles dentro del sistema**, donde cada
sistema es un monolito y en la ingesta de código un sistema es una colección.
`pedidos-empresa1` y `pedidos-empresa2` son bases de código distintas, no particiones de
una misma; el de tickets es uno solo que todas las empresas usan.

**Nivel 1 — entre empresas: la frontera coincide con la colección, y por eso es barato.**
No hace falta `tenant` en cada punto del índice ni filtro por punto para separar empresas:
hace falta la ACL del manifiesto de §4.3, y una colección compartida lista varias empresas
o exige el scope compartido. Eso es el ítem **5.f**, ya pendiente, sobre un record que ya
existe (`Domain/CollectionManifest.cs`) y que ya se valida al consultar. Dos campos.

**Nivel 2 — roles dentro de un sistema: aquí sí hay que filtrar por punto, y es el caro.**
El acceso a módulos y acciones se resuelve por roles y permission-roles, y eso es filtrado
intra-colección. Lo bueno: el dato del lado del índice ya está —el payload lleva `namespace`
y `relative_path`, verificado en el scroll de §1.1— así que el módulo se deriva de lo que
hay, **sin re-ingesta**. Lo caro no es el dato ni el filtro: es **el mapeo entre un rol de
la empresa y un módulo del código**, que nadie ha escrito y que no se puede inferir del
repositorio. Si ese mapeo no se puede obtener, la salida honesta es grano grueso —acceso por
sistema, vía el scope, no por módulo— declarado como limitación en la documentación, y **no**
un mapa de permisos propio que duplique el de la empresa y que alguien tenga que mantener
sincronizado.

**La regla arquitectónica que hay que fijar antes de escribir la primera línea de los dos
niveles:** el filtro de autorización se aplica **dentro del puerto de recuperación** (ítem
9.1), no en los endpoints. Hoy `/api/search`, `/api/ask` y `/api/ask/stream` construyen sus
`RetrievalOptions` por separado; si el filtro vive en el endpoint, son tres
implementaciones y la cuarta que alguien añada será la fuga. Un `RetrievalOptions` que no
pueda construirse sin contexto de autorización —que rompa la compilación si falta, no que
lo ignore— es la versión de esto que no se puede olvidar, y encaja con el ítem 9.5 (tests
de arquitectura).

### 4.5 Aviso: el criterio de aceptación de 7.b, tal como está escrito, es una fuga

El ítem 7.b dice hoy:

> *"Una colección inexistente devuelve 400 con ProblemDetails **nombrando las disponibles**"*

En multiempresa eso enumera el inventario de sistemas de todas las empresas a cualquiera
que escriba un nombre mal. Se corrige antes de implementarlo, no después:

- El error nombra sólo las colecciones **visibles para el llamante**.
- Una colección que existe pero no es del llamante responde igual que una que no existe.
  Distinguirlas confirma su existencia, que es la mitad de la información.

Es el ejemplo exacto del argumento de esta sección: el ítem está redactado para un motor de
un solo usuario, y ejecutarlo tal cual crea trabajo que habrá que deshacer.

### 4.6 La topología con Rancher, y el único límite que no se puede suplir

La topología viable, dada la restricción declarada —el servidor de IA de la empresa no se
va a prestar, así que la potencia de inferencia sigue siendo este Mac mini—:

| Pieza | Dónde | Por qué |
|---|---|---|
| API | **Pod tras el Ingress L7 de Rancher** | El Ingress termina TLS y aplica la autenticación de §4.2, así que resuelve `12.8` sin código |
| Qdrant | **Contenedor en el clúster**, `StatefulSet` + PVC, sólo `ClusterIP` | Cabe de sobra: la colección más grande son 22.986 puntos. Y al no publicarse, **el hallazgo de §1.2 desaparece por construcción**: no hay puerto expuesto que proteger |
| Postgres | **Servidor de desarrollo de la empresa** | Ya existe y `Npgsql` ya es convención (§4.7) |
| Ollama (generación) | **Sigue en el Mac** | Es la única pieza que necesita el ancho de banda de memoria del M4 Pro. Ver `viabilidad-computo-remoto.md`: 229,7 GB/s efectivos, 84 % del teórico |
| Ingesta (`rag ingest`) | **Sigue en el Mac** | Necesita las dos cosas a la vez: ONNX en proceso y Ollama para los resúmenes (~19 h) |

Tres consecuencias que no son obvias:

**1. La ingesta no necesita exponer Qdrant.** Es un trabajo por lotes que lanza un
operador, así que alcanza el Qdrant del clúster por `kubectl port-forward`, autenticado por
el kubeconfig y el RBAC de Rancher. Cero superficie nueva: Qdrant se queda `ClusterIP`.

**2. El Mac pasa a ser dependencia de producción para la generación.** Eso convierte el
ítem **8.e** de deseable en obligatorio: hoy `GenerationServiceExtensions.cs:85` arma
`new HttpClient { Timeout = ... }` a mano, sin retry ni circuit breaker, mientras Qdrant y
el generador de resúmenes sí tienen Polly. Un pod en el clúster hablando con un equipo de
escritorio por la LAN necesita esa resiliencia, y necesita degradar a "no puedo responder
ahora" en vez de a una excepción sin manejar. Además Ollama hoy escucha sólo en
`127.0.0.1:11434` y **no tiene autenticación** — abrirlo a la LAN es la conversación con IT
que ya identificó `viabilidad-computo-remoto.md` §6.3, y ahora deja de ser hipotética.

**3. El clúster es la máquina x64 que el ítem 3.5 está esperando.** Su `razon_bloqueo` dice
literalmente que la rama x64 "solo se probo con el overload puro que recibe la arquitectura
como parametro, nunca contra un runtime x64 real". Mover la API a Rancher **desbloquea 3.5 y
a la vez lo vuelve precondición**: sin él, el pod no arranca.

#### Lo que eso destapa, y que sí está medido

`RagEnginePaths.SelectArchitectureBinary` cae, fuera de arm64, del `model_qint8_arm64.onnx`
al `model.onnx` genérico (fp32). Es decir: en la topología partida, **el índice se
construye en el Mac con int8 y las consultas se sirven desde el clúster con fp32**. Nadie
había medido si eso importa. Medido en esta sesión, con `rag eval` sobre las 47 preguntas
respondibles de `bsuite-repo`, cambiando sólo el binario del lado de la consulta:

| | recall@1 | recall@3 | recall@5 | recall@10 | \|Δ top_score\| máx | media |
|---|---|---|---|---|---|---|
| Bi-encoder int8 (hoy) | 19% | 32% | 36% | **40%** | — | — |
| Bi-encoder fp32 (pod x64) | 17% | 30% | 36% | **40%** | 0,0093 | 0,0008 |
| Cross-encoder int8 (hoy) | 38% | 40% | 45% | **49%** | — | — |
| Cross-encoder fp32 (pod x64) | 40% | 43% | 45% | **49%** | **0,1534** | **0,0469** |

La corrida int8 reprodujo el baseline del repo exactamente, así que el instrumento es el
mismo.

**Conclusión, y va en dos direcciones opuestas:**

- **La recuperación es segura al cambiar de binario.** recall@10 idéntico en las dos
  parejas. En el bi-encoder cambian 2 preguntas de 55, las dos a k bajo, con deltas de
  score de ~0,001. Servir consultas desde un pod x64 contra un índice construido con int8
  **no degrada el recall**.
- **El gate de confianza NO es seguro.** El cross-encoder es ~50× más sensible a la
  cuantización que el bi-encoder: desviación media de 0,047 y máxima de **0,153**. Y ese es
  precisamente el score contra el que están calibrados los umbrales de banda. Cuántas
  preguntas cruzan un umbral al cambiar el binario:

  | Umbral | Preguntas que cruzan |
  |---|---|
  | 0,05 | 2 |
  | 0,10 | 2 |
  | 0,45 | 2 |
  | 0,60 | **5** |

  Cruzar un umbral no cambia el recall: cambia **si el motor responde o dice "no sé"**. Es
  el guardarraíl anti-fabricación moviéndose entre 2 y 5 preguntas de 47 (4–11%) por un
  cambio de arquitectura de despliegue. Y habría pasado en silencio: el recall no se mueve,
  los tests siguen verdes, y el motor simplemente empieza a contestar —o a negarse— en
  casos distintos.

**Regla que sale de esto:** los umbrales del gate están ligados al **binario**, no sólo al
modelo ni al TopK. Van en el perfil de colección (Ola 7) o en configuración validada al
arranque, y el despliegue x64 exige recalibrarlos con `gate-bandas.labeled-set.json` y
`infra/gate-bandas-barrido.py`, que ya existen. La alternativa —usar el mismo binario en
los dos lados— obligaría a re-exportar un int8 para x64 y a re-medirlo igual.

### 4.7 Almacenamiento: Postgres para tres de los cuatro roles, y Qdrant para el que importa

La empresa tiene servidores Postgres y `Npgsql` ya es dependencia en `BusinessSuite.Xaf`.
Eso convierte la Ola 9 de ejercicio de pureza en el camino a correr sobre infraestructura
sancionada. Con los cuatro roles de §4.8:

| Rol | Hoy | Objetivo | Nota |
|---|---|---|---|
| KV (caché de resúmenes) | SQLite | **Postgres** | No es preferencia: SQLite es un bloqueo duro para más de una instancia, y ya causó corrupción real compartiendo el WAL entre el host y el contenedor |
| Estado por documento | No existe | **Postgres** | Nace ahí directamente |
| Manifiesto / ACL | Payload de Qdrant | **Postgres** | Los permisos no deberían vivir en el mismo sistema que los datos que protegen |
| Auditoría de consultas | Archivo de log | **Postgres** | Un log en archivo no es un artefacto de cumplimiento consultable |
| Vectores | Qdrant | **Qdrant** | Ver abajo |

**Sobre mover los vectores a Postgres, la parte que no hay que endulzar:** `pgvector` no es
un reemplazo de lo que este motor usa de Qdrant. Aquí se usan vectores nombrados, un vector
**disperso** nativo y **fusión RRF del lado del servidor** en un solo `QueryAsync`.
`pgvector` no tiene tipo disperso ni RRF nativo: habría que armar BM25 con `tsvector` o una
extensión, y fusionar en el cliente. Eso es reescribir la pieza mejor medida del motor.

La recomendación honesta: **Qdrant se queda, desplegado como contenedor en los servidores de
la empresa.** La pregunta real no es técnica sino organizativa —si infraestructura sanciona
un almacén nuevo— y conviene plantearla pronto, con el coste de la alternativa nombrado. El
puerto se construye igual, por dos razones que no son puristas: hace la decisión reversible,
y el arnés de eval es lo que permitiría demostrar que un cambio de backend no hundió el
recall. Ahí es donde la Ola 1 se paga sola.

### 4.8 Los cuatro roles de almacenamiento

Lo único de la arquitectura de LightRAG que vale copiar tal cual, porque ahorra diseñar la
abstracción de la Ola 9 desde cero y hace obvio el cuarto hueco, que hoy no está en el ledger:

| Rol de LightRAG | Aquí | Ítem |
|---|---|---|
| Vector storage | `QdrantVectorStore` (clase concreta, sin interfaz) | 9.1 |
| KV storage | `SummaryCache` (SQLite concreto, 6 consumidores directos) | 9.4 |
| Graph storage | No existe (payload de símbolos + join en consulta) | Ola 6 |
| Doc status storage | **No existe** | nuevo |

### 4.9 La consola: lo único genuinamente pesado, y va después

`rag status` y `rag doctor` son toda la visibilidad, y son CLI: nadie que no sea ingeniero
puede ver nada. Pero una consola no se puede construir primero, porque **los datos que
mostraría no se están guardando**: no hay estado por documento, ni registro de corridas de
ingesta, ni auditoría consultable. Construir la consola antes que esas tres tablas es
construir una pantalla vacía.

Secuencia: las tablas primero (clase A, baratas), la consola después (clase C, cara). Lo
que sí hay que decidir ahora es **el modelo de datos**, para que el estado se persista desde
la primera ingesta en lugar de reconstruirse jamás.

---

## 5. Qué vale la pena tomar de LightRAG en retrieval

### 5.1 La cuarta banda: descripciones de relación, no sólo de entidad

El mecanismo de LightRAG —hacer que un LLM escriba, en la ingesta, una descripción en
lenguaje natural de cada entidad y **de cada relación**, e indexarla como vector propio— es
el mismo que la banda de resumen de negocio de este motor, que ya midió **+16 pp de
recall@10** con los pesos `sparse=1.3 / resumen=2.5`. Eso es evidencia independiente de que
la dirección es correcta, y añade una pieza que aquí no existe: **la descripción de la
relación entre dos entidades**, no sólo de cada una por separado.

Encaja como una banda más en `RetrievalFusionOptions`, con su peso a calibrar por barrido,
sin tocar la arquitectura. Y refuerza el ítem **5.b** (resumen por archivo/tipo en vez de
por chunk): LightRAG extrae por entidad, no por chunk, y es precisamente lo que hace su
coste viable. El ÷10 de llamadas a Ollama que 5.b persigue no es un atajo: es cómo lo hace
el estado del arte.

**Precondición:** medir esto sobre la línea base de §1.1 sin resolver antes el experimento
de §1.1.1 sería tirar cómputo.

### 5.2 Grafo persistido como plan B de la Ola 6, no como sustituto

La Ola 6 propone un two-hop en tiempo de consulta: un segundo `QueryAsync` filtrado por
`defined_symbols ∈ consumed_symbols(top)`. El propio ítem **6.d** ya declara el riesgo —
cuántas expansiones traen un homónimo de otro módulo.

**Recomendación: no cambiar la Ola 6.** Para C# y TypeScript, el join sintáctico de Roslyn
es *más preciso* que la extracción por LLM, y es gratis en la ingesta. Pero si 6.d mide una
precisión de join mala, el grafo persistido de LightRAG es el plan B con evidencia de que
funciona, y conviene que 6.d quede escrito como el punto donde se decide eso.

### 5.3 De RAG-Anything, sólo la capa de parseo — y sólo si hay un hueco real

El scanner admite hoy 12 extensiones, **ninguna binaria**: sin PDF, sin Office, sin
imágenes (`Domain/ScanProfile.cs:27`). Si los vaults de negocio tienen procedimientos en
PDF, matrices de permisos en Excel o capturas de pantalla, hoy son invisibles al motor y
ninguna mejora de retrieval los va a encontrar.

**Criterio de entrada, y no lo tengo medido:** contar cuántos archivos no ingeribles hay en
los corpus reales de negocio. Si es un 5%, no vale el esfuerzo. Si es un 40%, es el hueco
más grande del proyecto y ninguna ola lo aborda.

**Forma correcta si pasa el criterio:** sidecar HTTP en Python (MinerU o Docling) que
devuelve markdown, invocado por el scanner. **No** una dependencia del Core, ni una
migración a Python. El motor ya demostró que ingesta prosa muy bien; el hueco es convertir
el binario en prosa.

### 5.4 Lo que no hay que tomar

- **La base.** Se perderían 8 olas de trabajo verificado para ganar un grafo que cabe como
  una banda.
- **El modo `mix` como default.** LightRAG lo pone por defecto y afirma que da "los
  resultados más ideales" sin publicar el número. Aquí eso se decide con `rag eval`.
- **Los cinco modos de consulta como superficie pública.** Cinco modos son cinco caminos
  que calibrar, documentar y evaluar. La Ola 7 resuelve el problema real —que hoy todo es
  global— sin multiplicar la superficie.
- **Su modelo de autenticación y de aislamiento.** §4.2 y §4.3.

---

## 6. Lo que ya hacemos mejor: qué cuesta conservarlo y qué compra

Puesto en papel, como pediste, porque conservar esto **no es gratis** y la decisión de
seguir pagándolo debe ser explícita.

| Lo que tenemos | Qué cuesta conservarlo | Qué compra |
|---|---|---|
| **Chunkers Roslyn / TypeScript por AST** | Un chunker por lenguaje; cada lenguaje nuevo es trabajo real, no configuración | Chunks que respetan fronteras de símbolo. Es la base de las olas 5 y 6; con chunking genérico, `defined_symbols` no existiría |
| **Contrato de escala de score** (5 escalas, `IsComparableAcrossQueries()`, invariante de orden en test) | Cada camino nuevo de scoring debe declarar su escala | Que el gate de confianza no compare un RRF contra un umbral de sigmoide. Es el tipo de error que produce fabricación silenciosa, y aquí está cerrado por construcción |
| **Arnés de eval** (5 eval-sets con procedencia, negativos adversariales, umbral escrito antes del cambio) | Etiquetar preguntas a mano; verificar cada ancla dos veces; re-baselinear en cada salto de contrato | Es lo que hace que "adoptar la idea de LightRAG" sea una medición y no un acto de fe. Sin esto, todo §5 sería opinión. **Es el activo más valioso del proyecto** |
| **Gate de confianza, sanitizador del modo Simple, prompt sin anclaje** | Umbrales que se recalibran cuando cambia el TopK o el modelo | Que el motor diga "no sé" en lugar de inventar. En un corpus de auditoría empresarial esto no es una mejora de calidad, es el requisito |
| **Golden masters** (contexto byte a byte, hashes de prompt, chunking) | Regenerar el golden en cada cambio intencional | Refactorizar sin miedo. El corte de `RagGenerationService` de 680 líneas en 7 clases se pudo probar equivalente |
| **IDs deterministas por hash + limpieza incremental** | Un salto de contrato invalida hashes y fuerza re-ingesta (~19 h) | Re-ingestas idempotentes y sin huérfanos. Es lo que hace que una ingesta abortada se pueda reintentar |
| **El ledger y el arnés de plan** | Disciplina de un ítem por commit y de escribir el resultado | Que el estado del proyecto sobreviva al reinicio de sesión. Es la razón de que este documento pueda apoyarse en 8 olas y no en recuerdos |

**El coste común de toda la columna del medio es el mismo: lentitud deliberada.** Cada
cambio de retrieval cuesta una re-ingesta y un re-baseline. Eso es lo que hay que aceptar a
cambio de poder afirmar cualquier número. Y es exactamente lo que LightRAG **no** tiene: su
README afirma que `mix` da los mejores resultados y no publica una tabla.

**Cómo se relaciona con el fundamento empresarial:** este arnés es lo que hace viable
adoptar la seguridad sin miedo. Añadir un filtro obligatorio de autorización al retriever
es el tipo de cambio que normalmente da pánico porque puede hundir el recall en silencio.
Aquí se corre `rag eval` contra los cinco baselines y se sabe.

---

## 7. Ítems propuestos para el ledger

### Ola 11 — Arreglar el instrumento

Criterio de salida: el texto que el chunker produce es el texto que el encoder denso ve, o
está medido y descartado que eso importe.

| id | título | modelo | esf. | h. máquina | verificación |
|---|---|---|---|---|---|
| `11.1-medir-truncamiento` | Contador `rag_chunks_truncated_total` + fila en el summary de ingesta + advertencia amarilla cuando el p95 de tokens reales excede `MaxSequenceLength`. Es el fallo silencioso que estuvo invisible desde el cambio de modelo. | sonnet | low | 0 | Una ingesta de micro-repo reporta el número de chunks truncados; hoy sale 0 en la salida y >0 en la realidad |
| `11.2-experimento-3-brazos` | A/B/C **sobre la rama densa aislada** (no la fusión: el disperso no trunca y enmascara el efecto — §1.1.1) y sobre `bsuite-repo`. Brazos: (a) hoy, (b) chunk cortado a 250 tokens reales, (c) `MaxSequenceLength=512` sin tocar el chunker. Verificar antes el `max_seq_length` publicado del modelo: si es 512, (c) es una línea de config y (b) sobra. | opus | high | 1.5 | Los tres brazos con procedencia en `docs/eval/quality/`, con recall de la rama densa sola y de la fusión, sobre las 47 respondibles |
| `11.3-presupuesto-en-tokens-reales` | Sólo si 11.2 da ganancia: el presupuesto del chunker pasa a tokens reales del tokenizador. `TokenEstimator.CharsPerToken = 4.0` mide 2,52 aquí. Subir `ChunkingContract.Version`. | opus | high | 19 | recall@10 en `bsuite-repo` por encima de 40% fuera del ruido de ±1 pp; p95 de tokens reales ≤ 254 |
| `11.4-rebaseline-olas-5-y-6` | Re-baselinear y reescribir el umbral de decisión de las olas 5 y 6. Sin esto, la ganancia de la Ola 5 y la de 11.3 se mezclan. | sonnet | medium | 0.3 | Nuevo baseline con procedencia; el umbral cita el commit de 11.3 |
| `11.5-charspertoken-por-lenguaje` | La constante es global y la usan los chunkers de C#, TS y Markdown y `ContextAssembler`. Prosa y código no comparten ratio. | sonnet | medium | 0 | Un test fija el ratio medido por lenguaje contra una muestra del corpus |

**Rollback de 11.3:** `ChunkingContract.Version` anterior + baseline anterior, los dos
versionados. **El riesgo real que hay que probar, no el benigno:** cortar a 250 tokens
fragmenta métodos largos; el escenario a medir no es "¿mejora en preguntas cortas?" sino
"¿empeora `simbolo`, donde firma y cuerpo se separan en dos chunks?".

### Ola 12 — Fundamento de identidad y aislamiento

Ya no es "cerrar la superficie expuesta". Criterio de salida: ningún camino de consulta
puede construirse sin contexto de autorización, y el registro de auditoría tiene un actor
desde la primera consulta.

| id | título | modelo | esf. | h. máquina | verificación | clase |
|---|---|---|---|---|---|---|
| `12.1-cotas-y-cierre-inmediato` | Clamp de `TopK` (1..100) y `MinScore` (0..1) en los 3 endpoints; API key en Qdrant y bind a `127.0.0.1`; quitar `/api/test-metrics`; compose sin rutas absolutas. Lo que hoy está abierto en la LAN. | sonnet | medium | 0 | `TopK=100000` → 400; `curl` a Qdrant sin `api-key` → 401; `rag doctor` verde | C, pero urgente |
| `12.2-identidad-y-auditoria` | `JwtBearer` contra `https://identity.innovacion.reyma.com.mx/auth/<entorno>`, con el motor registrado como recurso de API y scopes `rym.ti.ragengine.read/write/admin` según la convención de la casa. Políticas para los tres verbos + **el actor en el registro de consultas**. La auditoría es la parte irreversible: va con la autenticación, no después. **Dependencia externa:** dar de alta el recurso y sus scopes en el IdentityServer. | opus | high | 0 | `curl` sin token → 401; con token de scope `.read`, ingestar → 403; el log de cada consulta nombra al actor | **A** |
| `12.3-acl-en-el-manifiesto` | `RequiredScopes[]` y `Tenants[]` en `CollectionManifest`, poblados con **scopes que ya existen** (`rym.ti.tickets.read` para el índice del monolito de tickets) y con el claim `organizational`. Vacío = sin publicar. **Se ejecuta como parte de 5.f**, no aparte. | opus | high | 0 | Una colección sin `RequiredScopes` no es legible salvo por administrador; el índice de tickets lo lee quien ya tiene `rym.ti.tickets.read` y nadie más | **B** |
| `12.4-filtro-en-el-puerto` | El filtro de autorización se aplica dentro del puerto de recuperación, y `RetrievalOptions` no se puede construir sin contexto de autorización. **Se ejecuta como parte de 9.1.** | opus | high | 0 | Un test de arquitectura (9.5) falla si un endpoint construye `RetrievalOptions` sin contexto; los 5 baselines de eval no se mueven | **B** |
| `12.5-corregir-el-criterio-de-7.b` | Reescribir el criterio de aceptación de 7.b antes de implementarlo: el error nombra sólo las colecciones visibles, y una ajena responde igual que una inexistente. | haiku | low | 0 | Un llamante de la empresa A que pide una colección de B recibe la misma respuesta que si no existiera | **B** |
| `12.6-filtro-por-modulo-intra-coleccion` | Nivel 2 de §4.4. Derivar el módulo de `namespace` / `relative_path`, que **ya están en el payload** — sin re-ingesta. Filtro obligatorio en el puerto de 12.4. **Precondición:** que `permissions.service` pueda responder qué módulos ve un rol (§4.3); si no puede, el alcance se reduce a grano por sistema y se declara la limitación. | opus | high | 0 | Un rol con acceso a un módulo no recupera chunks de otro, verificado con una consulta que sí los traía antes | B |
| `12.7-tenant-en-el-payload` | Añadir `tenant` al payload aprovechando la re-ingesta de **5.e**. No es irreversible (`set_payload` existe), pero ahí es gratis. | sonnet | low | 0 | Los puntos nuevos llevan `tenant`; un filtro por él devuelve sólo lo de esa empresa | B |
| `12.8-secretos-y-tls` | Los secretos salen de `appsettings` a variables de entorno / secretos del clúster. El TLS lo termina el **Ingress L7 de Rancher** (§4.6), así que no hay trabajo de TLS en el código — sólo documentar dónde termina. Hoy es HTTP en claro en `0.0.0.0:5080`. | sonnet | medium | 0 | `grep` de secretos en `appsettings*.json` vacío; `docs/operaciones.md` declara dónde termina el TLS | C |
| `12.10-recalibrar-el-gate-por-binario` | Los umbrales del gate están ligados al **binario** del cross-encoder, no sólo al modelo y al TopK: cambiar int8→fp32 mueve el score hasta 0,153 y cruza umbrales en 2–5 de 47 preguntas (§4.6). Recalibrar con `gate-bandas.labeled-set.json` + `infra/gate-bandas-barrido.py` para el binario que corra el pod, y dejar el binario declarado junto a los umbrales. | opus | high | 0.5 | Los umbrales del pod x64 se derivan de un barrido propio, y un test falla si los umbrales se usan con un binario distinto al que los calibró | **A si se despliega** |
| `12.9-retencion-del-log` | Qué se guarda del turno, dónde y por cuánto. Hoy guarda pregunta, respuesta y contenido de fuentes en claro, sin política. | sonnet | low | 0 | `docs/operaciones.md` lo declara y el sink lo implementa | C |

**Sobre `12.3`, `12.4` y `12.6`:** están escritos como ítems propios para que la
reconciliación no los pierda, pero **no se ejecutan por separado**. Son el contenido de
5.f, 9.1 y 9.1 respectivamente. Ejecutar 5.f sin la ACL significa escribir el manifiesto
dos veces.

### Ola 13 — Estado persistido y visibilidad

Criterio de salida: un turno se puede seguir de punta a punta, y una ingesta abortada dice
qué le faltó.

| id | título | modelo | esf. | h. máquina | verificación | clase |
|---|---|---|---|---|---|---|
| `13.1-estado-por-documento` | El cuarto rol de §4.8: qué archivo, cuándo, con qué versión de contrato, por quién, y si falló. Nace en Postgres. Es historia: lo que no se guarde ahora no se reconstruye. | sonnet | high | 0 | Tras abortar una ingesta a mitad, un comando dice qué archivos quedaron sin indexar | **A** |
| `13.2-kv-a-postgres` | `SummaryCache` de SQLite a Postgres vía el puerto de 9.4. SQLite bloquea el multi-instancia y ya causó corrupción real. | opus | high | 0.5 | Dos instancias de la API contra la misma caché sirven en paralelo sin corrupción; el eval no se mueve | B |
| `13.3-opentelemetry` | Sustituir el `MetricsListener` casero —que sólo escribe al log— por OpenTelemetry con exportador configurable. Los 4 instrumentos ya existen. | sonnet | medium | 0 | Un `/metrics` en formato Prometheus con los 4 instrumentos | C |
| `13.4-trazas-del-turno` | `ActivitySource` con un span por paso. Hoy los 7 colaboradores loguean por separado y no hay correlación. | sonnet | medium | 0 | Una traza muestra los 5 spans con su duración | C |
| `13.5-metricas-que-faltan` | Bandas del gate, uso de rerank, latencia de generación, hit-rate de la caché, chunks truncados (11.1). Todas decididas por análisis, ninguna instrumentada. | sonnet | medium | 0 | Aparecen en `/metrics` con datos tras un turno | C |
| `13.6-consola-de-administracion` | Inventario de colecciones con su perfil y ACL, versión de contrato, estado de ingestas, auditoría y resultados de eval. **Va después de 13.1**: sin esas tablas es una pantalla vacía. | opus | high | 1 | Un no-ingeniero ve qué colecciones existen, quién puede leerlas y cuándo se ingestaron | C |

### Ola 14 — Proceso

| id | título | modelo | esf. | h. máquina | verificación |
|---|---|---|---|---|---|
| `14.1-eval-nocturno` | El CI compila y corre la suite en ~30 ms, pero **nada ejecuta `rag eval`**: una regresión de recall sólo se ve si alguien la busca a mano. Job programado con Qdrant y modelos que corre los 5 eval-sets y falla si algún recall@10 cae más de 2 pp. | opus | high | 0.5 | Una regresión introducida a propósito pone el job rojo y nombra el eval-set y la categoría |
| `14.2-analizadores-como-aviso` | Nada impide que una advertencia entre al repo: `Directory.Build.props` sólo fija `MSBuildEnableWorkloadResolver=false`, ningún `.csproj` declara `TreatWarningsAsErrors`, y el CI no corre analizadores — pese a que CLAUDE.md pide compilar sin advertencias. | haiku | low | 0 | El CI publica las advertencias sin fallar |

---

## 8. Orden recomendado

1. **`12.1`** — horas, y cierra un stack que hoy está abierto en la LAN.
2. **`12.2`** — la parte irreversible. Cada consulta servida sin actor es historia que no se
   recupera, así que va antes que cualquier trabajo de retrieval.
3. **`13.1`** — misma razón, y es una tabla.
4. **`12.5`** — quince minutos de redacción que evitan implementar una fuga.
5. **`11.1` + `11.2`** — ~1,5 h, y deciden si hay que pagar las 19 h de `11.3`. El primer
   paso es gratis: verificar el `max_seq_length` del modelo.
6. **`5.f` con `12.3` dentro**, y **`9.1` con `12.4` y `12.6` dentro**. Aquí se consolida
   el aislamiento, montado en olas ya planificadas.
7. **`11.3` + `11.4`** si 11.2 gana. Aquí se pagan las 19 h. Y `5.e` lleva `12.7` gratis.
8. **Las olas 5 y 6**, ya con la línea base corregida y el aislamiento en su sitio.
9. **`13.2`**, y después el resto de la clase C: telemetría, consola.

**Si la decisión de mover la API a Rancher se toma antes de todo esto**, el orden cambia:
`3.5` (hoy bloqueado, y el clúster es la máquina que le falta) y `8.e` (resiliencia de
generación, que con el Mac como dependencia remota deja de ser opcional) pasan a ser
precondiciones del despliegue, y `12.10` —recalibrar el gate para el binario del pod— entra
en la clase A: sin él, el guardarraíl anti-fabricación cambia de comportamiento en silencio.

**Coste total, sin endulzarlo:** las clases A y B son semanas, no días, y la mayor parte va
montada en ítems que ya estaban planificados. La clase C —consola y telemetría— es lo
genuinamente pesado y es lo único que se puede posponer sin pagar intereses. Lo que **no**
se puede posponer sin pagarlos es la auditoría con actor, y es de lo más barato de la lista.

## 9. Qué invalidaría este análisis

- **Si `11.2` mide que arreglar el truncamiento no mueve el recall.** Entonces la mitad
  perdida del texto no contenía respuestas, la rama dispersa ya la cubría, y el diagnóstico
  de §1.1 es correcto como hecho pero irrelevante como causa. Sería un resultado válido y
  hay que publicarlo igual. **Es el desenlace más probable de los cuatro:** ya hay un
  experimento previo con ese resultado (§1.1.1), y los tres motivos por los que no lo doy
  por cerrado son objeciones de diseño, no contra-evidencia.
- **Si el `max_seq_length` real del modelo es 512** y no 128, el brazo (c) es casi gratis y
  `11.3` con sus 19 h se puede descartar.
- **Si existe o se planea una colección cuyo contenido pertenezca a más de una empresa**
  —un vault de documentación que mezcle empresas, o resúmenes de negocio que citen a dos—
  entonces el nivel 1 de §4.4 se cae al nivel 2: haría falta `tenant` por punto con filtro
  obligatorio, y el aislamiento deja de ser barato. Es lo primero que hay que verificar.
- **Las tres inferencias sobre el IdentityServer de §4.3 salen de nombres de scope, no de
  documentación.** Si el claim `organizational` no lleva la empresa, o si
  `permissions.service` no puede responder por módulo, el diseño de autorización cambia de
  grano — no de forma, pero sí de alcance. Confirmarlo con quien lo opera es la única
  dependencia externa de todo el plan, y `12.2` depende de que dar de alta un recurso de
  API nuevo sea trámite y no proyecto.
- **Si los corpus de negocio no tienen archivos binarios**, §5.3 se cae.
- **La medición de §4.6 es de un corpus y 47 preguntas.** El desplazamiento del
  cross-encoder es grande y consistente, así que la conclusión "los umbrales están ligados al
  binario" es robusta; los *números exactos* de preguntas que cruzan cada umbral no lo son.
  Antes de fijar umbrales nuevos hay que barrer con `gate-bandas.labeled-set.json`, que es
  el instrumento hecho para eso, no con este eval-set — el propio umbral de decisión de
  `bsuite-repo` advierte que no sirve para calibrar el gate.
- La muestra de §1.1 son 2.000 de 22.986 puntos, tomados por `scroll` en orden de ID (GUID
  determinista, sin correlación con el tamaño). Si el orden estuviera sesgado, los
  porcentajes se mueven; el mecanismo del truncamiento, no.
