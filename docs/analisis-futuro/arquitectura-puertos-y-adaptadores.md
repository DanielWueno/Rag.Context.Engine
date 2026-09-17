# Arquitectura hexagonal (Ports & Adapters): qué de esto ya existe y qué falta

> Evaluación de si adoptar arquitectura hexagonal en RagEngine, contra el estado real del código.
> Pasó por una revisión adversarial con contexto limpio, que reordenó las prioridades y añadió
> cuatro grietas que la primera pasada no vio. Delta del ledger en el apéndice.

> **Reconciliación complementaria, 2026-09-12:** los pendientes accionables de §6
> pasan al ledger: opciones → `9.8`, RRF → `9.7`, métricas globales → `13.5`;
> manifiesto continúa en `5.f`. LightRAG amplía `9.1` con contexto/filtros obligatorios
> comprobables localmente y `9.4` conserva SQLite detrás del puerto. Postgres e identidad
> corporativa son capacidades pendientes, no prerrequisitos del refactor local.
> El análisis y sus decisiones originales se conservan como registro fechado.

## 1. Veredicto en una página

**No hay refactorización grande que hacer. Hay una decisión con consecuencia funcional viva, un
contrato que miente, y dos hosts que hablan con la infraestructura por encima del núcleo.**

Hexagonal es el patrón de Alistair Cockburn (2005). Su tesis es una: la aplicación no debe saber si
la conversación entra por HTTP, por CLI o por un test, ni si sale a Qdrant, a un archivo o a un
mock. Todo se expresa como *puertos* (interfaces que define el núcleo) y *adaptadores*
(implementaciones que viven fuera). La regla dura es la **dirección de la dependencia**: el núcleo
no apunta a nada; todo apunta al núcleo.

"Hexagonal para RAG" no es un patrón distinto. Es hexagonal genérico con los puertos que un RAG
suele tener: embedder, vector store, retriever, reranker, LLM, fuente de documentos. La parte
específica de RAG es elegir esa lista, y `Abstractions/` ya la tiene casi entera.

El patrón no aparece nombrado en [arquitectura.md](../arquitectura.md) porque ese documento lo
describe con vocabulario propio — "Capas y proyectos", `Abstractions/` + `Infrastructure/` — en vez
de con el nombre canónico. No es que no esté aplicado: es que no está nombrado.

**Cuidado con medir esto como un porcentaje de avance.** El reparto no es uniforme: los caminos de
**consulta** están bien abstraídos, y los de **escritura**, **caché de resúmenes** y
**administración** no lo están en absoluto. Hay diez puertos en `Abstractions/`, pero el vector
store, la caché SQLite, el `Kernel` de Semantic Kernel y las métricas no tienen ninguno, y los dos
hosts construyen infraestructura directamente.

Lo que este análisis rechaza es la refactorización nominal: renombrar carpetas a `Ports/` y
`Adapters/`, partir ensamblados por ortodoxia, o introducir indirección para permitir un cambio de
vector store que este proyecto no va a ejecutar. Lo que acepta es que la dirección de dependencias
hoy depende de la buena voluntad de quien escribe, y que ya se rompió en varios sitios sin que
nadie lo notara.

## 2. Qué ya está construido (con evidencia)

| Concepto hexagonal | Dónde vive hoy |
|---|---|
| Puertos secundarios (driven) | `Abstractions/` — `IVectorizationBrain`, `ISemanticRetriever`, `IReRanker`, `ISparseTokenizer`, `IIngestionScanner`, `IBusinessSummaryGenerator`, `IChunkingStrategy`, `IMetaIntentDetector`, `IIngestionPipeline`, `IRagGenerationService` |
| Adaptadores secundarios | `Infrastructure/` — `Onnx*`, `Qdrant*`, `FileSystemIngestionScanner` |
| Modelo de dominio | `Domain/` — sin un solo `using` externo |
| Capa de aplicación | `Services/Generation/`, `Pipeline/` |
| Adaptadores primarios (driving) | `RagEngine.Cli`, `RagEngine.Api` |
| Registro de servicios compartido | `Extensions/ServiceCollectionExtensions.AddRagEngineCore` |

Un acierto que conviene registrar porque es el que más proyectos fallan: **`Domain/` está limpio**.
Ninguno de sus once archivos importa Qdrant, ONNX, Semantic Kernel ni SQLite. El modelo es
independiente de la infraestructura de hecho, no sólo de intención.

