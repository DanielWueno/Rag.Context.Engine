# Motor de embeddings e ingesta como servicio independiente para el equipo

Fecha: 2026-10-19 · Estado: análisis/planteamiento. Sin repositorio creado, sin código
movido, sin conexión a ningún servidor.

## 1. Decisión y origen

Petición del usuario del 2026-10-19: ahora hay permiso para usar un servidor de IA de
la empresa, y la tarea es aprovechar al 100 % la parte de embeddings e ingesta del
motor, exponiéndola como API para todo el equipo y quien la necesite. Si hace falta,
generar un fork para no mezclar ese "músculo" con proyectos distintos (vista,
analíticas/paneles).

Aclarado en esta conversación:

- **Servidor destino:** no es Servidor.IA (Mac mini, acotado a Ollama) ni reyma-dev
  (gobernado por la ola 21/23, aún bloqueado). Es **otro servidor de la empresa, sin
  ficha ni análisis previo**, con acceso por SSH previsto pero **no otorgado todavía**.
  El trabajo de esta ola es preparación offline para no "llegar en cero" cuando el
  acceso exista, no una migración ya autorizada.
- **Fork:** repositorio independiente nuevo, mismo patrón que `Mcp.ContextForge`
  (`10.8-separar-servidor-contextforge-del-consumo-rag`): local, sin remote, sin
  publicación ni despliegue hasta nueva decisión explícita.
- **Siguiente paso confirmado por el usuario:** redactar esta ficha antes de tocar
  código.

## 2. Qué existe hoy (evidencia)

| Componente | Estado actual | Evidencia |
|---|---|---|
| Endpoints de red de `RagEngine.Api` | `/api/health`, `/api/collections`, `/api/search`, `/api/ask`, `/api/ask/stream`. **No hay endpoint de ingesta ni de embeddings puros.** | `src/RagEngine.Api/Program.cs` |
| Ingesta | Solo por CLI (`rag ingest`, `RagEngine.Cli`), nunca por API. | `src/RagEngine.Cli` |
| Embeddings (`OnnxBrain`) | ONNX Runtime en proceso; se usa internamente para ingesta y para la consulta en `/api/search` y `/api/ask`. No existe un servicio de embeddings separado. | `RagEngine.Core.csproj`, `viabilidad-computo-remoto.md` §2 |
| Selección de binario ONNX por arquitectura | Resuelta y verificada en x64 y arm64. | `3.5-portabilidad-x64`, hecho |
| Arquitectura/SO del servidor nuevo | **No confirmada.** Condiciona qué binario ONNX portar y si aplica sin cambios. | Pendiente de SSH |

Decisión vigente del 2026-09-23 (`mac-mini-servidor-ia-dedicado.md`): Qdrant y
`rag-api` (que hoy empaqueta embeddings + ingesta + retrieval + generación en un solo
proceso) se decidió sacarlos del Mac mini hacia infraestructura de la empresa. Ese
traslado ya tiene vehículo propio: la **ola 21** (Docker x64 en DSOFT03 →
`21.4-k8s-de-pruebas` en reyma-dev, bloqueado). La ola 21 mueve el `rag-api`
**completo**, generación RAG incluida — no es lo mismo que separar solo
embeddings/ingesta como servicio de plataforma para otros consumidores.

## 3. Qué se entiende por "puro músculo" (propuesta a confirmar, no decidida)

| Se separaría al fork | Se queda en RagEngine |
|---|---|
| `OnnxBrain` (embeddings) | Generación RAG (LLM vía Servidor.IA) |
| Pipeline de ingesta (chunking, hash de contenido, upsert a Qdrant) | Gate de confianza, caché de resúmenes de negocio |
| Posiblemente `CrossEncoder`/rerank, si otros consumidores lo necesitan | Historial/conversación, modo Simple, SSE |
| — | Consola de administración (`13.6`, bloqueada), MCP/ContextForge (ola 23), métricas a Grafana (`24.4`) |

Si se confirma este corte, RagEngine dejaría de tener pipeline propio de
embeddings/ingesta y pasaría a ser **un cliente más** del nuevo servicio — el mismo
rol que tendría cualquier otro equipo. Eso cambia el plan de la ola 21: en vez de
portar embeddings/ingesta junto con el resto de `rag-api`, los consumiría en remoto.
Esto no está decidido; es la pregunta más importante abierta.

## 4. Preguntas abiertas (decisión del usuario, no del análisis)

1. **Relación con la ola 21/21.4:** ¿el fork sustituye el pipeline propio de
   RagEngine, o RagEngine conserva el suyo y el fork es solo para otros equipos?
