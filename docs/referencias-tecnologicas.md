# Referencias tecnologicas y proyectos de origen

> Registro de las tecnologias que sostienen RagEngine, las herramientas de desarrollo
> y los proyectos externos que aportaron ideas o se evaluan como complementos.

Registro inicial: **2026-10-05**, contrastado con el checkout `0b57f01` y las fuentes
enlazadas. Procedencia del trabajo: item `10.4-registro-de-referencias-tecnologicas`
del [ledger](analisis-futuro/ejecucion-plan.estado.json).

El objetivo es conservar **que usamos, para que, de donde viene y que decision tomamos**,
no solo una lista de enlaces. No es un inventario exhaustivo de dependencias transitivas,
un SBOM ni una certificacion de licencias o seguridad.

## Como interpretar el estado

| Estado | Significado |
|---|---|
| **Integrado** | Hay dependencia, codigo o configuracion versionada que acredita su uso en el motor. No implica que un servicio este encendido en cada entorno. |
| **Herramienta de desarrollo** | Apoya construccion, pruebas, despliegue o proceso; no aporta por si sola capacidad de retrieval. |
| **Referencia de diseno** | Influyo en un analisis o propuesta. No significa que se adoptara su codigo ni que la propuesta este implementada. |
| **Evaluado, no integrado** | Se estudio su posible aporte; no forma parte del producto por ese hecho. |
| **Retirado** | Dejo de usarse; se conserva la referencia y la decision de retirada cuando corresponda. |

Las versiones de paquetes las gobiernan los `.csproj`; las imagenes, Compose; los
modelos efectivos, la configuracion y la procedencia de cada medicion. Este registro
no mantiene una segunda lista de versiones que pueda divergir de esas fuentes.

## Tecnologias integradas en el motor