Sobre el registro de servicios, la primera pasada concluyó "esto ya está bien" y era demasiado
generoso. El **código** de registro no está duplicado (`Api/Program.cs:59-60` y `Cli/Program.cs:51-54`
llaman ambos a `AddRagEngineCore` / `AddRagEngineGeneration`), pero la **configuración** sí — ver 3.7.

## 3. Las grietas reales (con evidencia)

Ordenadas por consecuencia, no por elegancia.

### 3.1 La API reimplementa la regla de banda

`Api/Program.cs:181-187` (`ShouldSuppressSources`) recalcula la regla de banda baja que vive en
`ConfidenceGate.Assess`, y la consume en `:429` y `:526`. Las dos expresiones, lado a lado:

```
ConfidenceGate.Assess:   chunks.Count == 0 || (useReRanking && topScore < LowConfidenceThreshold)
ShouldSuppressSources:   isMetaIntent     || (rerank && results.Count > 0 && top < LowConfidenceThreshold)
```

**Los umbrales NO divergen:** la API recibe `RagGenerationOptions` y lee de ahí
`LowConfidenceThreshold`, el mismo objeto de configuración que usa el gate. Una recalibración las
mueve a las dos a la vez. Lo duplicado es **la regla**, no el número.

El coste es de mantenimiento, y es del tipo que se cobra tarde: cada cambio en la forma del gate hay
que replicarlo a mano en la API, y nada avisa si no se hace. El caso concreto que viene es la
condición `useReRanking` — el ítem 4.2 registra en su campo `bloquea` que romper la dependencia
gate→rerank es trabajo previsto; cuando eso ocurra en `ConfidenceGate`, la copia de la API seguirá
teniendo su `rerank &&`.

Ya está en el ledger como `4.7-regla-de-banda-duplicada-en-la-api`, y su `bloqueado_por` apunta a
`4.3-recalibrar-umbrales-banda`, **que ya está en `hecho`**: el ítem está desbloqueado.

### 3.2 El contrato de score miente, y ya son cuatro significados

`Domain/RetrievalResult.SimilarityScore` es un `float` cuyo significado depende del camino:

1. similitud coseno cruda (prefetch denso, `QdrantSemanticRetriever.cs:111-112`),
2. RRF nativo de Qdrant o RRF ponderado manual (`:118`, `:240`),
3. sigmoide del logit del cross-encoder (`Abstractions/IReRanker.cs:22-23`),
4. **re-puntuación en lote de 1 sobre la posición #0**, introducida por el ítem 4.2
   (`OnnxCrossEncoderReRanker.cs:93-110`, activada en ambos `appsettings.json`).

El cuarto rompe un invariante que nadie declaró: el comentario de `OnnxCrossEncoderReRanker.cs:104-106`
advierte que la lista devuelta por `ReRankAsync` **ya no está ordenada de forma monótona por
`SimilarityScore`** — el resultado #1 puede tener menos score que el #2. Cualquier consumidor que
"normalice" reordenando por ese campo revierte el ítem 4.2 sin darse cuenta.

`arquitectura.md` ya advierte que las escalas no son comparables, y `ConfidenceGate.cs:47-57,69` se
defiende con un `useReRanking &&` y diez líneas de comentario. Nada de eso lo dice el tipo.

Es la grieta con coste ya pagado: es la razón de los ítems 4.1 a 4.3 y de
[gate-de-confianza-score-inestable-y-fuga-de-prompt.md](gate-de-confianza-score-inestable-y-fuga-de-prompt.md).
Y el ítem pendiente `4.8` (tests de comportamiento de los colaboradores de generación) no se puede
escribir bien contra una semántica que no está fijada.

> **Cerrada por el ítem `4.9-contrato-explicito-del-score` (2026-08-31).** `RetrievalResult`
> lleva ahora un `ScoreScale` obligatorio que declara cuál de las escalas transporta, y
> `ScoreScale.IsComparableAcrossQueries()` responde la única pregunta que los consumidores
> hacían a ojo. `ConfidenceGate.Assess` perdió su parámetro `useReRanking`: la escala la lee del
> dato, no de una bandera que venía de otra capa. El invariante de orden del ítem 4.2 está
> documentado en el tipo y fijado en `tests/RagEngine.Core.Tests/RetrievalScoreContractTests.cs`,
> con la lista no monótona reproducida contra el modelo real. Ver la sección «El contrato del
> score» de [arquitectura.md](../arquitectura.md).

### 3.3 Los hosts construyen infraestructura por encima del núcleo

No es sólo que falte un puerto de escritura: es que **los dos adaptadores primarios hablan con
Qdrant directamente**.

