# Plan de refinamiento arquitectónico: del documento de evaluación a trabajo ejecutable

**Entrada:** *Documento de Evaluación Técnica y Plan de Refinamiento Arquitectónico: Rag.Context.Engine*
(diagnóstico comparativo contra un asistente comercial + hoja de ruta de 4 fases).
**Rama:** `feat/rag-api-selector-coleccion`. **Fecha:** 2026-08-24.
**Alcance de este turno:** sólo este documento. No se tocó código de producto ni el ledger.

> **Revisión adversarial del 2026-08-24 (segunda pasada).** Se verificaron las ~30 citas
> `archivo:línea` de este documento abriendo cada archivo. Cinco afirmaciones cayeron y están
> corregidas en el texto, no en un apéndice: (1) la caché de resúmenes **no** se indexa por
> `EnrichedContent`, así que un cambio de encabezado no cuesta una regeneración; (2) los "+16 pp"
> del tercer vector eran de la calibración de pesos, no de la banda; (3) no existe eval-set de
> `bsuite-repo`, así que ningún criterio de recall sobre "n=89" era ejecutable; (4) el camino de
> borrado incremental sí existe; (5) el corpus objetivo no tiene XAML. Lo que sobrevivió se dejó
> como estaba.

---

## 1. Veredicto en una página

El documento de entrada acierta en **una** cosa nueva y valiosa: el **grafo ligero de símbolos en el
payload** (`defined_symbols` / `consumed_symbols`) y el salto Vista → ViewModel que habilita. Eso no
existe hoy y es exactamente el tipo de pregunta que el motor falla.

Todo lo demás se reparte en tres cubetas:

- **Ya está construido** (§2 encabezado semántico, §4 guardrail, §5 selector de colección). El
  documento lo propone como si el repo estuviera en blanco.
