# Prompt: plan global de ingeniería del proyecto

**Para qué sirve:** pegar este texto en una sesión nueva de Claude Code con Opus y la palabra
`ultracode`, para que audite el repositorio completo y produzca un plan global de madurez de
ingeniería (qué falta, qué está mal hecho, qué orden seguir).

**Cómo usarlo:**

1. Abre una sesión limpia (sin contexto previo) en la raíz del repo.
2. Pega el bloque completo de abajo. La palabra `ultracode` ya viene incluida.
3. Léelo antes de pegarlo y ajusta la sección `RESTRICCIONES DEL DUEÑO` si tus prioridades
   cambiaron: es la parte que evita que el plan te proponga ceremonia que no quieres.

**Por qué está anclado a hechos concretos:** la sección `LO QUE YA SE VERIFICÓ` viene de una
auditoría multi-agente real corrida el 2026-08-20 sobre este repo. Sirve para que la sesión nueva
no gaste tokens redescubriendo lo obvio y, sobre todo, para que no te venda hallazgos de checklist
genérico. Está marcado explícitamente qué quedó verificado y qué quedó sin verificar.

**Nota de coste (medida, no estimada):** esa auditoría agotó el límite de sesión con 14 agentes.
Coste real 32,2M tokens (29,7M de subagentes + 2,4M del hilo principal), de los que el 92% fue
cache-read: ~2,1M tokens por agente de auditoría profunda. De los 14 agentes solo 2 devolvieron
resultado; los otros 12 murieron por el límite después de haber consumido su contexto. El número que
reporta el propio workflow (~873k) cuenta sólo output de subagentes y subestima el total 34 veces.

Correr este prompt tal cual proyecta ~50-75M tokens, o sea dos o tres ventanas de sesión. Por eso
incluye la instrucción de escalonar en fases. Para bajarlo a una sola ventana: fan-out de 5-6
auditores con lista explícita de archivos en vez de 12-16 exploratorios, reconocimiento mecánico en
Haiku y Opus sólo para síntesis, y pase adversarial únicamente sobre los hallazgos que entren al
plan. Antes de correr nada, saca los logs del árbol de código: `src/RagEngine.Cli/logs/` tiene un
archivo de 46 MB (~12M tokens) que un `grep -r src/` se traga entero.

---

````text
ultracode

Eres el ingeniero de software senior a cargo de hacer madurar este proyecto. Tu trabajo en este
turno NO es escribir código de producto: es auditar el repositorio con evidencia y entregarme un
plan global de ingeniería que yo pueda ejecutar en las próximas semanas.

## CONTEXTO DEL PROYECTO

Rag.Context.Engine es un motor RAG en .NET 10 (`RagEngine.slnx`) con tres proyectos —
`src/RagEngine.Core` (dominio + infraestructura), `src/RagEngine.Cli` (comandos `ingest`, `search`,
`ask`, `eval`, `doctor`, `status`) y `src/RagEngine.Api` (5 endpoints HTTP + un frontend en
`wwwroot`) — más un PoC aparte en `poc/RagEngine.Poc.FreeSearch`.

Stack: Qdrant como vector store, ONNX Runtime para embeddings densos y para el cross-encoder de
rerank, Ollama para generación y para los resúmenes de negocio, SQLite como caché de resúmenes,
Semantic Kernel, Serilog, Polly.

Lo que lo hace atípico: no es un CRUD. Es investigación aplicada con aparato experimental propio.
Hay retrieval híbrido con fusión RRF de tres bandas (densa, sparse, resumen) con pesos calibrados
empíricamente, un eval-runner (`rag eval`) que mide recall@k contra ground-truth versionado en
`docs/eval/`, y una carpeta `docs/analisis-futuro/` con ocho documentos de análisis donde se
registran experimentos, calibraciones y hasta enfoques abandonados por no dar señal. Lo desarrolla
un solo autor. No está en producción, no tiene SLA, no tiene usuarios externos.

## LO QUE YA SE VERIFICÓ (auditoría del 2026-08-20)

Trátalo como punto de partida, no como verdad revelada: confirma cada dato que vayas a usar como
base de una recomendación. Si algo de esto ya cambió o está mal, dímelo — es una señal valiosa.

Verificado con evidencia directa:

- No existe ni un proyecto de test en toda la solución. `RagEngine.slnx` lista 4 proyectos y
  ninguno es de pruebas; no hay referencia a xunit/NUnit/MSTest en ningún `.csproj`.
- No existe `.github/` — no hay CI de ningún tipo. Tampoco `.editorconfig`,
  `Directory.Packages.props`, `LICENSE`, `CONTRIBUTING`, `CHANGELOG` ni `CLAUDE.md`.
  `Directory.Build.props` sólo desactiva el workload resolver.
- Ningún `.csproj` activa analizadores, `TreatWarningsAsErrors` ni `AnalysisLevel`. Sí está
  `Nullable` en los tres.
- `src/RagEngine.Cli` referencia DOS frameworks de línea de comandos a la vez: `Spectre.Console.Cli`
  0.49.1 y `System.CommandLine` 2.0.0-beta4.22272.1.
- `src/RagEngine.Api/appsettings.json` está versionado y contiene rutas absolutas de la máquina del
  autor (`/Users/DevStudio/models/...`) apuntando a binarios `model_qint8_arm64.onnx`. Ese archivo
  no funciona en otra máquina ni en x64.
- La API expone `/api/health` como `Results.Ok(new { status = "ok" })`: no comprueba Qdrant, Ollama
  ni las sesiones ONNX. En `Program.cs` no hay autenticación, CORS explícito, rate limiting,
  `ProblemDetails` ni `UseExceptionHandler`.
- Polly SÍ está bien usado: pipelines nombrados con retry y circuit breaker para Qdrant y para
  Ollama, consumidos vía `ResiliencePipelineProvider` en `QdrantSemanticRetriever` y
  `OllamaBusinessSummaryGenerator`.
- `Diagnostics/RagEngineMetrics.cs` define un `Meter` con 4 instrumentos, pero ningún `.csproj`
  referencia OpenTelemetry: las métricas existen y nadie las exporta. Los logs de Serilog sí salen
  estructurados con TraceId/SpanId.
- `RagGenerationService.cs` son 1042 líneas y contiene 12 bloques de string literal: los prompts del
  sistema están hardcodeados en C#, no se pueden versionar ni comparar A/B sin recompilar. Ya existe
  un plan propio para esto en `docs/analisis-futuro/centralizacion-prompts-vault.md`.
- Las 4 estrategias de chunking duplican algoritmos completos (encabezado semántico ×4, ventana
  deslizante con la misma fórmula ×2, agrupación por presupuesto de tokens ×2, construcción de
  `CodeChunk` ×8, dos mecanismos paralelos de estimación de tokens). Consecuencia medible: el split
  de líneas es `Split(new[]{"\r\n","\r","\n"})` en las estrategias de TS y Markdown pero
  `Split('\n')` en Roslyn y Fallback, así que en archivos CRLF esas dos dejan `\r` dentro del
  `Content` y del `ContentHash` — el mismo archivo produce hash distinto según la estrategia.
- La tabla "Live" de progreso de `rag ingest` nunca se actualiza: se pasa `GetLayout()` una sola vez
  a `AnsiConsole.Live` y `UpdateTarget` no se llama en ninguna parte del repo, mientras el productor
  reporta progreso por cada chunk sin throttling (~23.000 refrescos que muestran siempre 0).