- `Api/Program.cs:223` y `:300` inyectan `Qdrant.Client.QdrantClient` **en los delegates de los
  endpoints** (`/api/health`, `/api/collections`).
- `Cli/Commands/StatusCommand.cs:29-32` inyecta `QdrantClient` **y** `QdrantVectorStore`.
- `Cli/Commands/DoctorCommand.cs:21,31,223`.

Esto lo hace posible `ServiceCollectionExtensions.cs:95-99`, que registra `QdrantClient` como
servicio público del contenedor.

**Consecuencia para el plan:** introducir un `IVectorStore` en Core **no arregla nada** mientras los
hosts sigan resolviendo `QdrantClient` del contenedor. La grieta se cierra por el lado de los hosts
o no se cierra.

### 3.4 No existe puerto para el vector store, y su tipo de cursor es de gRPC

`QdrantVectorStore` es `public sealed class` sin interfaz (`QdrantVectorStore.cs:15`), registrada por
tipo concreto (`ServiceCollectionExtensions.cs:102`) e inyectada directamente en la capa de
aplicación (`DefaultIngestionPipeline.cs:54,65`).

Sus tipos anidados aparecen en firmas del pipeline: `ExistingResumenState`
(`DefaultIngestionPipeline.cs:327-328,332`, definido en `QdrantVectorStore.cs:249`) y
`PendingResumenPoint` (`:433,:529`, definido en `:506`). De ahí los `using Qdrant.Client` /
`Qdrant.Client.Grpc` de `DefaultIngestionPipeline.cs:5-6`, que son síntoma y no hallazgo aparte.

**El detalle que decide el diseño:** `ScrollPendingResumenAsync` devuelve un `PointId` de gRPC
(`QdrantVectorStore.cs:514`) y el pipeline lo usa como cursor de paginación
(`DefaultIngestionPipeline.cs:444`). No basta con mover los records anidados: hace falta **un tipo de
cursor de dominio**, o el puerto será Qdrant con otro nombre.

Y `QdrantVectorStore` no es sólo de escritura — también lee (`InspectCollectionSchemasAsync:169`,
`HasSummaryVectorAsync:143`, `ScrollPendingResumenAsync:514`, `CountResumenPendingAsync:425`), y esas
lecturas las consumen `StatusCommand` y `DoctorCommand`. Meterlo todo en una interfaz produce un
puerto de doce métodos que sólo Qdrant puede implementar: la abstracción que no abstrae. Van dos
puertos separados — escritura de ingesta, y diagnóstico/administración — o ninguno.

**Evidencia de ejecución, 2026-09-17 (9.1):** los commits `45bdbce` y `9690c49`
introdujeron `IVectorStoreWriter`/`IVectorStoreAdmin`. En lugar de exponer otro tipo
de cursor, el puerto ofrece `IAsyncEnumerable<PendingResumenPoint>` con IDs `Guid`;
el offset gRPC queda dentro del adaptador. Una nueva invocación vuelve a enumerar
`resumen_pending`, sin guardar un offset que pueda saltarse fallos anteriores.
`VectorStoreResumeTests` verifica en Qdrant real 207 puntos, interrupción tras 103
resúmenes, reanudación con instancias nuevas y conservación exacta de vectores tras
otro upsert; incluye el control adversarial que sí pierde el vector al omitir el
estado anterior. Esta alternativa satisface el objetivo de separar dependencias,
sin añadir un cursor que el consumidor no necesita.

En la verificación guardada en `94c8755`, la ficha quedó **`en_curso`**, no cerrada:
dos parejas A/B sobre cuatro eval-sets
conservan hit@10, pero los empates del ranking nativo cambian hit@1/hit@5 entre
corridas. También se observa variación al repetir el control pre-9.1; no se atribuye
automáticamente al refactor. Se conservan ambas parejas y el fallo del criterio
estricto, sin cambiar anclas ni normalizar empates para forzar un PASS.
Evidencia y comparador históricos: `docs/eval/quality/9.1/`. Comando de aquella aceptación:
`python3 docs/eval/quality/9.1/verify.py --run-tests`; devuelve error mientras
se evalúan esas capturas, aunque los contratos pasen. Está ligado a sus hashes;
no sirve para certificar automáticamente otra versión del producto.

