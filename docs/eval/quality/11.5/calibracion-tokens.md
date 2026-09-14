# 11.5 — Calibración de caracteres-por-token por lenguaje

Ítem: `11.5-charspertoken-por-lenguaje`. Fecha: 2026-09-14. Commit base: `8c1e978`
(HEAD al ejecutar la calibración; árbol sucio permitido para este ítem porque no
toca datos servidos, ver `_ejecucion` en el ledger).

## Qué mide esto y qué NO cambia

`TokenEstimator.CharsPerToken = 4.0` es una constante GLOBAL compartida por dos
familias de consumidores muy distintas:

1. **Geometría de chunking** (`ChunkBuilder.WindowGeometry`,
   `RoslynCSharpChunkingStrategy`, `TypeScriptChunkingStrategy.FitsInBudget`,
   `ParagraphBudget`): determina las FRONTERAS de los chunks ya indexados.
   Cambiarla mueve los cortes, invalida los hashes del índice y exige subir
   `ChunkingContract.Version` + reingestar (~19 h documentadas para bsuite-repo).
2. **Presupuesto de contexto en tiempo de consulta** (`ContextAssembler` en
   `Pipeline/`, usado sólo por `rag search --format markdown`; y el presupuesto de
   caracteres de `GenerationContextAssembler`, que en producción real ni siquiera
   llama a `TokenEstimator` — usa `MaxContextCharacters = 12 000` con un comentario
   que asume 4 chars/token): no toca el índice.

**Esta ficha calibra el error de la heurística compartida contra un tokenizador
real, pero NO cambia el valor de `TokenEstimator.CharsPerToken` ni ninguna
constante de chunking.** Los 0,2 h anunciados y la ausencia de confirmación de
coste de reingesta (>1 h) no cubren una reingesta de corpus; ese cambio, si algún
día se justifica con evidencia, es responsabilidad de `11.3-presupuesto-en-tokens-reales`
(bloqueado; no se persigue tras el resultado nulo de `11.2`). Lo que sí se hizo:
publicar el ratio/sesgo/error medido, fijarlo como fixture de test para detectar
drift grande, y añadir una comprobación de presupuesto de generación con un ratio
DISTINTO y conservador (documentado más abajo) que no requiere tocar el índice.

El presupuesto duro del **embedder** (`OnnxVectorizationBrain`, `MaxSequenceLength`)
ya usa tokens reales del tokenizador ONNX efectivo (WordPiece/SentencePiece según el
modelo) — no se tocó, no lo necesitaba.

## Tokenizador de referencia

`TiktokenTokenizer.CreateForModel("gpt-4")` (familia cl100k_base), vía el paquete
`Microsoft.ML.Tokenizers.Data.Cl100kBase` añadido SOLO al proyecto de tests. Es el
único tokenizador BPE de propósito general empaquetado como dependencia .NET
offline disponible en este entorno. **No es el tokenizador exacto del LLM de
generación local** (Qwen2.5-Coder vía Ollama no expone un tokenizador .NET
instalable sin red): el error medido aquí es un proxy razonable, no una medición
exacta contra ese modelo. Esta limitación es la razón por la que el criterio de
11.5 habla de "tokenizador... o estimación conservadora documentada distinta", y
es la interpretación que se aplicó.

## Muestra (censo/calibración/validación)

Fragmentos: ventanas no solapadas de 40 líneas por archivo, descartando restos
`< 200` caracteres. Partición determinista 70/30 por el primer byte del
SHA-256 del contenido del fragmento (estable entre corridas).

| Grupo | Fuente | n total | n calibración | n validación |
|---|---|---:|---:|---:|
| C# | `src/**/*.cs`, `tests/**/*.cs` (sin `bin/obj`) | 445 | 311 | 134 |
| TypeScript | `tests/RagEngine.Core.Tests/Fixtures/typescript-{sample,large}.fixture` (censo completo; sin variantes CRLF duplicadas) | 3 | 2 | 1 |
| Prosa | `docs/**/*.md` | 279 | 197 | 82 |

