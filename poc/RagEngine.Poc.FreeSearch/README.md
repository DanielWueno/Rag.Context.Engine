# PoC — Búsqueda libre (RRF a 3 bandas)

Esqueleto ejecutable del **paso 1** de
[`docs/analisis-futuro/busqueda-libre-rag-3-bandas.md`](../../docs/analisis-futuro/busqueda-libre-rag-3-bandas.md).

Responde **una** pregunta con un número, no con impresiones:

> ¿Añadir un vector de *resumen de negocio* por chunk mejora el **recall** de
> preguntas libres frente al baseline de sólo-código?

Todo corre **offline y en memoria**. No toca el pipeline de ingesta ni Qdrant: reutiliza
los tipos públicos de `RagEngine.Core` (chunkers y `OnnxVectorizationBrain`).

## Por qué mide recall y no "calidad del resumen"

Que un resumen se lea bien **no** prueba que embeberlo suba el chunk correcto al TopK.
El documento original proponía validar "calidad de resumen entre stacks"; eso valida la
variable equivocada. Este PoC mide `recall@k` del baseline vs. la fusión — ése es el
go/no-go real. La calidad por lenguaje se reporta aparte, como cobertura de sentinel
(gate cualitativo secundario).

## Las 4 fases (`Program.cs`)

1. **Chunking** — `ChunkHarvester` reutiliza los chunkers reales con `NullLogger`.
2. **Resúmenes** — `SummaryGenerator` llama a `qwen2.5-coder` vía Ollama con el prompt
   universal del documento (incluye el centinela `SIN_CONTENIDO_DE_NEGOCIO`).
3. **Embeddings** — `EmbeddingHarness` envuelve el mismo `OnnxVectorizationBrain`.
4. **Recall** — `RecallEvaluator` compara tres configuraciones por RRF ponderado:
   `código` (denso aislado) · `cód+sparse` (el baseline REAL de producción) ·
   `cód+sparse+resumen` (la propuesta completa). La comparación que decide es
   cód+sparse vs cód+sparse+resumen: el aporte del resumen sobre el híbrido real.

## Requisitos previos

- Ollama corriendo con el modelo: `ollama pull qwen2.5-coder`.
- El modelo ONNX de embeddings descargado (ver `infra/download-model.sh`); las rutas por
  defecto en `poc-settings.json` son las mismas del `appsettings.json` del CLI.

## Cómo correrlo

1. **Muestra**: ya viene apuntada al microservicio real de tickets
   (`Reyma.TI.Tickets.Microservice/src`) en `poc-settings.json`. C# puro — señal
   limpia; el dominio de tickets es justo el caso motivador del documento.
2. **Set de evaluación** (el trabajo humano y el que da valor): `eval/eval-set.sample.json`
   ya trae 19 preguntas libres reales con targets verificados por ruta + substring
   (crear/cerrar/reasignar/recibir ticket, mesa de ayuda, actividad, doc de cierre,
   autorización, dominio). **Revísalo**:
   confirma que cada `TargetContentContains` cae en el chunk que TÚ considerarías la
   respuesta correcta, y añade más casos difíciles. Ojo: en este repo las reglas son
   CQRS + FluentValidation (`OnValidate`, `AbstractValidator`), no atributos XAF.
3. Ejecuta:
   ```bash
   dotnet run --project poc/RagEngine.Poc.FreeSearch -- poc-settings.json
   ```

## Cómo leer el resultado

Tabla `recall@k` baseline vs. fusión, con Δ, y la lista de preguntas que la fusión
**rescató** o **regresionó** al menor k. Criterio de decisión sugerido:

- **Δ recall claramente positivo sin regresiones** → la hipótesis se sostiene; procede
  a Reto A (caché SQLite) y Reto B (fusor RRF ponderado) en el pipeline real.
- **Δ ≈ 0** → los resúmenes no mueven la aguja; no vale la complejidad del segundo vector.
- **Δ positivo pero con regresiones** → problema de pesos, no de la idea: calibra
  `WeightCode`/`WeightResumen`, o evalúa activar `--rerank` (el documento nota que el
  re-ranker suele arreglar el ranking si el recall ya es bueno).

## Prueba barata que conviene hacer ANTES

Los chunkers dimensionan a 512 tokens pero el embedder trunca a **256**
(`OnnxBrainOptions.MaxSequenceLength`): hoy la mitad de los chunks grandes no se
vectoriza. Parte de la brecha de recall del baseline podría ser este truncado, no la
falta de resumen. Corre el PoC una vez con `MaxSequenceLength` corregido en
`poc-settings.json` **antes** de comprometerte con todo el segundo pipeline de vectores;
quizá recuperas relevancia gratis.

## Límites deliberados del esqueleto

- **Sin sparse**: el PoC aísla el aporte del vector de resumen; el baseline aquí es
  denso-código solo, no la fusión híbrida completa de producción.
- **Sin caché** (Reto A): con ~100 chunks la generación es de minutos; el caché sólo
  importa a escala de re-ingesta real.
- **Sin persistencia**: todo vive en memoria durante la corrida.