**Relevo autorizado, 2026-09-17:** se registra
`9.1.1-desempate-determinista` como una ficha plana previa al padre, con criterio,
rollback y protocolo A/A + B/B + A/B en el ledger. La preparación no ejecuta la
corrección. `9.1` pasa a **`bloqueado` por ese ID** para permitir trabajar el hijo
en una conversación nueva sin que la regla de retomar `en_curso` seleccione antes
al padre. La cadena real es `7.b` → `9.1.1` → `9.1` → `9.2`, sin dependencia
del hijo hacia el cierre de su padre. Usar IDs completos, no el prefijo `9.1`.

El subítem debe resolver los empates antes de los cortes relevantes, no ordenar
solo un TopK ya truncado; preservará la precedencia de scores distintos y el
aislamiento por autorización. Las nuevas mediciones aplicarán la misma política
al control pre-refactor y al candidato, con tres réplicas por brazo y los cuatro
sets congelados. Se conservarán las capturas fallidas anteriores sin normalizarlas
para hacerlas pasar; la evidencia nueva tendrá su propio directorio.
Cerrar el hijo **no cierra 9.1 automáticamente**: después, en otra conversación,
se revalidarán sus contratos y se enlazará un comando de aceptación real a la
nueva evidencia. No se ejecuta `9.2` durante este relevo.

**Ejecución de 9.1.1, 2026-09-17:** desempate por UUID D minúsculas ordinal,
solo con scores exactamente iguales y antes de cada corte. Las fuentes densa,
dispersa y resumen usan prefijos exactos ampliados hasta completar el empate;
si el techo de 32 768 candidatos no permite demostrar completitud, fallan.
La fórmula RRF de dos vectores conserva la aritmética nativa (float32,
`1/(2 + rango_base_cero)`), pero se calcula en el adaptador para controlar
los desempates; pesos y k de la fusión ponderada no cambian. Two-hop y reranker
comparten la regla, sin ordenar por el score estable del gate. Eval incorpora
UUID, posición, score/escala de ranking, latencia y contadores de consultas y
candidatos devueltos. Un error de búsqueda o expansión no se convierte en vacío.

Evidencia nueva: `docs/eval/quality/9.1.1/`. `control.patch` y `candidate.patch`
versionan los cambios completos sobre `bb9b11f` y `9690c49`; el protocolo prueba
identidad del código de ranking/instrumentación, con adaptaciones mecánicas
del store y mapeo anteriores a los puertos. Se conservan cuatro datasets,
145 preguntas (133 ancladas, 12 sin anclas), modelos, configuración y hashes
de payload/vectores. Las tres réplicas de cada brazo tienen idénticos IDs,
orden y scores; A/B no cambia ningún hit-any/full por pregunta en 1/3/5/10.
Los contratos cubren 257 empatados (más que prefetch 40/120), inserciones normal,
inversa y permutada, candidatos prohibidos con UUID menor, cortes 1/5/10,
negativos y control adversarial de truncamiento.

`pre-fix/pre-fix.trx` conserva seis fallos antes de cambiar producto. Ese fixture
inicial probaba ambos schemas con denso/sparse; la cobertura posterior exige
además el vector de resumen realmente poblado. `initial/` conserva íntegra
la primera medición, también estable. La segunda vuelve a congelar ambos brazos
tras ampliar los negativos de autorización y corregir un comentario XML, sin
cambiar ranking, preguntas ni umbrales; todas sus capturas coinciden también
con las de `initial/`. No se eligió una pareja favorable.

No se oculta el cambio frente al historial inestable: `verification.json`
detalla por pregunta ganancias en @1 y pérdidas en @5. Permanecen las pérdidas
de «importar auditorías desde Excel» y «pasar un ticket a Mesa de Ayuda», y
respecto de la segunda pareja histórica, «registrar avance/actividad».
Hit@10 no cambia. No es una mejora de recall demostrada: es estabilidad y
equivalencia bajo la misma política. Dos rondas completas de captura costaron
aproximadamente 222 segundos, incluidos builds y fingerprints; sin LLM,
reingesta ni escrituras en índices servidos. Los contadores de candidatos
incluyen repeticiones y no miden operaciones internas de distancia en Qdrant.

Aceptación manual (no ejecutada por hooks):
`dotnet build --no-restore --nologo -warnaserror && python3 -m unittest discover -s docs/eval/quality/9.1.1 -p 'test_accept.py' && DOTNET_PROCESSOR_COUNT=1 python3 docs/eval/quality/9.1.1/accept.py --run-tests`.
El límite de concurrencia evita solapar el cierre global de ONNX del harness API
con tests que lo usan: las ejecuciones paralelas abortaron y no se contaron como
éxito. La aceptación conserva los 72 contratos, sin omisiones, más seis tests
del comparador. El problema de lifetime global queda registrado, no corregido
fuera del alcance. **9.1 sigue bloqueado** hasta su reentrada y aceptación propias
en otra conversación; 9.2 no se ejecutó.