- **Está mal, y de forma peligrosa** (§2 "eliminar las llamadas LLM por chunk", §4 "umbral 0.20 y
  respuesta canned"). Las dos propuestas borran componentes que este proyecto ya midió y calibró.
  Apagar la banda de resumen cuesta, **medido**: −12 pp de recall@10 sobre datos reales (56% → 44%,
  n=16) y −37 pts en el PoC (63% → 26%, n=19). El umbral con respuesta canned reintroduce una
  regresión conversacional que este repo ya corrigió a propósito.
- **No lo ve**: la superficie HTTP sin endurecer, las dos colecciones ya rotas, los archivos
  borrados que nadie limpia, la generación conversacional sin política de reintento, y que la caché
  de resúmenes es un archivo SQLite mutable compartido cuya topología de despliegue ya provocó una
  corrupción real.

Corolario incómodo, y el punto que más importa si la palabra es *empresarial*: si el objetivo es que
un equipo use esto, el bloqueador no es *two-hop*. Es que `/api/health` miente
(`src/RagEngine.Api/Program.cs:136`), que no hay auth ni rate limit, y que dos colecciones del
vector store están rotas. Un two-hop excelente sobre una API que cualquiera puede tumbar con un POST
no es producción; es una demo mejor.

---

## 2. Qué del documento ya está construido (con evidencia)

| Propuesta del documento | Estado real | Evidencia |
|---|---|---|
| §2 "Encabezados sintéticos deterministas" antepuestos al chunk antes de vectorizar | **Ya existe.** Todo chunk tiene `EnrichedContent` = encabezado estructural + contenido crudo, y es eso lo que se embebe | `src/RagEngine.Core/Domain/CodeChunk.cs:31` (con el ejemplo del encabezado en el XML doc), `Infrastructure/Chunking/ChunkBuilder.cs:61` (`HeaderPrefix`), consumido en `RoslynCSharpChunkingStrategy.cs:142`, `TypeScriptChunkingStrategy.cs:478`, `FallbackChunkingStrategy.cs:76` |
| §2 "Desacoplar las llamadas a Ollama de la ingesta masiva" | **Ya está desacoplado**, opt-in, reanudable y cacheado. Fase 2 corre en su propio Channel + pool tras terminar la indexación, opera por scroll sobre `resumen_pending=true` y sobrevive a un corte | `Pipeline/DefaultIngestionPipeline.cs:169-176` y `:397-433`; `Infrastructure/VectorStore/QdrantVectorStore.cs:28` (`resumen_pending`), `Services/Summary/SummaryCache.cs` |
| §4 "Guardrail determinista: corte por umbral de cross-encoder" | **Ya existe**, y además con tres bandas en vez de un corte binario | `Services/Generation/RagGenerationService.cs:199` (banda baja) y `:225` (banda media); umbrales en `RagGenerationOptions.cs:53` (`0.05`) y `:61` (`0.60`) |
| §4 "Cortocircuito antes de invocar a Ollama" para consultas ajenas | **Ya existe**, y con un segundo mecanismo que el documento no contempla: un clasificador semántico de meta-intención | `Services/Generation/SemanticMetaIntentDetector.cs:92`, `MetaIntentOptions.cs:27` |
| §5 "API del selector de colección" | **Ya existe** el endpoint y el selector del frontend | `src/RagEngine.Api/Program.cs:141` (`GET /api/collections`) |
| §6 Fase 3 "Integrar el paso de re-ranking" | **Ya integrado**, y ya medido: con los pesos RRF calibrados el rerank dejó de sumar recall | `Infrastructure/Reranking/OnnxCrossEncoderReRanker.cs`; el único baseline con rerank es `docs/eval/baselines/innovapp-docs.rerank.baseline.json` — los otros tres son `norerank` |
| §6 Fase 2 "Configurar la fusión RRF" | **Ya configurada y calibrada por barrido**: `codigo=1.0`, `sparse=1.3`, `resumen=2.5`, `k=60`. Detalle que importa para §3.1: estos pesos **sólo se aplican a colecciones con el tercer vector**; con dos vectores el retriever usa la fusión nativa de Qdrant, que no admite peso por rama | `Infrastructure/VectorStore/RetrievalFusionOptions.cs:4-7` y `:16-19`, aplicados en `QdrantSemanticRetriever.cs:227-229` |

Lo único genuinamente nuevo de §2 no es el encabezado —que ya está— sino **los dos campos de
símbolos dentro de él**. Conviene decirlo con precisión, porque de eso depende presupuestar el
trabajo correctamente: no es "construir el encabezado", es "añadir dos líneas al encabezado que ya
se construye, y poblarlas".

---

## 3. Qué del documento es erróneo, y por qué es caro

### 3.1 "Sustituir el resumen LLM por chunk" borra el componente de retrieval más rentable del motor

El documento presenta esto como pura ganancia: *"0 llamadas a Ollama durante la ingesta"*. Lo que no
dice es qué se pierde. Hay dos mediciones directas de la banda de resumen, y las dos apuntan al mismo
lado:

- **PoC, n=19** (`poc/RagEngine.Poc.FreeSearch/RESULTADOS.md` §1): `código+sparse` da recall@10 =
  **26%**; añadir la banda de resumen lo sube a **63%**. +37 pts, 0 regresiones.
- **Ingesta real, n=16** (`docs/analisis-futuro/busqueda-libre-rag-3-bandas.md:356`, colecciones
  `bsuite-auditorias-baseline` vs `bsuite-auditorias-test`): recall@10 **44% sin resumen → 56% con
  resumen**. +12 pp netos (+5 preguntas ganadas / −3 regresiones).

Ese es el coste de la propuesta: entre 12 pp y 37 pts según el corpus, con n chico en los dos casos.
**Nota de procedencia, porque la primera versión de este documento la citó mal:** el número de
"+16 pts" que circula en el repo (`RESULTADOS.md:53`) **no** es la aportación de la banda — es la
ganancia de *calibrar los pesos* (sparse 1.0→1.3 y resumen 1.3→2.5) frente a los pesos base, con la
banda presente en ambas ramas. Citarlo como coste de apagar la banda es un error de atribución.

Y hay un efecto que compone: los pesos calibrados sólo se aplican a colecciones con tres vectores
(`RetrievalFusionOptions.cs:4-7`). Al bajar a dos, `QdrantSemanticRetriever` cae a la fusión nativa
de Qdrant, que entra con peso igual por rama. Apagar la banda no cuesta sólo la banda: se lleva de
paso la calibración de pesos.

Peor: el documento propone sustituirla por algo que **ya está en el sistema y ya está contribuyendo**
—el encabezado estructural del `EnrichedContent`, que la banda densa ya embebe. La sustitución
propuesta no cambia una señal por otra; elimina una señal y presenta como reemplazo una que ya
estaba contada.

Esto no vuelve gratuito el coste de la Fase 2. Significa que el experimento correcto es otro, y está
en la Ola 5.b: **resumen por archivo/tipo reutilizado por todos los chunks de ese archivo**, en vez
de por chunk. Divide las llamadas a Ollama por ~10 sin apagar la banda, y es falsable con el arnés
que ya existe.

### 3.2 "Umbral 0.20 sobre el top-1 del cross-encoder" es un número inventado sobre un score que este proyecto ya midió como inestable

Dos problemas independientes:

1. **El score no es invariante al lote.** El score de rerank que alimenta el gate cambia según el
   `TopK` con el que se compuso el lote. Está documentado y hay tres ítems abiertos por ello:
   `docs/analisis-futuro/gate-de-confianza-score-inestable-y-fuga-de-prompt.md`, ledger `4.1`, `4.2`,
   `4.3`. Fijar `0.20` sobre un score que se mueve con la configuración es calibrar sobre arena.
2. **La rama de score del gate sólo dispara si el rerank está encendido.** La condición real es
   `chunks.Count == 0 || (useReRanking && topScore < Options.LowConfidenceThreshold)`
   (`RagGenerationService.cs:199`): el caso "0 chunks" corta siempre, pero el corte **por umbral**
   está condicionado a `useReRanking`. Y el rerank, con los pesos calibrados, ya no aporta recall
   (`RESULTADOS.md` §1.3: con `sparse=1.3, resumen=2.5`, rerank da +0 preguntas / −2 regresiones,
   recall@10 79%→68%). O sea: el "cortafuegos matemático" del documento obliga a pagar un rerank
   cuyo beneficio de retrieval es negativo, sólo para tener un número que cortar. Esa dependencia
   hay que romperla (ledger `4.2`) **antes** de tocar umbrales, no después.

### 3.3 La "respuesta determinista predefinida" es exactamente el comportamiento que este proyecto ya retiró a propósito

El documento pide devolver, bajo umbral, la frase *"La consulta no está relacionada con el código
fuente ni la documentación indexada"*. Ese corte canned ya se probó y se reemplazó: rompía las
consultas conversacionales legítimas que caen en banda baja. Hoy la banda baja entra por
`NoGroundingSystemPrompt` (`Services/Generation/Prompts/NoGroundingSystemPrompt.cs`), y llegar ahí
costó una iteración documentada (`docs/analisis-futuro/guardrail-banda-baja-conversacional.md`).
Reintroducir el canned es una regresión con nombre y apellido.

### 3.4 El caso estrella del documento apunta a un lenguaje que no tiene chunker, en un corpus que no está indexado

Todo §3 se construye sobre `OrderDetailView.xaml`. Estado real del XAML en este motor:

- Se escanea: `Domain/ScanProfile.cs:27` incluye `.xaml`.
- Se clasifica: `Infrastructure/Scanning/FileSystemIngestionScanner.cs:25` mapea `.xaml` →
  `SourceLanguage.Xaml`.
- **No tiene estrategia de chunking.** El router registra sólo C#, TypeScript y Markdown; cualquier
  otro lenguaje cae a la ventana deslizante genérica
  (`Infrastructure/Chunking/ChunkingStrategyRouter.cs:47-52`).
- Prueba independiente: `ChunkType.XamlControl` y `ChunkType.XamlDataTemplate` están **declarados y
  nunca producidos** (`Domain/CodeChunk.cs:53-54`; un grep de `ChunkType.` sobre `src/` no los
  encuentra en ninguna estrategia). Son valores de enum muertos. Igual `ChunkType.SqlProcedure`.

Consecuencia directa: **el two-hop sobre vistas no puede funcionar antes de que el XAML se corte
estructuralmente**. Un chunk de XAML producido por ventana deslizante parte los `Binding` a la
mitad; extraer `consumed_symbols` de eso da símbolos truncados. El orden del documento (símbolos →
two-hop) está incompleto: falta un paso antes.

**Pero hay un hecho anterior a todo eso, y decide el ítem: el corpus objetivo no tiene XAML.**
Contado sobre el disco, excluyendo `bin/` y `obj/`:

| Fuente → colección | `.cs` | `.xaml` | `.sql` |
|---|---|---|---|
| `BusinessSuite.Xaf` → `bsuite-repo` | 2.031 | **0** | **0** |
| `Reyma.TI.Tickets.Microservice` → `micro-repo` | 275 | **0** | 0 |
| `Reyma.InnovApp` → *(ninguna colección)* | 413 | **79** | 0 |

`BusinessSuite.Xaf` es una app XAF (DevExpress, WinForms/Web): no hay una sola vista declarativa.
El XAML real del ecosistema vive en `Reyma.InnovApp`, una app MAUI con 79 `.xaml`, 896 apariciones
de `{Binding` y 68 `*ViewModel.cs` — es decir, el caso Vista→ViewModel del documento **sí existe**,
sólo que en un repositorio que hoy **no es una colección ingestada y no tiene eval-set**. La
evidencia de que se ingestó alguna vez está en los logs de la Fase 2
(`logs/rag-engine-20260820.json` resume `GrCardGroup.xaml`, `ServiceDetailPage.xaml`,
`SupportTicketEditPage.xaml`), no en una colección viva de hoy.

Conclusión operativa: escribir un chunker de XAML **no** es el paso previo al two-hop en `bsuite-repo`
—ahí el two-hop sólo puede ser C#→C#—, y para `Reyma.InnovApp` el paso previo no es el chunker sino
ingestar la colección y etiquetar preguntas. El ítem se mueve a la sección 8.

### 3.5 El two-hop es el tercer intento de un realce estructural, y los dos anteriores murieron

Este repo ya intentó dos veces mejorar el ranking con señal estructural:

- **Boost por afinidad de clase**: +6 pp en un eval-set chico, **0 pp con regresiones** en el corpus
  grande. Código revertido por completo (`docs/analisis-futuro/` y memoria del proyecto).
- **Rerank como mejora de recall**: dejó de sumar tras calibrar los pesos.

El two-hop puede ser distinto —expande el conjunto de candidatos en vez de reordenarlo, que es un
mecanismo diferente— pero merece el mismo escepticismo. Por eso en la Ola 6 el criterio de éxito va
pre-registrado antes de correr nada, y el rollback es apagar una bandera.

**Y aquí está el problema de instrumento que la primera versión de este documento no vio.** Lo que
existe para medir recall es esto y nada más:

| Eval-set | Preguntas | Colección contra la que corre |
|---|---|---|
| `docs/eval/bsuite-auditorias.eval-set.json` | 16 | `bsuite-auditorias-test` |
| `docs/eval/innovapp-docs.eval-set.json` | 26 | `innovapp-docs` |
| `docs/eval/tickets-microservice.eval-set.json` | 19 | `micro-repo` — **rota**, ledger `3.1` |

Total: **61 preguntas etiquetadas**, y **no hay eval-set de `bsuite-repo`**. El "n=89" que circula en
el ledger (`2.2`) es del arnés de *calidad* (`infra/quality-baseline.py` sobre
`replicate-env/data/questions/bsuite-repo.json`, 230 registros crudos), que mide postura y longitud
de respuesta, **no recall@k**. Cualquier criterio del tipo "+5 pp de recall@10 sobre `bsuite-repo`
(n=89)" es inejecutable tal como está escrito. Corolario para las olas 5 y 6: el instrumento hay que
construirlo antes, y el suelo de ruido es el que es —con n=16, una pregunta vale 6,25 pp; con n=61,
el error estándar de una proporción cerca de 0,5 ronda **±6 pp**. Un delta de +5 pp sobre 61
preguntas no es un resultado: es ruido con signo.

### 3.6 La tabla comparativa vende una garantía que el motor no da

*"0% alucinación paramétrica en reglas de negocio"*. Falso como está escrito, y el propio repo tiene
la evidencia: hay una regresión de fabricación documentada en modo Simple
(`docs/analisis-futuro/modo-respuesta-simple-codigo.md`, memoria del proyecto), y está medido que
el cuello de botella es la **generación**, no el retrieval: el cross-encoder pone el chunk correcto
en #1 y el modelo a veces responde con otro. Recuperar sólo del corpus reduce la alucinación; no la
elimina. Un documento que promete 0% invita a dejar de medirla.

---

## 4. Lo que el documento no ve

Ejes que un plan "a nivel producción empresarial" no puede omitir y que la entrada omite por completo:

1. **El coste de re-ingesta es real pero mucho menor de lo que este documento afirmó en su primera
   versión, y hay que decir por qué.** La caché de resúmenes se indexa por
   `(content_hash, prompt_version)` (`SummaryCache.cs:54`) y `content_hash` se calcula sobre el
   **contenido crudo**, no sobre el enriquecido: `ChunkBuilder.cs:82` es
   `ContentHasher.Compute(content)`, y `EnrichedContent` viaja aparte (`ChunkBuilder.cs:88-90`). El
   Id del punto se deriva de lo mismo (`DeterministicGuid.CreateForChunk(absolutePath, startLine,
   hash)`). Consecuencias, que reordenan el plan entero:
   - **Añadir dos líneas al encabezado no invalida nada.** No cambia `content`, ni el hash, ni el
     Id, ni la clave de caché. Es 100% de aciertos: la re-ingesta se reduce a embedding ONNX +
     upsert. Referencia medida en `docs/reingesta-manual.md` (sección "no cuesta 19 horas"):
     `micro-repo` son 953 chunks en **8,45 s**, así que los ~23.000 de `bsuite-repo` rondan
     **10–20 min**.
   - **Lo que sí cuesta horas** es mover *fronteras* de chunk (cambia `content` → miss de caché) o
     mover `prompt_version`, que es `SHA(PromptVersion|modelId|SystemPrompt)`
     (`OllamaBusinessSummaryGenerator.cs:150-152`): tocar el texto del prompt de resumen **o cambiar
     el modelo de Ollama** invalida las 21.082 entradas de golpe.
   - **Y el coste es medible antes de pagarlo, no estimable.** Contar cuántos de los chunks nuevos
     tienen un `content_hash` ausente de `summary-cache.sqlite3` da el número exacto de llamadas a
     Ollama antes de lanzar nada. Falsación en caliente, ya documentada: mirar la fila **Resúmenes**
     de la tabla en vivo en el primer minuto; si el contador de generados sube, la caché está
     fallando — cortar con Ctrl+C.
2. **Dos colecciones ya están rotas.** `engine-repo` y `micro-repo` fallan con "Not existing vector"
   (ledger `3.1`), y hay 272 puntos huérfanos de un chunking anterior en `bsuite-repo`
   (ledger `4.6`). Medir two-hop sobre un corpus sucio es medir ruido.
3. **`CollectionManifest` es código muerto.** El tipo existe para exactamente el problema del punto
   anterior —detectar deriva del modelo de embeddings por hash del `.onnx`— y no lo usa nadie: un
   grep de `CollectionManifest` sobre `src/` devuelve **sólo su propia definición**
   (`Domain/CollectionManifest.cs:9`).
4. **El camino de borrado incremental existe; lo que falta es más estrecho y más raro.** Corrección
   a la primera versión de este documento: `QdrantVectorStore.DeleteSupersededPointsAsync`
   (`:372-424`) barre los puntos de los archivos que **esa** corrida procesó y que ya no están en el
   conjunto de Ids vigentes, y el pipeline lo llama al cerrar la Fase 1
   (`DefaultIngestionPipeline.cs:165-167`). Los 272 huérfanos de `bsuite-repo` existen precisamente
   porque esa ingesta es **anterior** a que el barrido existiera (ledger `4.6`, commit `e559d80`), no
   porque el barrido falte. Los dos huecos que quedan son otros:
   - **Archivos borrados o renombrados.** El barrido se acota a `processedFilePaths`
     (`DefaultIngestionPipeline.cs:268`), que sólo contiene lo que el escáner recorrió en esta
     corrida. Un archivo que ya no está en disco nunca entra ahí, así que sus puntos sobreviven a
     toda ingesta incremental futura. Sólo `--force` los borra, y a costa de recrear la colección.
   - **La clave es la ruta absoluta.** El Id del chunk se deriva de `artifact.AbsolutePath`
     (`ChunkBuilder.cs:83`) y el payload guarda `file_path` = ruta absoluta
     (`QdrantVectorStore.cs:268`). Ingestar el mismo repositorio desde otra ruta —el contenedor
     montando `/repo` en vez de `~/Documents/Projects`, o un checkout movido— produce un conjunto de
     puntos **completamente duplicado** que el barrido no puede ver, porque ninguna ruta vieja está
     en `processedFilePaths`. No es hipotético para este proyecto: la API corre en Docker y la
     ingesta corre nativa.
5. **Superficie HTTP sin endurecer.** `/api/health` devuelve `Ok(new { status = "ok" })` sin
   comprobar Qdrant, Ollama ni las sesiones ONNX (`src/RagEngine.Api/Program.cs:136`). No hay auth,
   CORS explícito, rate limiting, `ProblemDetails` ni `UseExceptionHandler` (ledger `3.2`). Y
   `/api/search` acepta cualquier `Collection` como string libre sin validar que exista
   (`Program.cs:159`).
6. **Observabilidad a medias.** `Diagnostics/RagEngineMetrics.cs` define un `Meter` con 4
   instrumentos y **nadie los exporta** (ledger `3.3`). Los logs sí salen estructurados.
7. **Sin perfil por colección.** Los pesos de fusión son **globales**
   (`RetrievalFusionOptions.cs`), no por colección — justo lo contrario de lo que pide §5 del
   documento. Y la API fuerza `rerank = true` por defecto en los tres endpoints
   (`Program.cs:162`, `:215`, `:311`), sobre un rerank que ya no aporta recall. Este es el hueco
   real de la rama actual.
8. **Roslyn está en modo sintáctico.** `CSharpSyntaxTree.ParseText` sin `Compilation` ni
   `SemanticModel` (`RoslynCSharpChunkingStrategy.cs:54`). Se puede extraer símbolos, pero el join
   del two-hop será **por nombre, no por referencia resuelta**: dos `CanCancelOrder` en módulos
   distintos colisionan. Es aceptable y barato; hay que decirlo y medir la precisión, no fingir que
   es un grafo de referencias.
9. **La generación conversacional no tiene política de reintento.** Hay Polly, y está bien puesto —
   pero sólo en dos sitios: el pipeline `"qdrant"` (retry ×3 exponencial + circuit breaker) y el del
   generador de resúmenes (`ServiceCollectionExtensions.cs:105` y `:129`). El camino que ve el
   usuario no pasa por ninguno: `RagGenerationService` pide
   `_kernel.GetRequiredService<IChatCompletionService>()` (`:318`) sobre un `new HttpClient` armado a
   mano con sólo un `Timeout` (`GenerationServiceExtensions.cs:85-98`) — sin `IHttpClientFactory`,
   sin retry, sin breaker. Un hipo de Ollama a mitad de respuesta es una excepción no manejada sobre
   un SSE ya abierto. Detalle que lo confirma: `Microsoft.Extensions.Http.Resilience` 10.8.0 está
   declarado en `RagEngine.Core.csproj:19` y **no se usa en ninguna línea de `src/`**.
10. **La caché de resúmenes es estado mutable compartido, y eso ya causó una corrupción real.**
   Analizado por completo en `docs/analisis-futuro/concurrencia-y-cache-compartida-en-produccion.md`
   (nada implementado salvo el `busy_timeout` de `c21cf1d`): el 2026-08-20 el SQLite quedó con el
   btree corrupto por cruzar la frontera host macOS ↔ VM de Docker sobre el mismo WAL; se recuperó
   el 99,4% con `.recover`. Esto no es una nota histórica, es una **precondición operativa de la Ola
   5**: la re-ingesta y la API no pueden escribir el mismo archivo desde lados distintos de un bind
   mount, y el ledger ya lo escribe en la verificación de `4.6` ("con `rag-api` detenido"). El plan
   de la entrada propone un despliegue "empresarial" sin ver que la topología está acotada a una
   sola máquina por este archivo.

---

## 5. La restricción que ordena el plan

La primera versión de esta sección decía que *cualquier* cambio de encabezado o payload invalida la
caché y cuesta una regeneración completa. **Es falso** (§4.1) y llevaba a agrupar cambios que
conviene medir por separado. La regla correcta distingue tres clases de cambio:

> | Clase de cambio | Qué toca | Coste de máquina |
> |---|---|---|
> | **Encabezado / payload** (los dos campos de símbolos) | `EnrichedContent`, claves del payload | **Nulo en Ollama.** Embedding + upsert: ~10–20 min sobre `bsuite-repo` |
> | **Fronteras de chunk** (chunker nuevo, otra agrupación, otro límite de tokens) | `content` → hash → clave de caché | Ollama **sólo para los chunks cuyo hash cambió**. Acotado al lenguaje que se toca |
> | **Prompt de resumen o modelo de Ollama** | `prompt_version` | Regeneración total. Es el único cambio que de verdad cuesta ~19 h |

Y la regla de secuenciación que se deriva:

> **Antes de lanzar cualquier re-ingesta, contar cuántos `content_hash` nuevos faltan en
> `summary-cache.sqlite3`. Ese número —no una estimación— es el presupuesto. Si sale > 2.000, el
> cambio es de frontera de chunk y hay que justificarlo; si sale ~0, la corrida son minutos y se
> puede medir cada sub-ítem por separado.**

Corolario práctico, invertido respecto a la primera versión: **no** hay que agrupar los cambios de
chunking en un solo salto de `ChunkingContract.Version`. Agrupar era la respuesta a un coste que no
existe, y tiene un precio real —pérdida de atribución de causa, que es exactamente lo que mató el
boost estructural—. Se sube la versión por cada cambio que mueva un chunk, se re-ingesta, y se mide.
El salto de versión es una etiqueta de comparabilidad de baselines
(`ChunkingContract.cs:14-16`), no un peaje.

Los ítems de **tiempo de consulta** (two-hop en la Ola 6, perfiles en la Ola 7) siguen sin costar
re-ingesta y siguen siendo los más baratos de iterar.

---

## 6. Plan por olas

Las olas 1–4 son las del ledger vigente (`ejecucion-plan.estado.json`). Este plan **no las
reemplaza**: añade de la 5 a la 8 y fija dos precondiciones sobre las existentes.

### Precondiciones (ítems ya en el ledger, ascendidos a bloqueantes)

| Ítem | Por qué bloquea |
|---|---|
| `4.1`, `4.2`, `4.3` (estabilidad del gate) | Bloquean cualquier trabajo sobre el guardrail de §4. Calibrar un umbral sobre un score no invariante es tirar el trabajo. `4.2` además rompe la dependencia gate→rerank. |
| `3.1` (esquema de colecciones) + `4.6` (huérfanos de `bsuite-repo`) | Bloquean cualquier medición de two-hop. No se mide retrieval sobre un corpus con puntos de un chunking anterior. |
| `2.2` (partir `RagGenerationService`, 680 líneas — el ledger dice 1042, está desactualizado desde que `2.3` externalizó los prompts) | No bloquea, pero la Ola 8 mete el formato de 4 bloques ahí. Hacerlo antes evita tocar dos veces. |
| **Nuevo — `5.0`: instrumento de medición** | Bloquea todo criterio de recall de las olas 5 y 6. Hoy hay 61 preguntas etiquetadas repartidas en 3 eval-sets, ninguno sobre `bsuite-repo` (§3.5). Sin eso, "+5 pp de recall@10" no es un criterio, es una intención. |
| **Nuevo — precondición operativa de toda re-ingesta** | `rag-api` detenido, y la caché de resúmenes escrita desde un solo lado del bind mount. Ver `concurrencia-y-cache-compartida-en-produccion.md`: la alternativa ya costó una corrupción y un `.recover`. |

### Ola 5 — Contrato estructural de símbolos *(minutos de máquina, no horas)*

**Entrada:** `3.1` y `4.6` cerrados; corpus limpio; `5.0` cerrado (hay con qué medir).
**Salida:** todo chunk de C# y TypeScript lleva `defined_symbols` y `consumed_symbols` en el payload
y en el encabezado embebido; `bsuite-repo` re-ingestado; recall@10 **no peor** que la línea base.

Presupuesto de máquina de la ola, corregido: el cambio es de encabezado y payload, no de fronteras
de chunk, así que la caché de resúmenes acierta al 100% y la corrida son **~10–20 min** (§4.1 y §5).
Se verifica antes de lanzarla contando los `content_hash` ausentes de la caché.

| # | Ítem | Archivos | Verificación | Rollback | Máquina | Esfuerzo | Modelo | Multiag. |
|---|---|---|---|---|---|---|---|---|
| 5.0 | **Eval-set de recall sobre `bsuite-repo`.** El instrumento que hoy no existe: preguntas con chunk-ancla verificado literalmente contra el código, negativos incluidos, versionado ANTES de tocar el retriever | `docs/eval/bsuite-repo.eval-set.json`, `docs/eval/baselines/` | ≥40 preguntas etiquetadas y un baseline `norerank` con procedencia (commit, pesos, `chunking_contract_version`, `resumen_prompt_version`). Se declara en el mismo commit el umbral de decisión de las olas 5–6 | n/a — es el instrumento | alto | opus | no |
| 5.b | **Experimento: resumen por archivo en vez de por chunk.** Ataca el coste de ingestar corpus nuevo (÷~10 llamadas a Ollama) en lugar de apagar la banda | `Services/Summary/*`, `Pipeline/DefaultIngestionPipeline.cs:409-586` | A/B con criterio **pre-registrado en preguntas, no en pp**: sobre el eval-set de `5.0` (≥40) más `bsuite-auditorias` (16), el modo por archivo no puede perder **más de 1 pregunta neta** en ninguno de los dos. Reutiliza `docs/eval/quality/ab-resumen-*.json` como precedente de formato | Bandera de configuración; el modo por chunk sigue siendo el default hasta que gane | **sí, aquí sí hay horas**: cambia `prompt_version` y la granularidad, así que la caché no acierta. Contar los hashes ausentes antes de lanzar; el techo es el coste histórico de la Fase 2 completa | alto | opus | **sí** — un resumen peor degrada silenciosamente el ranking |
| 5.c | **Extractor de símbolos** sintáctico para C# y TypeScript | `Infrastructure/Chunking/*`, nuevo `SymbolExtractor` | Tests: `HasPermission(x) && Status == Draft` produce ambos símbolos; un comentario que menciona `CanCancelOrder` **no** lo produce (negativo adversarial) | Los campos quedan vacíos; el payload los ignora | 0 | medio | sonnet | no |
| 5.d | **Payload + índice en Qdrant.** Añadir las dos claves al upsert y crear índice de payload sobre `defined_symbols` | `Infrastructure/VectorStore/QdrantVectorStore.cs:266-283` | `rag status` muestra el índice; un filtro por `defined_symbols` responde en <50 ms sobre `bsuite-repo` | Las claves sobran sin romper nada; el índice se borra | 0 | bajo | sonnet | no |
| 5.e | **Salto de `ChunkingContract.Version`** + re-ingesta y re-baseline | `Infrastructure/Chunking/ChunkingContract.cs`, `docs/eval/baselines/` | `rag eval --baseline` sobre los eval-sets **que se puedan correr**: `bsuite-auditorias` (16, contra `bsuite-auditorias-test`), `innovapp-docs` (26) y el nuevo `bsuite-repo` de `5.0`. `tickets-microservice` corre contra `micro-repo`, que está rota — no cuenta hasta que `3.1` cierre. Criterio: recall@10 no peor **fuera de ±1 pregunta** en cada set; con estos n, una diferencia de una pregunta no es señal | Re-ingestar del commit anterior — con la caché acertando son otros ~15 min, no una noche | **~10–20 min por colección**, con `rag-api` detenido | medio | sonnet | no |
| 5.f | **Revivir `CollectionManifest`**: escribirlo al crear colección, validarlo al consultar | `Domain/CollectionManifest.cs:9`, `QdrantVectorStore.cs` | Consultar una colección creada con otro `.onnx` da error accionable en vez de "Not existing vector" | Dejar de validar; el manifest queda inerte | 0 | medio | sonnet | no |

> El chunker de XAML **ya no está en esta ola**: `bsuite-repo` tiene 0 `.xaml` (§3.4), así que era
> esfuerzo alto contra un lenguaje que el corpus objetivo no contiene. Está en la sección 8, con la
> condición exacta que lo devolvería al plan. El two-hop de esta ola es C#→C# y C#→TS.
>
> `5.c` extrae símbolos de C# y TypeScript solamente. Nada en la Ola 5 produce
> `ChunkType.XamlControl` ni `XamlDataTemplate`: siguen siendo valores de enum muertos y se quedan
> así.

### Ola 6 — Two-hop en tiempo de consulta *(coste de máquina ≈ 0)*

**Entrada:** Ola 5 cerrada con recall no peor.
**Salida:** el two-hop se puede encender y apagar por bandera, y hay un número que dice si sirve.

| # | Ítem | Archivos | Verificación | Rollback |
|---|---|---|---|---|
| 6.c | *(va primero)* Preguntas de salto ViewModel→Servicio / Controlador→Regla en el eval-set de `5.0`, etiquetadas con la **regla de negocio** como respuesta correcta, no con el punto de entrada | `docs/eval/bsuite-repo.eval-set.json` | ≥20 preguntas de salto **más** sus negativos (preguntas donde el salto NO debe ayudar, para detectar que el two-hop mete ruido). Versionadas y con baseline corrido antes de escribir una línea de 6.a | n/a |
| 6.a | Expansión por símbolo: tras la fusión, un segundo `QueryAsync` filtrado por `defined_symbols ∈ consumed_symbols(top)` y fusión del conjunto ampliado | `Infrastructure/VectorStore/QdrantSemanticRetriever.cs:186-230` | **Criterio pre-registrado, sobre el eval-set completo de `5.0` (≥40) más las ≥20 de salto de `6.c`:** el two-hop tiene que ganar **≥4 preguntas netas** en el subconjunto de salto y **no perder ninguna** fuera de él. Se declara en números de preguntas, no en pp, porque con estos n un punto porcentual no existe. Con menos, se descarta como se descartó el boost estructural | `TwoHop.Enabled=false` en config. Sin re-ingesta |
| 6.b | Presupuesto y tope del segundo salto (máx. símbolos, máx. candidatos, latencia) | ídem + `RetrievalFusionOptions` | El p95 de `/api/search` no crece más de 150 ms con two-hop ON, medido sobre las mismas preguntas antes y después | bandera |
| 6.d | Medir la **precisión del join por nombre** (D-4): cuántas expansiones traen un símbolo homónimo de otro módulo | ídem | Muestra manual de 30 expansiones; si >1/3 son colisiones, el ruido explica cualquier resultado nulo de 6.a y el número justifica evaluar `SemanticModel` | n/a |

Orden obligatorio: **6.c → 6.a**. Invertirlo es etiquetar viendo el resultado, que es la forma
estándar de conseguir el número que uno quería.

Modelo: `opus` para 6.a y 6.c (el criterio ES el trabajo, y el etiquetado decide el resultado),
`sonnet` para 6.b y 6.d. Multiagente: no — `rag eval` responde.

### Ola 7 — Perfiles de colección *(el hueco real de esta rama)*

**Entrada:** nada. Es independiente y se puede adelantar.
**Salida:** una colección declara su propio perfil de recuperación; la API valida contra él.

- **7.a** Perfil por colección: pesos de fusión, `min_score`, `rerank` sí/no, `two_hop` sí/no,
  familia de prompt (código / prosa), `top_k` por defecto. Hoy todo eso es global
  (`RetrievalFusionOptions.cs`) o se decide por heurística de contenido.
  Verificación: dos colecciones con perfiles distintos dan resultados distintos para la misma query
  con la misma llamada HTTP; `rag eval` reproduce ambos. Rollback: sin perfil, se usan los globales
  de hoy (el default debe ser byte-equivalente al comportamiento actual — se verifica con un eval
  idéntico al baseline).
- **7.b** Validar `Collection` contra las colecciones existentes en los 3 endpoints
  (`Program.cs:159`, `:212`, `:308`); hoy es string libre. Verificación: colección inexistente →
  400 con `ProblemDetails`, no 500.
- **7.c** Quitar `rerank = true` como default silencioso de la API (`Program.cs:162`, `:215`, `:311`)
  y dejar que lo decida el perfil. Verificación: el eval con perfil "sin rerank" iguala el baseline
  `*.norerank.baseline.json`.

Modelo: `sonnet`. Multiagente: no.

### Ola 8 — Superficie de producción y formato de respuesta

- **8.a** Endurecer la API — ledger `3.2`: `ProblemDetails`, `UseExceptionHandler`, health real
  (Qdrant + Ollama + sesiones ONNX), límites de tamaño de cuerpo, CORS explícito, rate limiting.
  **Si el criterio es "empresarial", este ítem va antes que la Ola 6.**
- **8.b** Exportador OTel del `Meter` que ya existe — ledger `3.3`.
- **8.c** Formato de diagnóstico en 4 bloques (§6 Fase 4 del documento de entrada): componente y
  archivo / propiedad de control / regla de negocio / rol requerido. Es un cambio de prompt, barato.
  Verificación: el arnés de calidad (`infra/quality-baseline.py`, 143 preguntas), **no** recall —
  el recall no ve el formato. Rollback: revertir el prompt. **Atención:** los prompts viven en
  clases C# (`Services/Generation/Prompts/`), así que un cambio de texto de resumen movería
  `prompt_version` e invalidaría la caché; el formato de respuesta **no** toca el prompt de resumen,
  así que aquí no aplica — pero hay que verificarlo antes de commitear, no después.
- **8.d** Regresión permanente de las 3 bandas de confianza — ledger `4.5`.
- **8.e** **Resiliencia en el camino de generación** (hueco 9 de la sección 4). Envolver el
  `IChatCompletionService` en un pipeline de Polly como los dos que ya existen, o registrar el
  `HttpClient` de Ollama por `IHttpClientFactory` y usar el
  `Microsoft.Extensions.Http.Resilience` que ya está en el `.csproj` sin usarse. Verificación:
  matar Ollama a mitad de un `/api/ask/stream` devuelve un evento de error en el SSE y un
  `ProblemDetails`, no una excepción sin manejar. Barato y observable con `curl`.
- **8.f** **Desacoplar la identidad del chunk de la ruta absoluta** (hueco 4 de la sección 4).
  Derivar el Id y el `file_path` del payload de `RelativeFilePath` + `RepositoryName` en vez de
  `AbsolutePath`. Verificación: ingestar el mismo repo desde dos rutas distintas produce el mismo
  conteo de puntos, no el doble. **Cuidado: esto sí cambia todos los Ids** y por tanto es una
  re-ingesta con `--force` de todas las colecciones — pero no toca `content`, así que la caché de
  resúmenes acierta y son minutos. Va después de `3.1`, que es donde se decide la detección de
  esquema.

---

## 7. Decisiones que tienes que tomar tú

**D-1 — ¿Hay XAML de verdad en el corpus objetivo? — RESPONDIDA, no hace falta decidir.** Contado
sobre el disco: `BusinessSuite.Xaf` (→ `bsuite-repo`) tiene 2.031 `.cs` y **0** `.xaml`;
`Reyma.TI.Tickets.Microservice` (→ `micro-repo`), 275 `.cs` y **0** `.xaml`. Los 79 `.xaml` del
ecosistema están en `Reyma.InnovApp`, que no es una colección ingestada. El chunker de XAML sale del
plan (sección 8) y el two-hop de la Ola 6 es C#→C# / C#→TS.
**La decisión que queda, y es distinta:** ¿se quiere que `Reyma.InnovApp` sea una colección? Si sí,
eso es una ola aparte —ingesta + eval-set + chunker de XAML— y hay que presupuestarla como tal, no
colarla dentro de la Ola 5.

**D-2 — ¿Se paga la re-ingesta una vez o varias? — la respuesta cambió al verificar la caché.**
Recomendación: **varias, una por sub-ítem que mueva chunks.** Los cambios de la Ola 5 son de
encabezado y payload, no de fronteras, así que la caché de resúmenes acierta y cada corrida son
~10–20 min, no una noche (§4.1, §5). Agrupar para "pagar una vez" era la respuesta a un coste que no
existe, y su precio —perder atribución de causa cuando el recall se mueve— es exactamente lo que
hizo caro diagnosticar el boost estructural. Lo único que hay que seguir agrupando es cualquier
cambio del **prompt de resumen o del modelo de Ollama**: eso sí invalida las 21.082 entradas de
caché y sí es una noche.

**D-3 — ¿El resumen por chunk se mantiene?** Recomendación: **sí, y se ataca su coste por 5.b, no
apagándolo.** El coste de apagarlo está medido dos veces (−12 pp con n=16 real, −37 pts con n=19 en
el PoC, §3.1) y el beneficio de retrieval de la sustitución propuesta es nulo porque el encabezado
ya está contado. Matiz que hace la decisión menos urgente de lo que parecía: la Fase 2 **ya está
pagada** y la caché la amortiza —una re-ingesta que no mueve fronteras no vuelve a llamar a Ollama—,
así que 5.b optimiza el coste de *ingestar corpus nuevo*, no el de re-ingestar el actual. Si el A/B
la refuta, el coste se queda y se acepta.

**D-4 — ¿Two-hop por nombre o por referencia resuelta?** Recomendación: **por nombre.** Roslyn está
en modo sintáctico (`RoslynCSharpChunkingStrategy.cs:54`); levantar un `Compilation` con todas las
referencias del proyecto es otro orden de magnitud de trabajo y de tiempo de ingesta. El join por
nombre tiene colisiones; se mide su precisión en 6.a y, si el ruido mata la ganancia, ahí sí se
evalúa el salto a `SemanticModel` con un número en la mano.

**D-5 — ¿"Empresarial" significa endurecer la API o mejorar el retrieval?** Son trabajos distintos y
compiten por el mismo tiempo. Recomendación: si va a haber **cualquier** consumidor que no seas tú,
**8.a antes que la Ola 6**. Un two-hop de +5 pp no sirve de nada si el primer usuario tumba el
servicio con un POST de 40 MB o consulta una colección que no existe y recibe un 500 con stack trace.
Si sigue siendo de un solo usuario, la Ola 6 primero y la 8.a se queda donde está en el ledger.

**D-6 — ¿Cuándo se ejecuta esto?** Hay 6 ítems pendientes de la Ola 4 y 5 de la Ola 3 en el ledger
vigente. Recomendación: **no abrir la Ola 5 hasta cerrar `4.1`–`4.3`, `3.1`+`4.6` y el nuevo `5.0`**,
por las razones de la sección 6. Meter símbolos en un payload cuya colección está rota es construir
sobre el corpus que hay que arreglar; y medir sin eval-set de `bsuite-repo` es declarar un resultado
sin instrumento (§3.5).

---

## 8. Lo que deliberadamente NO entra en el plan

- **Eliminar el resumen de negocio de la ingesta** (§2 del documento). Apaga la banda de peso 2.5 y,
  de paso, la fusión ponderada entera (con dos vectores el retriever cae a la fusión nativa de
  Qdrant). Coste medido: −12 pp de recall@10 con n=16 real, −37 pts con n=19 en el PoC. Sustituido
  por 5.b, que ataca el coste sin apagar la señal.
- **Chunker de XAML estructural** (era el ítem 5.a de este plan hasta esta revisión). `bsuite-repo`
  tiene **0** archivos `.xaml` sobre 2.031 `.cs`, y `micro-repo` tampoco tiene ninguno (§3.4). Era
  el ítem de mayor esfuerzo de la ola contra un lenguaje que el corpus objetivo no contiene: eso es
  ceremonia, por bien fundamentada que estuviera la técnica. **Condición exacta de reingreso:** que
  `Reyma.InnovApp` (79 `.xaml`, 896 `{Binding`, 68 `*ViewModel.cs`) se ingeste como colección y
  tenga eval-set con preguntas Vista→ViewModel. Sin esas dos cosas, un chunker de XAML no se puede
  ni verificar contra un número.
- **Agrupar todos los cambios de chunking en un solo salto de `ChunkingContract.Version`** (era la
  sección 5 de este plan). Era la respuesta a un coste de 19 h que no existe para cambios de
  encabezado (§4.1). Agrupar cuesta atribución de causa y no compra nada.
- **Umbral fijo de 0.20 sobre el top-1 del cross-encoder** (§4). Número inventado sobre un score que
  está medido como no invariante al `TopK`. Sustituido por `4.1`–`4.3` del ledger, que arreglan el
  score antes de calibrar el corte.
- **Respuesta canned "no está relacionada con..."** (§4). Regresión de un comportamiento ya
  corregido a propósito (`guardrail-banda-baja-conversacional.md`).
- **Reintroducir el rerank como mejora de recall** (§6 Fase 3). Ya está integrado y ya está medido
  que con los pesos calibrados no suma. La 7.c va en la dirección opuesta: dejar de encenderlo por
  defecto.
- **AST completo (Roslyn con `Compilation`, tree-sitter para todo lenguaje)** (§6 Fase 1). Ver D-4:
  se entra por lo barato y sólo se escala con un número que lo justifique.
- **Grafo de símbolos en un almacén aparte (Neo4j y compañía).** El filtro por payload de Qdrant con
  índice resuelve el salto de un nivel, que es el único que el caso de uso pide. Un grafo dedicado es
  un segundo sistema que hay que mantener sincronizado con la ingesta.
- **Chunker de SQL.** `ChunkType.SqlProcedure` también es un valor de enum muerto, y `.sql` también
  cae al fallback. Y como el XAML: `BusinessSuite.Xaf` tiene **0** archivos `.sql`. Ni hueco de
  chunker ni caso de uso. Se documenta y se deja.
- **Cobertura de tests por porcentaje, gates de CI sobre umbral, capas de abstracción "por si
  acaso".** Ceremonia. La red de seguridad que sí importa (tests de tabla con negativos
  adversariales, baselines con procedencia, `rag eval` reproducible) ya la construyó la Ola 1.