**Limitación honesta:** no hay corpus TypeScript en este repositorio (0 archivos
`.ts`; el chunker de TS sirve a repos externos ingeridos). El grupo TypeScript usa
censo completo de los fixtures versionados (`typescript-sample.fixture` +
`typescript-large.fixture`), muy por debajo del `>=100` que pide el criterio para
un censo *muestreado*; se documenta el n real en vez de inventar una muestra que
no existe. C# y prosa sí superan 100 fragmentos por split.

## Resultados (chars/token real medido, sesgo, error p95)

`sesgo` = media(tokens_estimados − tokens_reales) por fragmento (estimador =
`ceil(chars/4.0)`, la fórmula exacta de `TokenEstimator.Estimate`). `p95 error
relativo` = percentil 95 de `|estimado − real| / real`.

| Grupo | Split | n | chars/token real (agregado) | sesgo medio (tokens) | p95 error relativo |
|---|---|---:|---:|---:|---:|
| C# | calibración | 311 | 4.31 | +28.8 | 30.9% |
| C# | validación | 134 | 4.36 | +33.5 | 41.9% |
| TypeScript | calibración | 2 | 3.33 | −65.5 | 18.5% |
| TypeScript | validación | 1 | 3.22 | −113 | 19.3% |
| Prosa | calibración | 197 | 3.49 | −83.8 | 32.7% |
| Prosa | validación | 82 | 3.47 | −88.1 | 30.8% |

## Interpretación

- **C#**: el real es ~4.3–4.4 chars/token, MÁS holgado que el supuesto global de
  4.0. La heurística **sobreestima** el número de tokens (sesgo positivo) — dirección
  segura para un presupuesto (nunca deja pasar más contenido real del que cree).
- **TypeScript y prosa**: el real es ~3.2–3.5 chars/token, MÁS denso que 4.0. La
  heurística **subestima** el número de tokens reales (sesgo negativo) — dirección
  insegura: un presupuesto que asuma 4.0 chars/token sin margen para contenido de
  prosa/TS puede admitir más tokens reales de los que cree.
- El error p95 relativo (31–42%) confirma que ±15% (el comentario original de
  `TokenEstimator`) es optimista para C#/prosa en esta muestra; se documenta aquí,
  no se corrige la constante compartida por las razones de alcance ya explicadas.

## Qué se hizo con el hallazgo (sin tocar el índice)

`GenerationContextBudgetTests` (test project) usa el ratio más conservador medido
(TypeScript/prosa, redondeado hacia abajo a 3.0 chars/token por la incertidumbre
de n=3 en TS) para derivar un presupuesto de **tokens reales** de
`12 000 / 3.0 = 4 000 tokens`, y demuestra con el tokenizador real (cl100k) que:

1. Todos los casos de `GenerationGoldenCasos.Contexto` (los mismos que fija
   `GenerationContextGoldenTests` byte a byte) quedan por debajo de ese presupuesto.
2. Un caso adversarial construido con prosa REAL del propio repositorio (los
   fragmentos más densos del censo de `docs/**/*.md`, hasta llenar el límite de
   12 000 caracteres) tampoco lo excede.

`MaxContextCharacters` (12 000) NO se modificó: el golden byte a byte de
`GenerationContextGoldenTests` sigue intacto. Esta es una comprobación adicional,
no un cambio de comportamiento de producción.

## Reproducibilidad

```bash
dotnet test tests/RagEngine.Core.Tests --filter "FullyQualifiedName~Calibration"
```

Los números de esta tabla se generaron el 2026-09-14 con un test temporal
(`_RevealCalibration`, eliminado tras capturar los valores) que ejecuta las mismas
funciones (`TokenCalibration.CollectCSharp/CollectTypeScript/CollectProse` +
`Summarize`) que ahora fijan `TokenCalibrationTests` con tolerancias anchas
(detectan un cambio grande de composición del censo, no un dígito decimal).