### 3.5 `IRagGenerationService` no abstrae: obliga al host a duplicar el paso anterior

`Api/Program.cs:397-410` y `:506-517` hacen **dos llamadas independientes a `ISemanticRetriever` por
request**: una para poder devolver las fuentes, y otra dentro de `AskStreamingAsync`. El comentario
de `:391-396` lo admite y lo justifica por no tocar el contrato público del servicio.

La causa es que el puerto devuelve `IAsyncEnumerable<string>` y nada más. Al no devolver ni las
fuentes ni el veredicto del gate, fuerza al host a repetir el retrieval **y** a recalcular la banda
— es decir, es la causa raíz de 3.1. Cerrar 4.7 sin cerrar esto deja el segundo `SearchAsync` por
request en pie.

Dos secuelas del mismo contrato insuficiente:

- `Api/Program.cs:430` y `:563` comparan la respuesta contra `RagGenerationService.NoContextFallbackMessage`,
  una constante de la **clase concreta**. `RagGenerationService.cs:52-57` lo reconoce: es "parte del
  contrato hacia los hosts", un contrato que no está en la interfaz.
- `Abstractions/IRagGenerationService.cs:58-67` tiene nueve parámetros, entre ellos `topK`,
  `minimumScore = 0.10f` y `useReRanking` — perillas de *retrieval* en el puerto de *generación*, con
  defaults que repiten el `0.10` de `Domain/RetrievalResult.cs:30` y de `Api/Program.cs:333-336,480-483`.
  Cuatro sitios con la misma constante.

### 3.6 Tres adaptadores archivados como servicios de aplicación — y dos sin puerto

| Archivo | Dependencia externa | ¿Tiene puerto? |
|---|---|---|
| `Services/Summary/OllamaBusinessSummaryGenerator.cs:6-10` | SK, conectores OpenAI, Polly | Sí (`IBusinessSummaryGenerator`) |
| `Services/Summary/SummaryCache.cs:1,16` | `Microsoft.Data.Sqlite` | **No** |
| `Services/Generation/ChatAnswerStreamer.cs:3-4,21-25` | Semantic Kernel, `Kernel` concreto | **No** (`internal`) |

Mover los tres de carpeta suena a la corrección obvia y es, para dos de ellos, **puro cosmético**:
`SummaryCache` es una clase concreta de la que dependen directamente `GenerationContextAssembler.cs:23,42`,
`DefaultIngestionPipeline.cs:46,71` y `Api/Program.cs:201,323,376,468`. Cambiar el árbol de
directorios no mueve ni una flecha de dependencia. O se extrae `ISummaryCache`, o esto no es trabajo.

`ChatAnswerStreamer` está aislado a propósito — [arquitectura.md](../arquitectura.md) lo documenta
como "única pieza que toca el `Kernel`" — y es `internal` con fábrica explícita en
`GenerationServiceExtensions.cs:121-123`. Ahí el defecto es de ubicación, no de diseño.

### 3.7 La configuración de los hosts diverge en silencio

`src/RagEngine.Api/appsettings.json` **no tiene sección `RetrievalFusion`**;
`src/RagEngine.Cli/appsettings.json` sí (`WeightCodigo: 1.0`, `WeightSparse: 1.3`, `WeightResumen: 2.5`,
`RrfK: 60`).

**Hoy no hay bug:** los defaults de `RetrievalFusionOptions.cs:16-19` son exactamente esos mismos
pesos calibrados, así que ambos hosts fusionan igual. Es una trampa, no un defecto: la próxima
recalibración editando el appsettings del CLI dejará a la API con los defaults viejos, sin ruido.

Al lado, el problema simétrico: `LowConfidenceThreshold: 0.05` y `HighConfidenceThreshold: 0.60`
están **duplicados literalmente** en los dos archivos. Recalibrar exige editar dos ficheros o
desincronizar los hosts — el mismo modo de fallo de 3.1, en otro sitio.

Y configuración muerta: la sección `Ingestion` del CLI declara `DefaultCollection`, `RepositoryName`
y `BatchSize`, pero `IngestionOptions` (`Domain/IngestionTypes.cs:8-23`) sólo enlaza
`MaxConcurrentResumenCalls` y `ResumenCachePath`. Tres claves que no hacen nada y que un operador
razonablemente creería que sí.

### 3.8 No hay barrera — y un test de arquitectura, tal cual, tampoco la pondría