| Proyecto o tecnologia oficial | Aporte concreto | Evidencia local |
|---|---|---|
| [.NET](https://github.com/dotnet/runtime) / [ASP.NET Core](https://github.com/dotnet/aspnetcore) | Runtime C#, inyeccion de dependencias, opciones y API HTTP. | Proyectos [Core](../src/RagEngine.Core/RagEngine.Core.csproj) y [API](../src/RagEngine.Api/RagEngine.Api.csproj). |
| [Roslyn](https://github.com/dotnet/roslyn) | Analisis sintactico de C# para chunking y extraccion de simbolos. No presupone un grafo semantico persistido. | [RoslynCSharpChunkingStrategy](../src/RagEngine.Core/Infrastructure/Chunking/RoslynCSharpChunkingStrategy.cs) y [SymbolExtractor](../src/RagEngine.Core/Infrastructure/Chunking/SymbolExtractor.cs). |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | Inferencia de embeddings y cross-encoder dentro del proceso .NET. | [OnnxVectorizationBrain](../src/RagEngine.Core/Infrastructure/Vectorization/OnnxVectorizationBrain.cs) y [OnnxCrossEncoderReRanker](../src/RagEngine.Core/Infrastructure/Reranking/OnnxCrossEncoderReRanker.cs). |
| [Microsoft.ML.Tokenizers](https://github.com/dotnet/machinelearning) | Tokenizacion para los modelos, incluido SentencePiece; no confundirla con nuestro tokenizador disperso. | [Dependencia de Core](../src/RagEngine.Core/RagEngine.Core.csproj) y [OnnxVectorizationBrain](../src/RagEngine.Core/Infrastructure/Vectorization/OnnxVectorizationBrain.cs). |
| [Qdrant](https://github.com/qdrant/qdrant) / [cliente .NET](https://github.com/qdrant/qdrant-dotnet) | Almacenamiento vectorial, consultas densas/dispersas y fusion RRF nativa. | [QdrantVectorStore](../src/RagEngine.Core/Infrastructure/VectorStore/QdrantVectorStore.cs), [QdrantSemanticRetriever](../src/RagEngine.Core/Infrastructure/VectorStore/QdrantSemanticRetriever.cs) y [Compose](../infra/docker-compose.yml). |
| [Semantic Kernel](https://github.com/microsoft/semantic-kernel) | Cliente y abstracciones de generacion conversacional mediante el conector compatible con OpenAI. | [GenerationServiceExtensions](../src/RagEngine.Core/Extensions/GenerationServiceExtensions.cs). |
| [Ollama](https://github.com/ollama/ollama) | Servidor de modelos para respuestas y resumenes, con perfiles local/servidor. Usar el conector OpenAI no significa consumir la nube de OpenAI. | [Configuracion de perfiles](configuracion.md) y [generador de resumenes](../src/RagEngine.Core/Services/Summary/OllamaBusinessSummaryGenerator.cs). |
| [SQLite](https://sqlite.org/) / [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | Persistencia de cache de resumenes por contenido y version de prompt. | [SummaryCache](../src/RagEngine.Core/Services/Summary/SummaryCache.cs) y [dependencia de Core](../src/RagEngine.Core/RagEngine.Core.csproj). |
| [Polly](https://github.com/App-vNext/Polly) | Resiliencia en caminos concretos de llamadas; su presencia no acredita cobertura de todos los endpoints o del streaming. | [QdrantSemanticRetriever](../src/RagEngine.Core/Infrastructure/VectorStore/QdrantSemanticRetriever.cs) y [dependencias de Core](../src/RagEngine.Core/RagEngine.Core.csproj). |
| [Spectre.Console](https://github.com/spectreconsole/spectre.console) | Interfaz de terminal y despacho de comandos CLI. | [Proyecto CLI](../src/RagEngine.Cli/RagEngine.Cli.csproj). |
| [Serilog](https://github.com/serilog/serilog) | Logs estructurados y sinks de consola/archivo. No equivale a auditoria persistente de negocio. | Arranque de [API](../src/RagEngine.Api/Program.cs) y [CLI](../src/RagEngine.Cli/Program.cs). |

### Modelos: tambien son dependencias con procedencia

| Modelo o proyecto de origen | Uso documentado | Evidencia |
|---|---|---|
| [paraphrase-multilingual-MiniLM-L12-v2](https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2) | Embeddings densos multilingues. | [Descarga](../infra/download-model.sh) y [configuracion de API](../src/RagEngine.Api/appsettings.json). |
| [mmarco-mMiniLMv2-L12-H384-v1](https://huggingface.co/cross-encoder/mmarco-mMiniLMv2-L12-H384-v1) | Cross-encoder para reordenar candidatos cuando se habilita reranking. | [Descarga](../infra/download-model.sh) y [contrato de scores](arquitectura.md). |
| [Qwen2.5-Coder](https://github.com/QwenLM/Qwen2.5-Coder) | Familia de modelos de generacion y resumen servida mediante Ollama. El tag efectivo depende del perfil. | [Configuracion](configuracion.md) y [opciones de generacion](../src/RagEngine.Core/Extensions/GenerationServiceExtensions.cs). |

Consumir pesos publicados por Sentence Transformers no convierte su biblioteca Python
en una dependencia de ejecucion: aqui se ejecutan con ONNX Runtime. El nombre de un
modelo tampoco identifica el binario efectivo: conservar hash, variante y configuracion
en la [procedencia de las evaluaciones](eval/README.md).

## Herramientas del desarrollo y la operacion

| Proyecto oficial | Aporte | Evidencia local |
|---|---|---|
| [Docker Compose](https://github.com/docker/compose) | Ambiente reproducible de Qdrant y API, con volumenes y configuracion por entorno. | [Compose](../infra/docker-compose.yml) y [operaciones](operaciones.md). |
| [xUnit.net](https://github.com/xunit/xunit) / [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | Ejecucion de pruebas automatizadas y regresiones del motor. | [Proyecto de tests](../tests/RagEngine.Core.Tests/RagEngine.Core.Tests.csproj). |
| [GitHub Actions](https://docs.github.com/actions) | Automatizacion versionada de build y tests. | [Workflow CI](../.github/workflows/ci.yml). |
| [arnes-plan](https://github.com/DanielWueno/arnes-plan) | Proyecto propio separado que gestiona el proceso de ejecucion del plan y valida fichas; no es una dependencia del motor RAG. | [Contrato de trabajo](../AGENTS.md) y [ledger](analisis-futuro/ejecucion-plan.estado.json). |

El [contrato de trabajo](../AGENTS.md) contempla Claude Code, GitHub Copilot CLI y
Gemini CLI como entornos de asistencia. Esa compatibilidad no prueba que todos hayan
ejecutado cada cambio: la atribucion concreta vive en `_ejecucion` y `resultado` de la
ficha correspondiente, con modelo efectivo y limites conocidos, no con equivalencias
supuestas entre proveedores.

## Proyectos que aportaron ideas o se evaluan

| Proyecto oficial | Estado y aporte | Procedencia y decision |
|---|---|---|
| [LightRAG (HKUDS)](https://github.com/HKUDS/LightRAG) | **Referencia de diseno.** Separacion de roles de almacenamiento, estado por documento y descripciones de entidades/relaciones. | [Analisis del 2026-09-04](analisis-futuro/lightrag-rag-anything-y-los-huecos-del-motor.md), reconciliado en el ledger el 2026-09-12. No adoptado como base ni como dependencia. La incorporacion de propuestas no prueba implementacion. |
| [RAG-Anything (HKUDS)](https://github.com/HKUDS/RAG-Anything) | **Referencia de diseno.** Extraccion por formatos/modalidades y conservacion de procedencia. | [Mismo analisis](analisis-futuro/lightrag-rag-anything-y-los-huecos-del-motor.md); relacion con `15.3` (inventario), `15.4` (parsing) y `15.5` (modalidades). No adoptado como base; conservar condiciones de entrada de esas fichas. |
| [ContextForge (IBM)](https://github.com/IBM/mcp-context-forge) | **Evaluado, no integrado.** Gateway/registro para MCP, REST/gRPC y agentes, con servidores virtuales, plugins y observabilidad. | Evaluacion documental del 2026-10-05 sobre [v1.0.11](https://github.com/IBM/mcp-context-forge/releases/tag/v1.0.11), licencia Apache-2.0. Candidato a complementar RagEngine, no a reemplazarlo; sin instalacion ni prueba de integracion en este registro. |

### ContextForge: oportunidad que se conserva

La decision de esta evaluacion es **mantenerlo como candidato dentro de la evolucion
de RagEngine**, no adoptar toda la plataforma ni abrir por ello un producto independiente.
Un servicio desplegado por separado puede seguir siendo un componente opcional del mismo
sistema. El motor conserva chunking, embeddings, retrieval, reranking, contexto y evaluacion.

Posibles aportes, aun no implementados:

- **Publicar RagEngine como herramientas MCP:** adaptar `/api/search` y, si el caso lo
  necesita, `/api/ask`, mediante su [adaptacion REST](https://github.com/IBM/mcp-context-forge/blob/v1.0.11/docs/docs/architecture/tool-invocation-and-validation.md).
  El SSE de `/api/ask/stream` no es por si mismo un servidor MCP.
- **Complementar el indice con fuentes actuales:** consultar tickets o APIs cuando la
  pregunta requiera datos vivos. Registrar una herramienta no crea su conector ni decide
  cuando llamarla; seleccion, combinacion de evidencia y presupuestos serian trabajo nuestro.
- **Compartir controles y observabilidad entre herramientas:** evaluar sus
  [plugins](https://github.com/IBM/mcp-context-forge/blob/v1.0.11/docs/docs/architecture/plugins.md)
  y [trazas](https://github.com/IBM/mcp-context-forge/blob/v1.0.11/docs/docs/architecture/observability-otel.md).
  No sustituyen las ACL del motor, la instrumentacion interna ni la consola de ingestas/eval.
- **Reutilizar piezas auxiliares sin adoptar el gateway completo:** su
  [servidor DOCX](https://github.com/IBM/mcp-context-forge/blob/v1.0.11/docs/docs/using/servers/python/docx-server.md)
  es un candidato de extraccion, no una capacidad instalada ni una prueba de calidad.

Limites observados: el [plugin de cache de herramientas](https://github.com/IBM/mcp-context-forge/blob/v1.0.11/plugins/cached_tool_result/README.md)
no evita por si solo ejecutar la herramienta; no contabilizar ahorro automatico. Un gateway
local tampoco garantiza privacidad si el cliente envia sus resultados a un modelo externo.
No hay mejora medida de recall, latencia o respuestas por el solo hecho de esta evaluacion.

Para retomar: preparar en el ledger una ficha de prueba local y reversible, con una necesidad
concreta, fuentes autorizadas, criterio predefinido y presupuesto. Comparar una consulta
solo con indice frente a la misma consulta enriquecida, distinguiendo citas, fechas,
permisos y fallos de fuente. Empezar por herramientas de solo lectura. Este registro no
autoriza esa prueba, no cambia prioridades y no desbloquea fichas existentes.

## Mantenimiento y atribucion

Al incorporar una referencia, registrar fuente oficial, aporte, estado, evidencia local
o analisis fechado, y decision con sus limites. Si se estudia codigo o documentacion
externa cambiante, anotar tag/commit cuando se conozca; no inventarlo para estudios antiguos.

Si un candidato se adopta, enlazar el item y commit que lo demuestran y conservar la
evaluacion anterior como historia. Si se retira o descarta, documentar el motivo sin borrar
su procedencia. Reutilizar codigo exige revisar la licencia de la revision concreta y
preservar sus avisos; citar una idea no permite atribuirse el proyecto de origen.

Las decisiones ejecutables y su avance siguen en el
[ledger](analisis-futuro/ejecucion-plan.estado.json). Este documento explica las referencias;
no constituye un segundo backlog ni sustituye los analisis y evidencias enlazados.