- **Autenticación con IdP / OAuth en la API.** Para un despliegue interno air-gapped, un API key por
  cabecera y rate limiting (8.a) cubren el riesgo real. Un IdP es un proyecto aparte.
- **La afirmación "0% de alucinación paramétrica".** No se adopta como objetivo declarado porque no
  es cierta y porque declararla desincentiva medirla. Lo que sí entra: la regresión permanente de
  bandas (8.d) y el arnés de 143 preguntas.

---

## 9. Apéndice: delta propuesto para el ledger *(no aplicado)*

Este documento **no** modificó `ejecucion-plan.estado.json`. El delta que habría que aplicar, para
revisión antes de tocarlo (cambia lo que ejecuta `/plan-siguiente`):

- **Ascender a bloqueantes** con una nota `bloquea`: `4.1`, `4.2`, `4.3` (bloquean guardrail);
  `3.1`, `4.6` (bloquean toda medición de two-hop); `5.0` (bloquea todo criterio de recall).
- **Corregir `2.2`**: el título dice "1042 líneas"; hoy son **680**
  (`RagGenerationService.cs`), porque `2.3` externalizó los prompts. Y su `verificacion` dice
  "las 89 preguntas de bsuite-repo": está bien para un A/B de *calidad*, pero hay que decir
  explícitamente que es el arnés de `infra/quality-baseline.py` y **no** recall, para que nadie
  vuelva a citar ese n=89 como si midiera retrieval (§3.5).
