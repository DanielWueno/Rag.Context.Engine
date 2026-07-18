Fase 4: Roadmap de Ejecución por Hitos

### Del Repositorio Vacío al POC CLI Funcional — Sprints de Desarrollo Lógico

──────

## Principios del Roadmap

• Vertical Slices: Cada sprint entrega funcionalidad end-to-end usable, no capas horizontales aisladas.
• Walking Skeleton First: El Sprint 0 establece la estructura mínima que compila y corre.
• Fail Fast: Las integraciones con dependencias externas (Qdrant, ONNX) se validan en Sprint 1, no al final.
• CLI como contrato de UX: La experiencia de línea de comandos define la API interna; la Minimal API (Fase 2) lo hereda.
──────

## Estructura de la Solución .NET

    RagEngine.sln
    ├── src/
    │   ├── RagEngine.Core/              ← Class Library (núcleo compartido)
    │   │   ├── Abstractions/
    │   │   ├── Domain/
    │   │   ├── Pipeline/
    │   │   ├── Infrastructure/
    │   │   │   └── Chunking/
    │   │   └── Extensions/
    │   │
    │   ├── RagEngine.Cli/               ← Fase 1: Console App (.NET 10)
    │   │   ├── Commands/                ← System.CommandLine commands
    │   │   ├── Rendering/               ← Spectre.Console UI
    │   │   └── Program.cs
    │   │
    │   └── RagEngine.Api/               ← Fase 2 (placeholder): ASP.NET Core Minimal API
    │       └── Program.cs
    │
    └── tests/
        ├── RagEngine.Core.UnitTests/
        ├── RagEngine.Core.IntegrationTests/
        └── RagEngine.Cli.E2ETests/
    ──────

## 🏁 Sprint 0 — Walking Skeleton (3–4 días)

Meta: Un binario que compila, resuelve dependencias y valida conectividad con Qdrant y el modelo ONNX.

### Tareas

[S0-T1] Scaffolding de la solución

    # Crear solución y proyectos
    dotnet new sln -n RagEngine
    dotnet new classlib -n RagEngine.Core -f net10.0 -o src/RagEngine.Core
    dotnet new console -n RagEngine.Cli  -f net10.0 -o src/RagEngine.Cli
    dotnet sln add src/RagEngine.Core src/RagEngine.Cli
    dotnet add src/RagEngine.Cli reference src/RagEngine.Core

[S0-T2] Instalación de dependencias NuGet en RagEngine.Core

    <!-- RagEngine.Core.csproj -->
    <ItemGroup>
      <!-- Orquestación AI -->
      <PackageReference Include="Microsoft.SemanticKernel"            Version="1.*" />

      <!-- ONNX Runtime para embeddings locales -->
      <PackageReference Include="Microsoft.ML.OnnxRuntime"            Version="1.19.*" />
      <PackageReference Include="Microsoft.ML.OnnxRuntime.Extensions" Version="0.12.*" />

      <!-- Parsing de código fuente -->
      <PackageReference Include="Microsoft.CodeAnalysis.CSharp"       Version="4.*" />

      <!-- Cliente Qdrant -->
      <PackageReference Include="Qdrant.Client"                       Version="1.*" />

      <!-- Tokenización HuggingFace -->
      <PackageReference Include="FastBertTokenizer"                   Version="1.*" />

      <!-- DI y configuración -->
      <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.*" />
      <PackageReference Include="Microsoft.Extensions.Configuration.Json"  Version="9.*" />
      <PackageReference Include="Microsoft.Extensions.Logging.Console"     Version="9.*" />
    </ItemGroup>

    <!-- RagEngine.Cli.csproj -->
    <ItemGroup>
      <!-- CLI framework moderno -->
      <PackageReference Include="System.CommandLine"                  Version="2.0.0-beta4.*" />

      <!-- UI rica en consola -->
      <PackageReference Include="Spectre.Console"                    Version="0.49.*" />
      <PackageReference Include="Spectre.Console.Cli"               Version="0.49.*" />
    </ItemGroup>

[S0-T3] Configuración base appsettings.json

    // src/RagEngine.Cli/appsettings.json
    {
      "Qdrant": {
        "Host": "localhost",
        "Port": 6334,
        "ApiKey": ""
      },
      "OnnxBrain": {
        "ModelPath": "models/all-MiniLM-L6-v2/model.onnx",
        "TokenizerPath": "models/all-MiniLM-L6-v2/tokenizer.json",
        "MaxSequenceLength": 512,
        "EmbeddingDimensions": 384,
        "BatchSize": 32
      },
      "Logging": {
        "LogLevel": { "Default": "Warning", "RagEngine": "Information" }
      }
    }

