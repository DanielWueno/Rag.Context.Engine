# Configuración

> Referencia de `appsettings.json`, gestión de modelos ONNX y la matriz de "cuándo re-ingestar".

## `src/RagEngine.Cli/appsettings.json`

```jsonc
{
  "OnnxBrain": {
    "ModelPath":  "${RAG_MODELS_DIR}/paraphrase-multilingual-MiniLM-L12-v2/model_qint8_arm64.onnx",
    "VocabPath":  "${RAG_MODELS_DIR}/paraphrase-multilingual-MiniLM-L12-v2/sentencepiece.bpe.model",
    "TokenizerType": "SentencePiece",   // "SentencePiece" (XLM-R) | "WordPiece" (BERT)
    "MaxSequenceLength": 256,           // tokens; el padding real es dinámico por lote
    "BatchSize": 32,                    // chunks por inferencia
    "EmbeddingDimensions": 384          // debe coincidir con el modelo Y con la colección
  },
  "CrossEncoder": {
    "ModelPath": "${RAG_MODELS_DIR}/mmarco-mMiniLMv2-L12-H384-v1/model_qint8_arm64.onnx",
    "VocabPath": "${RAG_MODELS_DIR}/mmarco-mMiniLMv2-L12-H384-v1/sentencepiece.bpe.model",
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

## Rutas y portabilidad

`appsettings.json` está versionado, así que no puede llevar rutas absolutas de una máquina
concreta. Las rutas de modelo admiten `~` y tokens `${VARIABLE}`, y las resuelve
`RagEngine.Core.Utilities.RagEnginePaths`:

| Variable | Default | Qué controla |
|---|---|---|
| `RAG_MODELS_DIR` | `~/models` | Raíz donde viven los modelos ONNX descargados. Una ruta relativa en `ModelPath`/`VocabPath` se ancla aquí, **nunca al directorio de trabajo**. |
| `RAG_LOGS_DIR` | `<raíz del repo>/logs` | Dónde escriben los sinks de Serilog. La raíz se localiza buscando `RagEngine.slnx` hacia arriba desde el binario; si no aparece (publish fuera del repo), cae a `logs/` junto al ejecutable. |

Un token sin definir se deja literal a propósito: así el error de "modelo no encontrado" muestra
`${RAG_MODELS_DIR}/...` tal cual, en vez de una ruta a medio construir.

Cuidado con `infra/download-model.sh`: descarga a `models/` **relativo al directorio desde el que
lo corres**, que no es necesariamente `$RAG_MODELS_DIR`. Córrelo desde la raíz de modelos, o
apunta `RAG_MODELS_DIR` a donde haya dejado los archivos.

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
| Contenido editado de un archivo ya ingestado (código o docs) | ⚠️ Basta `ingest` incremental — ver nota abajo |
| Reglas nuevas en `.ragignore` / `.gitignore` sobre rutas YA indexadas | ✅ Sí, con `--force` — ver nota abajo |

```bash
# Re-ingesta incremental: reindexa lo que cambió y borra lo que quedó obsoleto
rag ingest /ruta/al/repo -c mi-coleccion

# Reconstrucción completa (borra y recrea la colección); --yes la hace no interactiva
rag ingest /ruta/al/repo -c mi-coleccion --force --yes
```

> **Editar archivos ya indexados ya no exige `--force`.** El Id de chunk es
> `UUIDv5(rutaAbsoluta:startLine:hashContenido)`, así que una edición que desplaza líneas produce
> Ids nuevos y dejaba los viejos indexados para siempre. Desde `e559d80` la ingesta incremental
> **borra esos puntos obsoletos** al cerrar la Fase 1 (`DeleteSupersededPointsAsync`), y la línea
> de cierre del log reporta cuántos: `Obsoletos borrados: N`.
>
> Dos casos que la limpieza **no** cubre, y que sí necesitan `--force`:
>
> 1. **Archivos borrados del repo.** La limpieza sólo toca archivos que la Fase 1 procesó; uno que
>    ya no existe no se procesa, así que sus puntos sobreviven. Es deliberado: si un archivo falló
>    al leerse o al chunkearse, un fallo transitorio nunca debe borrar datos buenos.
> 2. **Rutas recién excluidas** por `.ragignore` o `.gitignore`. Excluir es dejar de procesar, así
>    que sus puntos quedan igualmente huérfanos.

## Calibración de `min-score`

El default (`0.10`) está calibrado para el modelo multilingüe (relevante ≈ 0.12–0.25, ruido < 0.08 en pares pregunta↔código). Si cambias de modelo denso, **re-calibra**: mide el coseno entre una pregunta representativa y un chunk que sabes relevante vs. uno irrelevante, y fija el umbral entre ambas nubes. La escala completa está en [busqueda-hibrida.md](busqueda-hibrida.md#escala-de-min-score-denso-coseno).