`RagEngine.Core.csproj:10-27` declara Roslyn, SQLite, ONNX Runtime, Semantic Kernel y Qdrant.Client
en un **ensamblado único**. No hay separación de compilación entre capas, y no hay test que valide la
dirección: `tests/RagEngine.Core.Tests/CompositionRootTests.cs:53,65` verifica que el grafo de DI
resuelve, no quién depende de quién.

Dos correcciones sobre la recomendación ingenua:

- **Un test de arquitectura en el proyecto actual es estructuralmente incapaz de ver las peores
  violaciones.** `RagEngine.Core.Tests.csproj:11` referencia **sólo** `RagEngine.Core`; las grietas de
  3.3 están en `Api` y `Cli`. Cubrirlas exige un proyecto de tests nuevo que referencie los tres
  ensamblados — y que, por serlo, no verá los tipos `internal` que `RagEngine.Core.csproj:36-38`
  expone únicamente a `RagEngine.Core.Tests`.
- **Existe una barrera más dura y más barata que un test.** Ni `Api.csproj` ni `Cli.csproj` declaran
  `Qdrant.Client`: compilan contra él porque Core lo filtra transitivamente. Poner
  `PrivateAssets="all"` en esa `PackageReference` rompe la compilación de los hosts **hoy, en el
  build**, sin escribir una línea de test. Eso convierte 3.3 en un error de compilador en vez de en
  una convención.

## 4. Qué el patrón NO compra aquí

- **Poder cambiar de vector store o de vectorizador.** Es el argumento estrella de la literatura y en
  este proyecto es falso: cambiar el modelo de embeddings exige re-ingesta completa (~19 h medidas).
  La sustituibilidad en caliente no se va a ejercer nunca.
- **Testabilidad.** Ya hay puertos mockeables, 122 tests y golden masters. La ganancia marginal es
  cercana a cero.
- **Claridad documental.** Renombrar `Abstractions/` a `Ports/` es churn sin lector que lo pida.
- **Partir `RagEngine.Core` en varios ensamblados.** Es la forma ortodoxa de imponer la dirección, y
  es cara: toca todos los `csproj`, el registro de servicios y los tests, en medio de una Ola 4
  abierta con un gate recién calibrado. Además rompería en silencio
  `ServiceCollectionExtensions.cs:156-166`, que descubre las estrategias de chunking por reflexión
  sobre `typeof(IChunkingStrategy).Assembly.GetTypes()`: registraría menos estrategias sin fallar.
  `PrivateAssets` más un test da la misma garantía observable a una fracción del coste.

## 5. Hallazgos colaterales