- `rag ingest` termina con exit code 0 aunque se hayan perdido lotes. La pérdida sí es observable
  (log ERROR, contador `IngestionErrorsTotal` por etapa, y el delta entre las filas "Chunks
  Generated" y "Chunks Indexed" de la tabla final), pero no es detectable programáticamente.
- De los 3 eval-sets en `docs/eval/` hay UN solo baseline guardado
  (`innovapp-docs.rerank.baseline.json`). Registra collection, top_k, rerank y min_score, pero no el
  commit, ni el modelo de embeddings, ni los pesos RRF, ni la versión del chunker. Los pesos se
  recalibraron después de ese baseline, así que no es comparable con una corrida de hoy.
- `replicate-env/` es un repositorio git anidado, NO trackeado por el repo padre, con 11 MB: los seis
  scripts de reproducción del entorno (descarga de modelos, arranque de Qdrant, clonado de repos
  fuente, ingesta, restauración de la caché de resúmenes, verificación de conteos) y
  `data/summary-cache.sqlite3`. El aparato que hace reproducible el proyecto vive fuera de la
  historia del proyecto.
- `logs/` ocupa 178 MB en disco (correctamente ignorado por git). Los cinco documentos
  "Fase N - *.md" (26-33 KB cada uno) están en la raíz del repo, no en `docs/`.
- La rama principal del repo es `docs/architectural-design`, hay ramas `feat/*` de larga vida y un
  commit sin pushear.
- Contrapeso importante: el código está más limpio de lo habitual. Cero TODO/HACK/FIXME en `src/`,
  cero `async void`, cero `.Result`/`.Wait()` en Core, todos los `IAsyncEnumerable` con
  `[EnumeratorCancellation]`, cancelación re-lanzada en vez de tragada, sesiones ONNX dispuestas de
  forma determinista con un workaround de ARM64 documentado, y las decisiones no obvias comentadas
  con su razón. Los umbrales y pesos no son números mágicos sueltos: viven en records de opciones
  (`RetrievalFusionOptions`, `RagGenerationOptions`, `MetaIntentOptions`).

Reportado pero SIN verificar (el verificador no llegó a correr — compruébalo tú antes de usarlo):

- Que `TypeScriptChunkingStrategy` pierda el cuerpo de toda función cuya firma no lleve `{` en la
  misma línea (llave Allman, o firma partida en varias líneas por prettier), emitiendo un chunk con
  sólo la línea de la firma. Se alegó `TypeScriptChunkingStrategy.cs:246-263`, con el detalle de que
  `braceDepth` vale 0 al entrar en modo captura y eso cierra el bloque de inmediato.

Áreas que la auditoría NO alcanzó a cubrir y que tú sí debes cubrir: arquitectura y límites de
módulo (`Abstractions/`, lifetimes de DI, duplicación de wiring entre Api y Cli), configuración y
validación de opciones al arranque, observabilidad end-to-end, capa de generación como ingeniería,
madurez del aparato de evaluación, documentación y DX, seguridad de la superficie HTTP, portabilidad
ARM/x64, procedencia y licencia de los modelos descargados, coste de re-ingesta (una corrida real de
22.986 puntos tardó ~19 horas), migración del esquema del vector store cuando cambia el modelo de
embeddings, y actualización/borrado incremental de documentos ya ingestados.

## RESTRICCIONES DEL DUEÑO

Léelas como el filtro que decide qué entra al plan:

1. Un solo desarrollador. Cualquier propuesta que requiera un equipo o un mes de trabajo sin
   entregar valor intermedio está fuera.
2. Esto es investigación aplicada, y la velocidad de experimentación es un activo, no un descuido.
   Distingue con nombre y apellido dos cosas: el rigor que PROTEGE los resultados experimentales
   (poder reproducir un número, saber qué config lo produjo, detectar una regresión de recall) y la
   ceremonia que sólo daría sensación de profesionalismo (cobertura de tests por porcentaje,
   gates de CI sobre un repo de un autor, capas de abstracción "por si acaso"). Lo primero lo quiero.
   Lo segundo lo quiero explícitamente rechazado y justificado.
3. No inventes severidad. "Alta" es lo que rompe un resultado, corrompe el índice o me hace concluir
   algo falso. La ausencia de una práctica que aquí no tiene consumidor no es alta.
4. No propongas reescrituras de lo que funciona. Si algo está bien hecho, dilo y déjalo.
5. Nada de refactor grande sin criterio de verificación. Si tocar chunking o retrieval puede mover el
   recall, el plan debe decir cómo se mide antes y después, y con qué corpus.
6. El recurso escaso es el tiempo de máquina, no los tokens. La caché de resúmenes
   (`replicate-env/data/summary-cache.sqlite3`, 947 entradas) se indexa por
   `content_hash + prompt_version` y exige contenido BYTE-IDÉNTICO para pegar. Por lo tanto: todo
   cambio de chunking invalida la caché completa y obliga a regenerar resúmenes vía Ollama, que es el
   paso más lento de la ingesta (~19 h para bsuite-repo), y probar que no movió el recall exige dos
   corridas. Externalizar los prompts invalida la caché por la vía de `prompt_version` salvo que se
   haga como refactor byte-idéntico — el plan debe decirlo explícitamente y ordenar el trabajo para
   no pagar dos veces esa regeneración.
7. Presupuesta cada ítem del plan en las dos monedas: horas de máquina (ingestas, evals, builds) y
   esfuerzo mío. Y di para cada uno si merece verificación multi-agente o le basta la verificación
   mecánica. Por defecto basta la mecánica: la Ola 1 existe precisamente para construirla, y una vez
   que hay tests y evals reproducibles, un panel de agentes votando sobre un diff es peor y mucho más
   caro que correr `dotnet test` y `rag eval`. Reserva el pase multi-agente para los cambios donde un
   error es silencioso y caro de detectar.

## CÓMO QUIERO QUE TRABAJES

Escalona el trabajo en fases y no lances toda la flota de golpe: la auditoría anterior agotó el
límite de sesión con 14 agentes en paralelo. Corre una fase, revisa lo que volvió, decide la
siguiente. Prefiero tres workflows pequeños encadenados y con criterio tuyo entre ellos, que uno
grande que se muera a mitad.

Reglas de evidencia, no negociables:

- Cada hallazgo lleva `archivo:línea` o el comando ejecutado y su salida. Sin evidencia no se
  reporta, ni siquiera como sospecha, salvo que lo marques explícitamente como "no verificado".
- Prohibido el pattern-matching con proyectos parecidos. Si crees que algo falla, ábrelo y
  compruébalo en ESTE repo.
- Los hallazgos que vayas a poner arriba del plan pásalos por un verificador adversarial cuyo
  trabajo sea refutarlos, con instrucción de refutar ante la duda razonable. Cuéntame cuántos
  murieron ahí: es información sobre la calidad de la auditoría.
- Escepticismo con n=1. Un resultado sobre un eval-set chico o una sola pregunta no es evidencia
  suficiente para justificar un cambio de retrieval.
- Si un supuesto problema resulta que sí está bien resuelto, dilo. Los falsos positivos me cuestan
  más que los huecos, porque erosionan la confianza en todo el resto del documento.

## LO QUE NO HAY QUE TOCAR

Estas son decisiones tomadas con evidencia, no descuidos. Si el plan las mueve, tiene que
justificarlo con datos, no con "mejores prácticas":

- Los valores calibrados: min-score 0.10, RRF k=60, peso sparse 1.3, peso resumen 2.5. Se llegó a
  ellos por barrido y dan +16 puntos de recall@10 frente a los pesos planos.
- Con esos pesos, el rerank dejó de sumar recall. No lo re-introduzcas como mejora obvia.
- El boost estructural por afinidad de clase se abandonó a propósito: daba +6pp en un eval chico y
  0pp con regresiones en el corpus grande. El código se revirtió por completo.
- El circuit breaker de la Fase 2 de resúmenes, la preservación del vector de resumen en re-upsert,
  la humanización de identificadores del modo Simple y el workaround de ARM64 al liberar las
  sesiones ONNX: todo eso resuelve un problema real ya vivido.
- Cualquier cambio en la tokenización sparse o en el modelo de embeddings obliga a re-ingesta
  completa del corpus. Una re-ingesta de bsuite-repo son ~19 horas. Trátalo como lo que cuesta.

## ENTREGABLE

Un solo documento markdown en `docs/analisis-futuro/plan-global-ingenieria.md`, en español, con esta
estructura:

1. **Estado real del proyecto.** Media página, honesta, sin diplomacia: qué está sólido de verdad y
   qué está inmaduro. Con nombres de archivo.
2. **Inventario de brechas por eje.** Pruebas, CI/build, arquitectura, configuración y portabilidad,
   observabilidad y resiliencia, calidad de código, capa LLM, aparato de evaluación, seguridad de la
   API, documentación y DX, operaciones del corpus (re-ingesta, migración de esquema, actualización
   incremental). Cada brecha con su evidencia. Deduplicada: si dos ejes ven el mismo problema desde
   ángulos distintos, fusiónalos. Si un eje salió limpio, una línea y sigue.
3. **Los problemas ordenados por lo que de verdad importa.** Entre 8 y 12. Cada uno con: problema,
   evidencia, qué consecuencia concreta tiene si no se arregla, esfuerzo estimado en horas o días, y
   cómo sé que quedó arreglado.
4. **Plan por olas.** Tres olas con criterio de entrada y salida explícito. Ola 1: lo que hace
   auditable todo lo demás — la red mínima de seguridad y la reproducibilidad. Ola 2: la deuda
   estructural que hoy multiplica el coste de cada cambio. Ola 3: lo que abre camino a que esto sea
   usable por alguien que no seas yo. Para cada ítem: qué archivos toca, cómo se verifica, y cómo se
   revierte si sale mal.
5. **Decisiones que tengo que tomar yo.** Donde dos arreglos compiten, o donde hay que elegir entre
   rigor y velocidad. Preséntamelas como decisión con recomendación tuya y su razón, no como menú.
6. **Lo que deliberadamente NO está en el plan.** Con la razón por la que lo descartaste. Esta
   sección importa tanto como las demás: es donde demuestras que filtraste en vez de acumular.

Además del documento, **reconcilia el ledger de ejecución**
`docs/analisis-futuro/ejecucion-plan.estado.json`: ya está sembrado con 18 ítems provisionales de la
auditoría del 2026-08-20, cada uno con modelo, esfuerzo, si merece multiagente, criterio de
verificación y horas de máquina. Tu plan manda sobre esa semilla: agrega lo que falte, elimina lo que
descartes (moviéndolo a estado `descartado` con la razón, no borrándolo), y corrige las asignaciones
de modelo/esfuerzo que consideres mal calibradas — pero justifica cada cambio. Respeta el esquema:
ese archivo lo consume el comando `/plan-siguiente`, que ejecuta un ítem por invocación y se detiene.

Criterio para asignar modelo: Opus sólo donde el CRITERIO es el trabajo (diseñar el oráculo de un
test, decidir una descomposición, verificar un hallazgo dudoso). Haiku donde un `find`, el compilador
o un hash verifican. Sonnet el resto. Y `multiagente: true` sólo donde un error sería silencioso y
caro de detectar — no donde `dotnet test` ya responde.

Convenciones: no crees ni modifiques código de producto en este turno, sólo el documento del plan y
el ledger.
Si necesitas ejecutar `dotnet build`, `rag eval` o un script de `replicate-env/` para verificar algo,
hazlo, pero avísame antes si va a tardar horas. Si terminas commiteando, no agregues `Co-Authored-By`
ni ninguna referencia a la herramienta en el mensaje.

Empieza mostrándome tu plan de auditoría antes de lanzar la primera fase.
````


---

## Arnés de ejecución (ya construido)

Para que ejecutar el plan no se coma tu límite de golpe ni pierda trabajo al cortarse:

| Pieza | Qué hace |
|---|---|
| `docs/analisis-futuro/ejecucion-plan.estado.json` | Ledger con los 18 ítems, su modelo, esfuerzo, criterio de verificación y horas de máquina. Sobrevive al reinicio del límite, a `/clear` y a cerrar la terminal. |
| `/plan-siguiente` | Ejecuta **un** ítem, lo verifica, actualiza el ledger, commitea y **se detiene**. Pregunta antes de lanzar cualquier ítem de más de 1 hora de máquina. Acepta un id o `ola:N`. |
| `/plan-estado` | Estado del avance sin ejecutar nada. Corre en Haiku, cuesta casi nada. |

Cómo se traduce lo que querías:

- **"Que se detenga al 60% y siga después sin perder trabajo."** El porcentaje de tu límite no es
  legible por ningún script — sólo lo ves tú con `/usage`. Lo que sí es un techo duro: prefijar la
  invocación con un presupuesto, `+80k /plan-siguiente`, que hace fallar a los agentes al alcanzar
  ese output en vez de seguir a ciegas. Y no perder trabajo está resuelto por construcción: cada
  ítem cierra con commit, así que lo máximo que puedes perder es el ítem en curso.
- **"Ir incremental para no consumir el 100% de golpe."** Es el modo por defecto: un ítem por
  invocación. Corres `/plan-siguiente`, revisas, decides si sigues. Nada avanza sin que tú lo pidas.
- **"Sin aviso."** El comando te dice modelo, esfuerzo y horas de máquina ANTES de empezar, y para
  todo lo que pase de una hora te pide confirmación explícita.

Cadencia recomendada: `/plan-estado` para ver dónde estás, `/plan-siguiente` las veces que aguante la
ventana, y los ítems caros (2.1 son ~40 horas de máquina por las re-ingestas) los lanzas cuando
puedas dejar la máquina trabajando sola.