2. **Multi-tenencia:** ¿colecciones por equipo en un Qdrant compartido, o un Qdrant
   propio del fork, separado del de RagEngine?
3. **Contrato de API:** ¿solo `texto → vector` (embeddings puros), el pipeline
   completo `documento → colección indexada` (ingesta), o ambas superficies por
   separado?
4. **Autenticación:** hoy no hay IdP corporativo conectado (`12.2` sigue bloqueado).
   No se expone "a todo el equipo y quien lo necesite" sin algún control de acceso,
   aunque sea simple al inicio.
5. **Arquitectura/SO real del servidor** — pendiente del acceso SSH.
6. **Nombre y ubicación del repositorio nuevo.**

## 5. Vector store: Qdrant hoy, Milvus en las pruebas del administrador del servidor

Añadido durante esta misma conversación: el usuario reporta que el administrador del
servidor nuevo usa Milvus en sus pruebas, mientras que RagEngine usa Qdrant hoy. Antes
de "formalizar" hay que decidir cuál sirve el servicio compartido — **no es una
decisión técnica menor ni debe resolverse por preferencia del administrador sin
comparar**, igual que `23.4-comparar-contextforge-con-http` comparó gateway contra HTTP
directo antes de adoptar uno.

| Evidencia actual (Qdrant) | Fuente |
|---|---|
| Puertos ya separados: `ISemanticRetriever`, `IVectorStoreWriter`, `IVectorStoreAdmin` en `RagEngine.Core.Abstractions`, con `QdrantSemanticRetriever`/`QdrantVectorStore` como único adaptador. | `9.1-puertos-del-vector-store` (hecho), `9.2-hosts-sin-qdrantclient` (hecho) |
| Búsqueda híbrida real en uso: vectores nombrados `dense` + `sparse-code` (TF), un tercer vector `dense-resumen` opt-in, índice de payload y filtro de tenant/manifiesto. | `src/RagEngine.Core/Infrastructure/VectorStore/QdrantVectorStore.cs` |
| Colecciones ya indexadas y en uso (ej. `rag-engine`, `micro-repo`, además de las de ola 21/23) | ola 21, resultados de `21.2-poblar-colecciones-en-x64` |

Lectura honesta: que ya existan puertos (`9.1`/`9.2`) significa que un adaptador Milvus
es **viable sin reescribir retrieval/ingesta**, no que sea gratis. Replicar búsqueda
híbrida densa+sparsa, el vector de resumen opcional y el filtro de tenant en Milvus es
trabajo real de adaptador, más migrar o reconstruir las colecciones ya existentes. No
hay todavía ninguna medición de Milvus contra estos mismos requisitos.

**No se decide aquí.** Se añade `25.2-comparar-qdrant-vs-milvus` a la ola, antes de
crear el repositorio o fijar el contrato de API, porque el vector store elegido
condiciona ambos. Mientras no haya comparación, Qdrant sigue siendo el store vigente
tanto para RagEngine como para cualquier preparación offline del fork.

## 6. Alcance autorizado hoy / límites

- Sin SSH ni aprobación de TI confirmados para el servidor nuevo: nada de
  despliegue, publicación, ni intentos de conexión a un host no documentado. No se
  inventan endpoints, rutas ni credenciales.
- Preparación offline permitida: crear el repositorio local (sin remote), diseñar el
  contrato y portar código, para no llegar en cero cuando el acceso exista.
- Este servidor no comparte configuración, namespace ni credenciales con reyma-dev
  ni con Servidor.IA; son entornos distintos sin relación confirmada entre sí.

## 7. Correspondencia con el ledger (ola 25)

| Ficha | Qué resuelve |
|---|---|
| `25.1-analizar-separacion-motor-embeddings-ingesta` | Este documento. |
| `25.2-comparar-qdrant-vs-milvus` | Decide el vector store del servicio compartido con criterios medidos, no por preferencia del administrador (§5). |
| `25.3-crear-repositorio-independiente-motor-embeddings` | Repo local, sin remote/publicación, una vez confirmadas las preguntas 1, 2 y 6 de §4, y el store de `25.2`. |
| `25.4-definir-contrato-api-y-autenticacion` | Contrato HTTP y control de acceso (preguntas 3 y 4 de §4). |
| `25.5-portar-codigo-motor-a-repositorio` | Mover `OnnxBrain`/pipeline de ingesta al repo nuevo con el store decidido; RagEngine pasa a cliente si así se decidió. |
| `25.6-preparar-acceso-ssh-y-despliegue-offline` | Preparación de despliegue sin tocar el servidor hasta tener SSH y aprobación. |

Ninguna de estas fichas implica hoy código movido, repositorio creado, cambio de
vector store ni conexión a ningún servidor.