- **Nueva Ola 5** "Contrato estructural de símbolos": `5.0` (eval-set de `bsuite-repo`, bloqueante),
  `5.b`–`5.f`. `5.b` es el único con `multiagente: true`. **`horas_maquina` de `5.e` = 0,3**, no 19:
  el cambio es de encabezado y la caché acierta. `5.b` es el único ítem de la ola con horas de
  Ollama, y su presupuesto se cuenta antes de lanzarlo, no se estima.
- **Nueva Ola 6** "Two-hop en tiempo de consulta": `6.c` primero, luego `6.a`, `6.b`, `6.d`. El
  criterio de `6.a` va **pre-registrado dentro del ledger y expresado en preguntas ganadas/perdidas**,
  no en pp — con n≤80 los pp no son señal.
- **Nueva Ola 7** "Perfiles de colección": `7.a`–`7.c`.
- **Reetiquetar** `3.2` y `3.3` como Ola 8 (`8.a`, `8.b`) y añadir `8.c` (formato de 4 bloques),
  `8.d` (= `4.5` existente, sin duplicar), `8.e` (resiliencia en generación) y `8.f` (identidad de
  chunk sin ruta absoluta).
- **Nota de operación transversal**, no un ítem: toda `verificacion` que implique re-ingesta debe
  llevar "con `rag-api` detenido", como ya la lleva `4.6`. Es la mitigación de la corrupción de
  `summary-cache` del 2026-08-20.
- **Nuevo ítem menor**: borrar `nohup.out` de la raíz (artefacto de una corrida fallida de Spectre,
  6 líneas — `Unknown command 'inges'`) y añadirlo al `.gitignore` — hoy aparece como untracked en
  `git status`.
- **Nada se descarta del ledger vigente** por este plan.