[S0-T4] Script de descarga del modelo ONNX

    # scripts/download-model.ps1
    # Descarga all-MiniLM-L6-v2 exportado a ONNX desde HuggingFace
    pip install optimum[onnxruntime] transformers
    optimum-cli export onnx `
      --model sentence-transformers/all-MiniLM-L6-v2 `
      --task feature-extraction `
      models/all-MiniLM-L6-v2

[S0-T5] Docker Compose para Qdrant

    # docker-compose.yml
    services:
      qdrant:
        image: qdrant/qdrant:latest
        ports:
          - "6333:6333"   # REST API
          - "6334:6334"   # gRPC
        volumes:
          - ./qdrant_storage:/qdrant/storage
        environment:
          QDRANT__SERVICE__GRPC_PORT: 6334

Criterio de aceptación del Sprint 0:

    ✅ dotnet build → 0 errores, 0 warnings
    ✅ docker compose up → Qdrant responde en localhost:6333/dashboard
    ✅ Smoke test: OnnxVectorizationBrain carga el modelo y vectoriza "hello world"
    ✅ Smoke test: QdrantClient conecta y lista colecciones (vacías)
    ──────

## 🏁 Sprint 1 — Ingestion Pipeline E2E (5–7 días)

Meta: El comando rag ingest funciona end-to-end sobre un repositorio real pequeño.

### Diseño del Comando CLI

    // RagEngine.Cli/Commands/IngestCommand.cs

    using System.CommandLine;
    using Spectre.Console;

    public sealed class IngestCommand : Command
    {
        public IngestCommand() : base("ingest", "Indexa un repositorio en la base vectorial")
        {
            var pathArg    = new Argument<DirectoryInfo>("path", "Ruta al repositorio");
            var collection = new Option<string>("--collection", () => "default", "Nombre de colección en Qdrant");
            var forceOpt   = new Option<bool>("--force", "Eliminar y recrear el índice");
            var batchOpt   = new Option<int>("--batch-size", () => 32, "Chunks por lote ONNX");

            AddArgument(pathArg);
            AddOption(collection);
            AddOption(forceOpt);
            AddOption(batchOpt);

            this.SetHandler(HandleAsync, pathArg, collection, forceOpt, batchOpt);
        }

        private async Task HandleAsync(
            DirectoryInfo path, string collection, bool force, int batchSize)
        {
            await AnsiConsole.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn(),
                    new ElapsedTimeColumn())
                .StartAsync(async ctx =>
                {
                    var scanTask      = ctx.AddTask("[cyan]Escaneando archivos...[/]");
                    var chunkTask     = ctx.AddTask("[yellow]Chunking...[/]");
                    var vectorizeTask = ctx.AddTask("[green]Vectorizando...[/]");
                    var indexTask     = ctx.AddTask("[blue]Indexando en Qdrant...[/]");

                    var progress = new Progress<IngestionProgress>(p =>
                    {
                        switch (p.Stage)
                        {
                            case IngestionStage.Scanning:
                                scanTask.Value = (p.FilesProcessed * 100.0) / Math.Max(p.TotalFilesDiscovered, 1);
                                break;
                            case IngestionStage.Chunking:
                                chunkTask.Increment(1);
                                break;
                            case IngestionStage.Vectorizing:
                                vectorizeTask.Increment(1);
                                break;
                            case IngestionStage.Indexing:
                                indexTask.Increment(1);
                                break;
                        }
                    });

                    var pipeline = ServiceLocator.GetRequired<IIngestionPipeline>();
                    var summary  = await pipeline.IngestRepositoryAsync(
                        new IngestionRequest(path.FullName, collection,
                            ScanProfile.DotNetEnterprise, force),
                        progress);

                    RenderSummaryTable(summary);
                });
        }

        private static void RenderSummaryTable(IngestionSummary summary)
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Métrica")
                .AddColumn("[bold]Resultado[/]");

            table.AddRow("Archivos escaneados",  $"[cyan]{summary.FilesScanned:N0}[/]");
            table.AddRow("Chunks generados",     $"[yellow]{summary.ChunksGenerated:N0}[/]");
            table.AddRow("Chunks indexados",     $"[green]{summary.ChunksIndexed:N0}[/]");
            table.AddRow("Archivos omitidos",    $"[red]{summary.FilesSkipped:N0}[/]");
            table.AddRow("Duración total",       $"[white]{summary.TotalDuration:mm\\:ss\\.fff}[/]");
            table.AddRow("Throughput",           $"[white]{summary.ChunksIndexed / summary.TotalDuration.TotalSeconds:F0} chunks/seg[/]");

            AnsiConsole.Write(table);
        }
    }

### Tareas del Sprint 1

ID │ Tarea │ Estimación
──────────────────────────────────────────────────────────┼──────────────────────────────────────────────────────────────────────────────────────┼──────────────────────────────────────────────────────────
S1-T1 │ Implementar FileSystemIngestionScanner con Microsoft.Extensions.FileSystemGlobbing │ 1 día
S1-T2 │ Implementar RoslynCSharpChunkingStrategy (métodos + clases) │ 2 días
S1-T3 │ Implementar OnnxVectorizationBrain con Mean Pooling y L2 norm │ 1 día
S1-T4 │ Implementar DefaultIngestionPipeline con Channel │ 1 día
S1-T5 │ Integrar IngestCommand en el CLI con Spectre.Console progress │ 0.5 días
S1-T6 │ Tests de integración: ingestar el propio repositorio RagEngine.Core │ 0.5 días

Criterio de aceptación del Sprint 1:

    ✅ rag ingest ./RagEngine.Core --collection rag-test
       → Muestra barra de progreso animada
       → Termina con tabla de resumen
       → Qdrant Dashboard muestra la colección con N puntos

    ✅ rag ingest ./RagEngine.Core --force
       → Elimina y recrea la colección antes de indexar

    ✅ Test de regresión: ingestar 500 archivos .cs sin errores ni leaks de memoria
    ──────

## 🏁 Sprint 2 — Búsqueda Semántica Interactiva (4–5 días)

Meta: El comando rag search permite consultas en lenguaje natural y muestra código relevante resaltado en sintaxis.

### Diseño del Comando CLI

    // RagEngine.Cli/Commands/SearchCommand.cs

    public sealed class SearchCommand : Command
    {
        public SearchCommand() : base("search", "Busca código semánticamente relevante")
        {
            var queryArg   = new Argument<string>("query", "Pregunta en lenguaje natural");
            var collection = new Option<string>("--collection", () => "default");
            var topK       = new Option<int>("--top-k", () => 10, "Número de resultados");
            var minScore   = new Option<float>("--min-score", () => 0.70f, "Similitud mínima [0-1]");
            var lang       = new Option<SourceLanguage?>("--language", "Filtrar por lenguaje");
            var ns         = new Option<string?>("--namespace", "Filtrar por namespace");
            var rerank     = new Option<bool>("--rerank", "Activar re-ranking con Cross-Encoder");
            var outputFmt  = new Option<OutputFormat>("--output", () => OutputFormat.Rich, "Formato de salida");

            // ... AddArgument + AddOption ...

            this.SetHandler(HandleAsync, queryArg, collection, topK, minScore, lang, ns, rerank, outputFmt);
        }

        private async Task HandleAsync(
            string query, string collection, int topK, float minScore,
            SourceLanguage? lang, string? ns, bool rerank, OutputFormat output)
        {
            AnsiConsole.MarkupLine($"[dim]Buscando:[/] [bold cyan]{query}[/]");
            AnsiConsole.WriteLine();

            var retriever = ServiceLocator.GetRequired<ISemanticRetriever>();

            IReadOnlyList<RetrievalResult> results = null!;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots2)
                .StartAsync("Vectorizando query y buscando...", async ctx =>
                {
                    results = await retriever.SearchAsync(query, new RetrievalOptions
                    {
                        CollectionName = collection,
                        TopK = topK,
                        MinimumSimilarityScore = minScore,
                        FilterByLanguage = lang,
                        FilterByNamespace = ns,
                        UseReRanking = rerank
                    });
                });

            if (!results.Any())
            {
                AnsiConsole.MarkupLine("[yellow]⚠ No se encontraron resultados.[/]");
                AnsiConsole.MarkupLine($"[dim]Intenta reducir --min-score (actual: {minScore})[/]");
                return;
            }

            switch (output)
            {
                case OutputFormat.Rich:
                    RenderRichResults(results);
                    break;
                case OutputFormat.Markdown:
                    Console.Write(new ContextAssembler().Assemble(results, query));
                    break;
                case OutputFormat.Json:
                    Console.Write(JsonSerializer.Serialize(results, JsonOptions.Indented));
                    break;
            }
        }

        private static void RenderRichResults(IReadOnlyList<RetrievalResult> results)
        {
            foreach (var (result, i) in results.Select((r, i) => (r, i + 1)))
            {
                var panel = new Panel(
                    new Rows(
                        // Header con metadata
                        new Markup($"[bold]{result.Metadata.ClassName}[/]" +
                                   $"[dim]::[/][cyan]{result.Metadata.MethodName}[/]"),
                        new Markup($"[dim]{result.Metadata.FilePath}[/] " +
                                   $"[dim]L{result.Metadata.StartLine}–{result.Metadata.EndLine}[/]"),
                        new Rule(),
                        // Código resaltado con Spectre
                        new Markup($"[green]{Markup.Escape(result.Content.Truncate(800))}[/]")
                    ))
                {
                    Header = new PanelHeader(
                        $"[bold]#{i}[/] [yellow]Score: {result.SimilarityScore:P1}[/]"),
                    Border = BoxBorder.Rounded,
                    BorderStyle = new Style(Color.Grey)
                };

                AnsiConsole.Write(panel);
                AnsiConsole.WriteLine();
            }

            AnsiConsole.MarkupLine(
                $"[dim]Mostrando {results.Count} resultado(s). " +
                $"Usa [bold]--output markdown[/] para formato de prompt.[/]");
        }
    }

    public enum OutputFormat { Rich, Markdown, Json }

Criterio de aceptación del Sprint 2:

    ✅ rag search "validación de pedidos" --collection rag-test
       → Muestra panel con código resaltado y score de similitud

    ✅ rag search "inyección de dependencias" --language CSharp --top-k 5
       → Filtra correctamente por lenguaje

    ✅ rag search "..." --output markdown
       → Output válido para pegar en system prompt de Claude/GPT-4

    ✅ rag search "xyz_término_inexistente"
       → Mensaje amigable "no se encontraron resultados"
    ──────

## 🏁 Sprint 3 — Completitud del Chunking (3–4 días)

Meta: Soporte completo de TypeScript, XAML y SQL. Re-indexación incremental.

ID │ Tarea │ Estimación
────────────────────────────────────────────────────────────────┼──────────────────────────────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────
S3-T1 │ Implementar TypeScriptChunkingStrategy │ 1 día
S3-T2 │ Implementar XamlChunkingStrategy │ 1 día
S3-T3 │ Implementar SqlChunkingStrategy (por GO y CREATE PROCEDURE ) │ 0.5 días
S3-T4 │ Re-indexación incremental: comparar LastModified vs. payload en Qdrant │ 1 día
S3-T5 │ Comando rag status --collection X → estadísticas de la colección │ 0.5 días

Comando rag status :

    ┌─────────────────────────────────────────────────────────┐
    │ Colección: rag-enterprise-repo                          │
    ├──────────────────────┬──────────────────────────────────┤
    │ Puntos totales       │ 48,392                           │
    │ Dimensiones vector   │ 384                              │
    │ Tamaño en disco      │ 2.1 GB                           │
    │ Distribución por tipo│ Method: 71% | Class: 12% | ...   │
    │ Último índice        │ 2026-07-15 08:30 (hace 5h)       │
    └──────────────────────┴──────────────────────────────────┘
    ──────

## 🏁 Sprint 4 — Integración con Semantic Kernel (4–5 días)

Meta: Plugin oficial de Semantic Kernel que expone el retriever como herramienta para agentes AI.

    // RagEngine.Core/SemanticKernel/RagContextPlugin.cs

    using Microsoft.SemanticKernel;

    public sealed class RagContextPlugin
    {
        private readonly ISemanticRetriever _retriever;
        private readonly ContextAssembler _assembler;

        [KernelFunction("search_codebase")]
        [Description("Busca código fuente semánticamente relevante en el repositorio indexado.")]
        public async Task<string> SearchCodebaseAsync(
            [Description("Pregunta o descripción de lo que buscas en el código")]
            string query,

            [Description("Número máximo de fragmentos a recuperar (1-20)")]
            int topK = 8,

            [Description("Filtrar por lenguaje: CSharp, TypeScript, Xaml, Sql")]
            string? language = null,

            Kernel kernel = null!,
            CancellationToken cancellationToken = default)
        {
            var langFilter = language is not null
                ? Enum.TryParse<SourceLanguage>(language, true, out var l) ? l : (SourceLanguage?)null
                : null;

            var results = await _retriever.SearchAsync(query, new RetrievalOptions
            {
                CollectionName = "default",
                TopK = topK,
                MinimumSimilarityScore = 0.68f,
                FilterByLanguage = langFilter,
                UseReRanking = topK > 5
            }, cancellationToken);

            return _assembler.Assemble(results, query, maxContextTokens: 12_000);
        }
    }

    // Registro en el Kernel de Semantic Kernel
    var kernel = Kernel.CreateBuilder()
        .AddOpenAIChatCompletion("gpt-4o", apiKey)  // O cualquier LLM local
        .Build();

    // El plugin usa las mismas abstracciones del Core
    var ragPlugin = new RagContextPlugin(retriever, assembler);
    kernel.Plugins.AddFromObject(ragPlugin, "RagContext");

    // Ejemplo de uso en agent loop
    var result = await kernel.InvokePromptAsync(
        "¿Cómo implementa OrderService la validación de inventario?",
        new KernelArguments(new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
        }));
    ──────

## 🏁 Sprint 5 — Advanced Retrieval & Hybrid Search (3-4 días)

Meta: Implementar búsqueda híbrida local pura (Dense + Sparse) usando Reciprocal Rank Fusion (RRF).

ID │ Tarea │ Detalles
────────────────────────────────────────────────────────────────────┼───────────────────────────────────────────────────────────────────┼───────────────────────────────────────────────────────────────────
S5-T1 │ Tokenizador Local (SparseTokenizer) │ Eliminación de stop-words y cálculo de TF en C#
S5-T2 │ Evolución del Esquema en Qdrant │ Configuración dual: Vectores Densos + Vectores Dispersos (Sparse)
S5-T3 │ Indexación Dual (DefaultIngestionPipeline) │ Procesamiento paralelo para enviar ambos dominios a Qdrant
S5-T4 │ Búsqueda Híbrida (HybridRetriever) │ Uso de Qdrant Prefetch y Fusion.Rrf (QueryAsync)
    ──────

> 📌 **Retrospectiva post-implementación (julio 2026):** el sprint cumplió su meta, pero dejó
> tres deudas latentes que se detectaron y corrigieron en el Sprint 7:
>
> 1. **`min-score` quedó como no-op:** la migración de `SearchAsync` a `QueryAsync`+RRF eliminó
>    el `scoreThreshold` que se pasaba a Qdrant, y ningún componente volvió a leer
>    `MinimumSimilarityScore`. Además, el score post-fusión es RRF (función del ranking, tope
>    ~0.5), no coseno — el default 0.65 era incomparable con la nueva escala.
> 2. **TF proporcional sesgado a micro-chunks:** el peso `count/totalTerms` del `SparseTokenizer`
>    hacía que un constructor de una línea dominara el ranking disperso frente al chunk rico
>    que contenía la respuesta.
> 3. **Regresión de rendimiento en ingesta (2s → 6s en el repo de ejemplo):** el upsert dual
>    (HNSW + índice invertido) entró íntegro a la ruta crítica del consumidor único; la
>    tokenización dispersa resultó irrelevante en costo (~1 ms/lote, en paralelo con ONNX).

## 🏁 Sprint 6 — Hardening y Observabilidad (3–4 días)

Meta: El sistema es robusto para uso diario. Logs estructurados, métricas, circuit breakers.

ID │ Tarea │ Detalles
────────────────────────────────────────────────────────────────────┼───────────────────────────────────────────────────────────────────┼───────────────────────────────────────────────────────────────────
S6-T1 │ Structured logging con Serilog │ JSON sink + Seq local para explorar logs
S6-T2 │ Métricas con System.Diagnostics.Metrics │ Counters: chunks/seg, errores, latencia P99
S6-T3 │ Polly para resiliencia en Qdrant │ Retry exponencial + Circuit Breaker
S6-T4 │ Comando rag doctor │ Valida conectividad Qdrant, modelo ONNX, espacio en disco
S6-T5 │ Unit tests críticos │ Chunking Roslyn, TokenEstimator, ContextAssembler

Comando rag doctor :

     Checking RagEngine dependencies...

     ✅ Qdrant         localhost:6334   [Connected | v1.9.2]
     ✅ ONNX Model     models/paraphrase-multilingual-MiniLM-L12-v2/model_qint8_arm64.onnx   [Loaded | 384 dims]
     ✅ Tokenizer      models/paraphrase-multilingual-MiniLM-L12-v2/sentencepiece.bpe.model   [OK]
     ✅ Disk Space     qdrant_storage/   [2.1 GB used | 180 GB free]
     ⚠️  Colecciones   0 colecciones encontradas → Ejecuta 'rag ingest'
    ──────

## 🏁 Sprint 7 — Multilingüe y Optimización de Retrieval (completado)

Meta: consultas en español al mismo nivel que en inglés, y recuperar el rendimiento de ingesta
perdido en el Sprint 5. Sprint nacido del análisis de dos comportamientos observados en
producción: la ingesta 3× más lenta tras la búsqueda híbrida, y consultas en español que
devolvían "0 resultados" mientras su traducción al inglés funcionaba.

ID │ Tarea │ Detalles
────────────────────────────────────────────────────────────────────┼───────────────────────────────────────────────────────────────────┼───────────────────────────────────────────────────────────────────
S7-T1 │ Cerebro denso multilingüe │ paraphrase-multilingual-MiniLM-L12-v2 int8 ARM64 (mismos 384 dims); estrategia dual de tokenizer (WordPiece/SentencePiece XLM-R con remapeo fairseq); padding dinámico por lote
S7-T2 │ Normalización léxica ES/EN (SparseTokenizer) │ Folding de acentos + stemming ligero simétrico (auditoría/auditorias → auditori); TF saturado tf/(tf+1) contra el sesgo a micro-chunks
S7-T3 │ Restauración de min-score │ Aplicado como ScoreThreshold del prefetch denso (coseno); pool RRF 4× el corte final; defaults recalibrados 0.65 → 0.10 (escala medida: relevante ≈ 0.12–0.25)
S7-T4 │ Paralelización real de la ingesta │ 2–4 consumidores sobre el Channel + upserts wait:false (durabilidad vía WAL); filtro de contenido mínimo indexable (60 chars)
S7-T5 │ Fix de chunking Roslyn │ Cabecera de clase reconstruida desde el AST (atributos + declaración + campos); antes se reducía a la primera línea no vacía (el atributo)
S7-T6 │ Fix de ensamblado de contexto │ Chunk sobredimensionado ya no trunca la cola del contexto (continue, no break); prompt responde en el idioma de la pregunta
    ──────

Resultados medidos: ingesta BusinessSuite.Xaf 4m21s → **2m26s** (con modelo del doble de
profundidad); coseno ES↔EN en pares equivalentes **0.92/0.89**; la consulta canónica en español
sobre reglas de auditoría pasó de la negativa del LLM a una respuesta fundamentada y citada.
Detalle técnico: `docs/busqueda-hibrida.md` y `docs/pipeline-de-ingesta.md`.

## Tabla Resumen del Roadmap

    SEMANA     S0          S1          S2          S3          S4          S5          S6          S7
               ────────    ────────    ────────    ────────    ────────    ────────    ────────    ────────
    Días        1–4         5–11        12–16       17–20       21–25       26–29       30-33       34–37

    Output    Scaffolding  Ingest E2E  Search CLI  Multi-lang  SK Plugin   Hybrid      Hardening   Multilingüe
               + Docker    + Roslyn    + Spectre   + Incremental+ Agent    Search      + Tests     + Retrieval QA

    Comando     —          rag ingest  rag search  rag status  rag ask     (interno)   rag doctor  (interno)
                           rag ingest  rag search
                           --force     --output md
    ──────

## Entregables Acumulados al Finalizar el POC

Al cerrar el Sprint 7, el equipo tendrá:

Entregable │ Descripción
──────────────────────────────────────────────────────────────────────────────────────────────────────┼─────────────────────────────────────────────────────────────────────────────────────────────────────
rag ingest <path> │ Indexa cualquier repositorio .NET/TS con barra de progreso
rag search <query> │ Búsqueda híbrida (densa + dispersa, RRF) con filtros y múltiples formatos de salida
rag ask <query> │ Pregunta en lenguaje natural (ES/EN) con respuesta citada del LLM local
rag status │ Dashboard de estado de colecciones
rag doctor │ Auto-diagnóstico de dependencias
RagContextPlugin │ Plugin listo para Semantic Kernel agents
Búsqueda multilingüe ES/EN │ Modelo denso multilingüe + normalización léxica simétrica en la rama dispersa
Suite de tests │ Unit + Integration + E2E para el núcleo
docker-compose.yml │ Qdrant listo con un comando
appsettings.json documentado │ Configuración lista para adaptar a otros equipos
Documentación formal (docs/) │ Arquitectura, búsqueda híbrida, pipeline, CLI, configuración y runbook de operaciones
──────
