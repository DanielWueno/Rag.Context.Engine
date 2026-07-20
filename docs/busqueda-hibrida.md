# Búsqueda híbrida

> Cómo se combinan la rama densa (semántica multilingüe) y la dispersa (léxica exacta) mediante fusión RRF, y qué significa realmente cada score.

## Por qué híbrida

- La rama **densa** entiende *significado*: "¿cómo se autentican los usuarios?" encuentra `ValidateCredentials` aunque no compartan ni una palabra.
- La rama **dispersa** ancla *identificadores exactos*: `AuditoriaResultadoHallazgo`, `fk_auditoria`, nombres de tablas — cosas que un embedding diluye.
- **RRF** (Reciprocal Rank Fusion) combina ambos rankings sin necesidad de calibrar escalas entre ellos: premia a los documentos que aparecen bien rankeados en *ambas* listas.

## Rama densa — `OnnxVectorizationBrain`

**Modelo:** `sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2`, export ONNX **int8 ARM64** (118 MB), 384 dimensiones, 50+ idiomas.

**Tokenización:** SentencePiece Unigram (XLM-RoBERTa). El tokenizador de `Microsoft.ML.Tokenizers` produce IDs en el espacio crudo del `.spm`; el brain los remapea al espacio del modelo con la convención fairseq:

```
modelo:  <s>=0   <pad>=1   </s>=2   <unk>=3   pieza_spm(i) → i+1
```

La secuencia final es `<s> …ids… </s>` con padding `<pad>=1` (anulado por la attention mask). Este mapeo está validado contra el `tokenizer.json` oficial del modelo (las piezas comienzan en el índice 4).

**Pooling:** mean pooling ponderado por attention mask + normalización L2 → la similitud coseno equivale al dot product.

**Calidad medida** (pares semánticamente equivalentes):

| Par | Coseno |
|---|---|
| "¿Qué reglas tiene una auditoría para ser creada?" ↔ "What rules does an audit have in order to be created?" | **0.92** |
| "Condiciones para registrar un hallazgo…" ↔ "Conditions to register a finding…" | **0.89** |
| Pregunta relevante ↔ chunk de código correcto | **~0.15** |
| Pregunta irrelevante ↔ mismo chunk | ~0.06 |

> La similitud **pregunta↔código** vive en una escala mucho más baja que la de pregunta↔pregunta. De ahí el default de `min-score = 0.10` (ver abajo).

## Rama dispersa — `SparseTokenizer`

Vector disperso BM25-style calculado en C# puro (sin modelo, sin red), zero-allocation.

**Pipeline por término:** extraer palabra (con split camelCase: `GetOrder` → `get`, `order`) → filtrar stop words ES/EN y keywords de código → lowercase → **folding de acentos** (`código` → `codigo`, `ñ` → `n`) → **stemming ligero** → MurmurHash3 mod 2²⁰.

**Stemming ligero unificado ES/EN** (estilo `SpanishLightStemmer` de Lucene): recorta el plural `-s` (evitando `-ss`) y la vocal temática final `a/o/e`, con longitud mínima 5 por regla. No busca corrección lingüística sino **determinismo simétrico** — índice y consulta aplican exactamente la misma función:

| Formas | Stem común |
|---|---|
| auditoría, auditoria, Auditorias | `auditori` |
| condicion, condiciones | `condicion` |
| hallazgo, hallazgos | `hallazg` |
| rule, rules | `rule` |
| ingesta ↔ ingest | `ingest` (convergencia cross-lingüe) |

> **Límite conocido de la convergencia cross-lingüe:** solo ocurre cuando las raíces coinciden
> tras el stemming. `import` (EN) ≠ `importar` (ES) — una pregunta en inglés sobre la clase
> `ImportarAuditoria` no ancla la rama dispersa. Regla práctica: pregunta en el idioma de los
> identificadores del corpus, o incluye el identificador exacto en la pregunta.

**Pesado — TF saturado:** `peso = tf/(tf+1) × penalización_IDF` con `tf = min(count, 10)`. La saturación mide *presencia* con rendimientos decrecientes. La fórmula proporcional anterior (`count/totalTerms`) sesgaba el ranking hacia micro-chunks: en un constructor de una línea, "auditoria" pesaba 0.33; en la clase de 200 términos que contenía la respuesta, 0.005. Las penalizaciones IDF estáticas castigan términos genéricos de código (`task`, `result`, `service`…); los sets se re-stemmean al inicializar para operar sobre términos ya normalizados.

## Fusión y umbrales — `QdrantSemanticRetriever`

```
QueryAsync(
  prefetch: [ denso  (limit = 4×TopK, ScoreThreshold = min-score),
              disperso (limit = 4×TopK) ],
  query: Fusion.Rrf,
  limit: TopK )
```

Tres reglas que importan:

1. **El pool de cada rama es 4× el corte final** (mínimo 40). Con listas de tamaño TopK, RRF solo puede intercalar dos listas cortas; con pools anchos emerge el *consenso* denso∩disperso, que es su verdadera señal.
2. **`min-score` se aplica SOLO al prefetch denso**, donde el score es coseno `[0..1]`. La rama dispersa no se filtra (sus dot products TF no tienen escala comparable) — así los anclajes léxicos exactos sobreviven aunque la semántica densa sea débil.
3. **El score final es RRF, no coseno.** Vale `Σ 1/(k + rank)` — el tope práctico observado es `0.5` (rank 1 de una rama). Es una función del *ranking*: no lo compares contra umbrales de similitud ni lo interpretes como "50% de parecido".

### Escala de `min-score` (denso, coseno)

| Valor | Efecto |
|---|---|
| `0.00` | Sin filtro — la rama densa devuelve sus vecinos más cercanos, sean buenos o no |
| **`0.10`** (default) | Piso de ruido: descarta vecinos irrelevantes (~0.06) sin sacrificar relevantes (~0.12–0.25) |
| `0.25+` | Solo matches semánticos muy fuertes; la rama densa se vacía con frecuencia (queda la dispersa) |
| `0.65` | ⚠️ Escala del modelo anterior — vacía la rama densa en toda consulta real |

## Re-ranking Cross-Encoder — `OnnxCrossEncoderReRanker`

Un bi-encoder (la rama densa de arriba) comprime query y chunk **por separado** y los compara
por coseno; un cross-encoder los lee **juntos**, con atención completa sobre el par
`(query, chunk)`, y resuelve interacciones que el bi-encoder pierde al comprimir cada lado a un
solo vector. El costo es una inferencia por candidato — por eso corre solo sobre el pool ya
reducido por RRF, nunca sobre la colección completa.

**Activación:** flag `--rerank` en `search`/`ask` (ver [guía CLI](guia-cli.md)). Sin el flag, el
re-ranker ni siquiera carga su modelo — el `InferenceSession` es `Lazy<T>`, así que no tener el
modelo descargado no afecta al flujo normal.

**Modelo:** `cross-encoder/mmarco-mMiniLMv2-L12-H384-v1`, export ONNX **int8 ARM64**,
multilingüe (afinado sobre mMARCO). Reutiliza el mismo tokenizador SentencePiece/XLM-R que el
bi-encoder — mismo remapeo fairseq, sin dependencias nuevas.

**Secuencia par (convención XLM-R):**

```
<s> …query… </s></s> …chunk… </s>
```

La query reserva como máximo la mitad de `MaxSequenceLength` (512 por defecto); el resto queda
para el chunk, que se trunca a lo que quepa. El logit de clasificación del modelo se mapea a
`[0..1]` con sigmoide.

**Qué cambia en `QdrantSemanticRetriever`:** con `UseReRanking` activo, el pool ya era 3×TopK
(reservado desde el Sprint 7 para esta palanca); ahora ese pool se re-puntúa candidato a
candidato y solo entonces se corta al TopK final — antes se descartaba sin usar.

> ⚠️ **El score cambia de naturaleza.** Sin `--rerank`, `SimilarityScore` es **RRF** (tope
> ~0.5). Con `--rerank`, es el **sigmoide del cross-encoder** (rango 0..1, con valores altos —
> 0.9+ — para matches fuertes). Son escalas incomparables entre sí: no interpretes un 0.5
> cross-encoder como "peor" que un 0.5 RRF.

**Verificado end-to-end** contra la colección `rag-engine` (427 chunks reales, no sintéticos):
pool de 15 candidatos (3×5) re-rankeado en **~700ms**, mejor score sigmoide **0.997**. Para la
misma consulta sin `--rerank`, los scores RRF fueron `0.5, 0.5, 0.39, 0.33, 0.33` — confirma que
son dos escalas distintas y que el camino de código realmente se ejecuta (no es un no-op).

**Costo:** cada candidato paga una pasada completa por el cross-encoder (secuencias hasta 512
tokens, mucho más largas que las ~150 típicas del bi-encoder) — de ahí el `BatchSize` menor
(8 vs. 32) en `CrossEncoderOptions`. Úsalo cuando la precisión importe más que la latencia: el
caso de uso pensado es exactamente el límite de síntesis que Sprint 7 dejó documentado
(preguntas donde el LLM 7B no lograba conectar el fraseo abstracto de la pregunta con el chunk
correcto entre varios candidatos semánticamente parecidos).

## El caso resuelto: español vs. inglés

Antes del rediseño, una consulta en español devolvía "0 resultados" mientras su traducción al inglés funcionaba. Eran cuatro capas alineadas en contra:

1. El modelo denso anterior (`all-MiniLM-L6-v2`) era monolingüe inglés → embeddings de consultas en español cuasi-aleatorios.
2. La rama dispersa hacía matching de hash exacto sin normalizar morfología ni acentos → `auditoría` ≠ `auditorias`.
3. `min-score` era un no-op (se perdió en la migración a RRF) y su escala documentada era de otro modelo.
4. Los chunks de clase llegaban casi vacíos al LLM (bug de chunking), que respondía honestamente "no encuentro información".

Cada capa se corrigió por separado; la verificación end-to-end (consulta en español sobre reglas de auditoría contra un corpus XAF real) produce hoy una respuesta fundamentada y citada en español.
