# Configuración

> Referencia de `appsettings.json`, gestión de modelos ONNX y la matriz de "cuándo re-ingestar".

## `src/RagEngine.Cli/appsettings.json`

```jsonc
{
  "OnnxBrain": {
    "ModelPath":  ".../paraphrase-multilingual-MiniLM-L12-v2/model_qint8_arm64.onnx",
    "VocabPath":  ".../paraphrase-multilingual-MiniLM-L12-v2/sentencepiece.bpe.model",
    "TokenizerType": "SentencePiece",   // "SentencePiece" (XLM-R) | "WordPiece" (BERT)
    "MaxSequenceLength": 256,           // tokens; el padding real es dinámico por lote
    "BatchSize": 32,                    // chunks por inferencia
    "EmbeddingDimensions": 384          // debe coincidir con el modelo Y con la colección
  },
  "CrossEncoder": {
    "ModelPath": ".../mmarco-mMiniLMv2-L12-H384-v1/model_qint8_arm64.onnx",
    "VocabPath": ".../mmarco-mMiniLMv2-L12-H384-v1/sentencepiece.bpe.model",
    "MaxSequenceLength": 512,           // XLM-R admite hasta 512; no hay índice que re-ingestar
    "BatchSize": 8                      // secuencias más largas que el bi-encoder → batch menor
  },
  "Qdrant":   { "Host": "localhost", "GrpcPort": 6334, "HttpPort": 6333 },
  "Ingestion":{ "DefaultCollection": "rag-engine", "RepositoryName": "my-repo", "BatchSize": 32 },
  "Ollama":   { "Endpoint": "http://localhost:11434/v1", "ModelId": "qwen2.5-coder", "TimeoutSeconds": 120 }
}
```

### Campos críticos

| Campo | Regla |
|---|---|
| `TokenizerType` | Debe corresponder a la familia del modelo: `vocab.txt` → `WordPiece`; `sentencepiece.bpe.model` → `SentencePiece`. Un mismatch produce embeddings basura *sin error visible*. |
| `EmbeddingDimensions` | Debe coincidir con el modelo (384 en ambos MiniLM) y con las colecciones ya creadas. |
| `MaxSequenceLength` | Techo de truncamiento. El costo de inferencia escala con la longitud real del lote (padding dinámico), así que subirlo solo afecta a los chunks largos. |

## Modelos disponibles

`infra/download-model.sh` descarga a `models/` (o donde apunte tu config):

| Variante | Comando | Uso |
|---|---|---|
| **Multilingüe** (default) | `bash infra/download-model.sh` | `paraphrase-multilingual-MiniLM-L12-v2` — ES/EN y 50+ idiomas. Incluye `model.onnx` (fp32) y `model_qint8_arm64.onnx` (int8, **recomendado en Apple Silicon**: 2.3× más rápido, calidad casi idéntica) |
| Inglés (legacy) | `bash infra/download-model.sh english` | `all-MiniLM-L6-v2` — ~2× más rápido que el multilingüe, **sin soporte real de español** |
| Re-ranker (opcional) | `bash infra/download-model.sh reranker` | `mmarco-mMiniLMv2-L12-H384-v1` — Cross-Encoder multilingüe para `--rerank`. **No requiere re-ingesta**: solo actúa en tiempo de consulta. Detalle: [busqueda-hibrida.md](busqueda-hibrida.md#re-ranking-cross-encoder--onnxcrossencoderreranker) |

Para cambiar de modelo denso: descarga → apunta `ModelPath`/`VocabPath`/`TokenizerType` → **re-ingesta todas las colecciones**. El re-ranker es independiente de este ciclo — se puede activar/desactivar o cambiar de modelo sin tocar las colecciones ya ingestadas.

## Cuándo re-ingestar

Los vectores y términos almacenados quedan desalineados con las consultas cuando cambia cualquier pieza de la vectorización. Regla práctica:

| Cambio | ¿Re-ingestar? |
|---|---|
| Modelo denso (`ModelPath`, variante, dimensiones) | ✅ Sí, siempre |
| `TokenizerType` / archivo de tokenizador | ✅ Sí |
| Lógica del `SparseTokenizer` (stemming, folding, pesos, stop words) | ✅ Sí |
| Estrategias de chunking | ✅ Sí |
| `MaxSequenceLength`, `BatchSize` (`OnnxBrain`) | ⚠️ Recomendado solo si bajó el primero |
| `min-score`, TopK, opciones de búsqueda | ❌ No — son parámetros de consulta |
| Modelo o configuración de `CrossEncoder` (`--rerank`) | ❌ No — re-puntúa en tiempo de consulta, no toca vectores almacenados |
| Prompt del LLM, Ollama, contexto | ❌ No |
| Contenido editado de un archivo ya ingestado (código o docs) | ✅ Sí, con `--force` — ver nota abajo |

```bash
# Re-ingesta estándar
rag ingest /ruta/al/repo -c mi-coleccion --force
```

> **Sin re-ranking de vectorización de por medio, igual re-ingesta si editaste archivos ya
> indexados.** `OrphanChunkCleaner` (mencionado como ítem de checklist en la Fase 5) **no está
> implementado**: un `ingest` sin `--force` solo hace `EnsureCollectionAsync` + upsert, nunca borra
> puntos. Si una edición desplaza líneas o elimina una sección, los chunks viejos en esas
> posiciones quedan huérfanos en Qdrant con contenido obsoleto, sumados a los nuevos — el índice
> queda con basura además de estar potencialmente incompleto. Para automatización, usa el patrón
> ya documentado arriba: `curl -X DELETE http://localhost:6333/collections/<nombre>` y corre
> `ingest` sin `--force` (evita el prompt interactivo de TTY).

## Calibración de `min-score`

El default (`0.10`) está calibrado para el modelo multilingüe (relevante ≈ 0.12–0.25, ruido < 0.08 en pares pregunta↔código). Si cambias de modelo denso, **re-calibra**: mide el coseno entre una pregunta representativa y un chunk que sabes relevante vs. uno irrelevante, y fija el umbral entre ambas nubes. La escala completa está en [busqueda-hibrida.md](busqueda-hibrida.md#escala-de-min-score-denso-coseno).