**5.1 `CLAUDE.md` apunta a un directorio que ya no existe.** Las líneas 20 y 29 documentan
`infra/arnes/` como el arnés de ejecución de planes e instruyen `python3 infra/arnes/ledger_path.py`
para leer el ledger. Ese directorio se eliminó en `e55279e` ("refactor(arnes): consumir el plugin en
vez de la copia vendorizada"). El comando documentado está roto: el arnés es el plugin `arnes-plan`.

**5.2 El ítem bloqueado 3.5 no tiene condición de reingreso como campo.** `3.5-portabilidad-x64` está
correctamente bloqueado y su `razon_bloqueo` es exhaustivo: implementado y verificado en arm64
(122/122 tests, `rag doctor` confirma la resolución del binario), bloqueado sólo porque la rama x64
nunca se probó contra un runtime x64 real. Falta que la condición que lo desbloquea sea un campo
legible, no un texto que hay que leer entero. El precedente existe: 5.a nace descartado con su
condición de reingreso escrita.

## 6. Registrados, no planificados

Grietas reales que no justifican un ítem hoy. Quedan aquí para no re-descubrirlas.

- **Opciones repartidas por cuatro capas sin regla.** `OnnxBrainOptions`, `CrossEncoderOptions`,
  `QdrantOptions` y `RetrievalFusionOptions` en `Infrastructure/`; `IngestionOptions` en
  `Domain/IngestionTypes.cs:8` (config de host dentro de Dominio); `RagGenerationOptions` y
  `MetaIntentOptions` en `Services/Generation/`; `OllamaOptions` en
  `Extensions/GenerationServiceExtensions.cs:20`. Efecto medible: `OllamaBusinessSummaryGenerator.cs:13`
  hace `using RagEngine.Core.Extensions` — un adaptador importando la capa de composición.
- **RRF ponderado duplicado entre PoC y producción.** `QdrantSemanticRetriever.cs:180-184` documenta
  que `SearchWeightedFusionAsync`/`ToRankMap` son un port manual de
  `poc/RagEngine.Poc.FreeSearch/RecallEvaluator.cs`. El PoC es lo que se usa para calibrar los pesos
  que consume producción: la deriva entre ambos está garantizada, sólo falta saber cuándo.
- **Métricas como estático global.** `Diagnostics/RagEngineMetrics.cs:9-31` es una `static class` con
  instrumentos `static readonly`, invocada desde `QdrantSemanticRetriever.cs:162,171` y
  `Api/Program.cs:310-314`. No es inyectable ni sustituible en test, y es el tipo de dependencia
  oculta que un test de arquitectura basado en `using` no detecta.
- **`Domain/CollectionManifest.cs:9` no lo usa nadie.** Ya está en el ledger como
  `5.f-revivir-collection-manifest`.

## 7. Plan

Ningún ítem consume tiempo de máquina significativo. El orden importa más que el contenido: **la
barrera se instala después de tapar, no antes** — un test de arquitectura escrito primero sólo
produce una lista de excepciones que nadie vacía.

### Añadidos a olas existentes

- **Ola 4 · `4.9-contrato-explicito-del-score`** — grieta 3.2. Va en Ola 4 y no en una ola nueva
  porque el criterio de salida de Ola 4 es literalmente *"el score que decide la banda no cambia
  según el TopK"*. Precondición: `4.5`. Conviene antes de `4.8`, que no se puede escribir bien contra
  una semántica sin fijar.
- **Ola 8 · `8.g-configuracion-divergente-entre-hosts`** — grieta 3.7.

### Ola 9 — Dirección de dependencias

*Criterio de salida: ningún host construye un cliente de Qdrant, y quien lo intente rompe el build,
no una convención.*

- **9.1 — Puertos del vector store con cursor de dominio.** Grieta 3.4. Dos puertos, no uno:
  escritura de ingesta y diagnóstico/administración. Incluye inventar el tipo de cursor que hoy es
  `PointId`.
- **9.2 — Ningún host resuelve `QdrantClient`.** Grieta 3.3. Verificación mecánica:
  `PrivateAssets="all"` sobre la `PackageReference` de Qdrant.Client en `RagEngine.Core.csproj` y la
  solución compila. Depende de 9.1.
- **9.3 — `IRagGenerationService` devuelve resultado estructurado.** Grieta 3.5: fuentes y veredicto
  del gate en el retorno, un solo `SearchAsync` por request, y `NoContextFallbackMessage` fuera de la
  clase concreta. Interactúa con `4.7`: conviene cerrar 4.7 de forma que no cierre esta puerta.
- **9.4 — Extraer `ISummaryCache` y reubicar los adaptadores.** Grieta 3.6. El puerto es el trabajo;
  mover los archivos es la consecuencia.
- **9.5 — Proyecto de tests de arquitectura sobre los tres ensamblados.** Grieta 3.8. Congela lo que
  9.1–9.4 ya arreglaron. Va al final por diseño.
- **9.6 — Limpiar el vocabulario de los puertos.** Incluye `Domain/RetrievalResult.cs:38-39`,
  `Abstractions/ISemanticRetriever.cs:6,8`, `Abstractions/IRagGenerationService.cs:19,26,43` (donde el
  `:26` además dice "cosine" sobre un score que no lo es en dos de los cuatro caminos), y sobre todo
  `RagGenerationService.cs:253`, que le dice al **usuario final** *"Please ensure Qdrant is running"*.

### Ola 10 — Higiene del arnés y del ledger

*Criterio de salida: la documentación de proceso describe el proceso que existe, y ningún ítem parado
depende de que alguien recuerde por qué.*

- **10.1 — `CLAUDE.md` apunta al plugin, no a `infra/arnes/`.** Hallazgo 5.1.
- **10.2 — Condición de reingreso explícita para 3.5.** Hallazgo 5.2.
- **10.3 — Paso de plan a ledger con cómputo local.** Precondición: `4.8` cerrado, y dos
  reconciliaciones manuales documentadas (la del 2026-08-24 y la de este documento) como
  especificación de entrada. Automatizar el mapeo con una sola muestra es diseñarlo a ciegas.

## 8. Riesgos de ejecución

Los que pueden destruir trabajo, no los que rompen el build.

- **El cursor de 9.1 es el punto delicado.** `ScrollPendingResumenAsync` devuelve `PointId?` y el
  pipeline lo usa para reanudar (`DefaultIngestionPipeline.cs:444`). Un cursor de dominio mal hecho
  rompe la reanudación de la Fase 2, que es lo que evita reprocesar ~19 h de resúmenes.
- **`UpsertBatchAsync` tiene semántica destructiva documentada** (`QdrantVectorStore.cs:241-248,340-341`):
  Qdrant reemplaza el punto entero, así que omitir `SummaryVector` **borra resúmenes ya generados**.
  Una firma reescrita para caber en un puerto genérico puede perder ese detalle sin fallar.
- **Tocar `ResumenCachePath` o `prompt_version`** (`OllamaBusinessSummaryGenerator.ComputePromptVersion:149`)
  al pasar por 9.4 invalida la caché entera → regeneración de ~19 h. Hay antecedente de corrupción de
  esa caché por WAL compartido entre host y contenedor.
- **9.6 puede mover un mensaje asertado.** `RagGenerationService.cs:253` puede estar en golden masters
  o en el arnés de calidad; `RagEngineMetrics.cs:17` es la `description` de una métrica publicada y
  puede mover dashboards. Verificar `tests/GoldenMaster/` antes.
- **4.9 puede romper el contrato JSON.** Si el contrato de score se implementa partiendo el campo,
  toca `Api/Contracts.cs:75,95,112` (`SourceDto.Score`) y con ello el front de `wwwroot`; además deja
  incomparables los baselines de `rag eval` (`Cli/Infrastructure/EvalProvenance.cs`) y los
  `GoldenMaster/*.json`.
- **El riesgo silencioso de 4.9:** que al "dejarlo consistente" alguien restaure un
  `OrderByDescending(SimilarityScore)` y revierta el ítem 4.2, devolviendo la inestabilidad por TopK.

## 9. Decisiones que tienes que tomar tú

1. **¿`4.7` se ejecuta ya, o se espera a `9.3`?** 4.7 está desbloqueado y cuesta 0,2 h, pero 9.3 es su
   causa raíz. Hacer 4.7 primero es correcto si su implementación consume el veredicto del gate en vez
   de inventar otro camino; hacerlo mal deja dos arreglos donde cabía uno. No es urgente: los
   umbrales son compartidos (ver 3.1), así que no hay nada rompiéndose hoy que justifique adelantarlo
   sobre el orden del ledger.
2. **¿`9.2` se verifica con `PrivateAssets` o sólo con el test de `9.5`?** `PrivateAssets` es más duro
   y más barato, pero convierte la grieta en un build roto hasta que 9.1 y 9.2 cierren — no se puede
   dejar a medias entre commits.
3. **Partir `RagEngine.Core` en ensamblados** queda fuera del plan (sección 4). Si el test de 9.5
   empieza a acumular excepciones en vez de perderlas, esa es la señal de reabrir la decisión.

## 10. Lo que deliberadamente NO entra

- Renombrar `Abstractions/` a `Ports/` y `Infrastructure/` a `Adapters/`.
- Partir `RagEngine.Core` en varios ensamblados.
- Un puerto por cada dependencia externa por simetría: `Utilities/`, `Diagnostics/` y los chunkers no
  lo necesitan.
- Abstraer el `Kernel` de Semantic Kernel detrás de un puerto propio de LLM: `ChatAnswerStreamer` ya
  cumple esa función y otro puerto sería indirección sobre indirección.

## 11. Apéndice: delta del ledger *(aplicado el 2026-08-31)*

Once ítems nuevos. Nada del ledger vigente se descarta ni se reabre.

| Ola | Ítem | Modelo / esfuerzo | h máquina |
|---|---|---|---|
| 4 | `4.9-contrato-explicito-del-score` | opus / high | 0.3 |
| 8 | `8.g-configuracion-divergente-entre-hosts` | sonnet / low | 0 |
| 9 | `9.1-puertos-del-vector-store` | opus / high | 0.3 |
| 9 | `9.2-hosts-sin-qdrantclient` | sonnet / medium | 0 |
| 9 | `9.3-resultado-estructurado-de-generacion` | opus / high | 0.3 |
| 9 | `9.4-puerto-de-cache-de-resumenes` | sonnet / medium | 0 |
| 9 | `9.5-tests-de-arquitectura` | sonnet / medium | 0 |
| 9 | `9.6-vocabulario-de-puertos` | haiku / low | 0 |
| 10 | `10.1-claude-md-apunta-al-plugin` | haiku / low | 0 |
| 10 | `10.2-condicion-de-reingreso-de-3.5` | haiku / low | 0 |
| 10 | `10.3-plan-a-ledger-con-computo-local` | sonnet / high | 0 |
