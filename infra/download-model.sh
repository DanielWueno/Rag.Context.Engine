#!/usr/bin/env bash
# =============================================================================
# download-model.sh
# Downloads the ONNX embedding model + tokenizer files from Hugging Face Hub
# for use with Microsoft.ML.OnnxRuntime.
#
# Usage:
#   bash infra/download-model.sh                # multilingual (default)
#   bash infra/download-model.sh reranker       # cross-encoder para --rerank
#   bash infra/download-model.sh english        # legacy all-MiniLM-L6-v2
#
# Models:
#   multilingual → paraphrase-multilingual-MiniLM-L12-v2
#                  (50+ idiomas, 384 dims, tokenizer SentencePiece/XLM-R)
#   reranker     → mmarco-mMiniLMv2-L12-H384-v1 (cross-encoder multilingüe
#                  afinado sobre mMARCO; mismo tokenizer SentencePiece/XLM-R)
#   english      → all-MiniLM-L6-v2
#                  (monolingüe inglés, 384 dims, tokenizer WordPiece)
# =============================================================================

set -euo pipefail

VARIANT="${1:-multilingual}"

case "$VARIANT" in
  multilingual)
    HF_ORG="sentence-transformers"
    MODEL_NAME="paraphrase-multilingual-MiniLM-L12-v2"
    FILES=(
      "onnx/model.onnx"
      "onnx/model_qint8_arm64.onnx"   # int8 para Apple Silicon: ~2.3x más rápido, calidad casi idéntica
      "sentencepiece.bpe.model"
      "tokenizer.json"
      "tokenizer_config.json"
      "config.json"
    )
    ;;
  reranker)
    HF_ORG="cross-encoder"
    MODEL_NAME="mmarco-mMiniLMv2-L12-H384-v1"
    FILES=(
      "onnx/model.onnx"
      "onnx/model_qint8_arm64.onnx"   # int8 para Apple Silicon, igual que el bi-encoder
      "sentencepiece.bpe.model"
      "tokenizer.json"
      "tokenizer_config.json"
      "config.json"
    )
    ;;
  english)
    HF_ORG="sentence-transformers"
    MODEL_NAME="all-MiniLM-L6-v2"
    FILES=(
      "onnx/model.onnx"
      "tokenizer.json"
      "tokenizer_config.json"
      "vocab.txt"
      "special_tokens_map.json"
    )
    ;;
  *)
    echo "❌ Unknown variant '$VARIANT' (expected: multilingual | reranker | english)" >&2
    exit 1
    ;;
esac

MODEL_DIR="models/$MODEL_NAME"
HF_BASE="https://huggingface.co/$HF_ORG/$MODEL_NAME/resolve/main"

echo "📦 Creating model directory: $MODEL_DIR"
mkdir -p "$MODEL_DIR"

for FILE in "${FILES[@]}"; do
  DEST="$MODEL_DIR/$(basename $FILE)"
  if [ -f "$DEST" ]; then
    echo "✅ Already exists, skipping: $DEST"
  else
    echo "⬇️  Downloading: $FILE → $DEST"
    curl -fL --progress-bar \
      "$HF_BASE/$FILE" \
      -o "$DEST"
  fi
done

# Compute SHA-256 of the ONNX model for CollectionManifest validation
ONNX_PATH="$MODEL_DIR/model.onnx"
echo ""
echo "🔑 SHA-256 of model.onnx (save this for CollectionManifest):"
if command -v sha256sum &>/dev/null; then
  sha256sum "$ONNX_PATH"
else
  shasum -a 256 "$ONNX_PATH"
fi

echo ""
echo "✔  Model download complete. Files in: $MODEL_DIR"
echo ""
if [ "$VARIANT" = "reranker" ]; then
  echo "ℹ️  Recuerda que 'CrossEncoder' en appsettings.json debe apuntar a este"
  echo "   modelo (ModelPath/VocabPath). El re-ranker NO exige re-ingesta:"
  echo "   solo re-puntúa candidatos en tiempo de consulta (--rerank)."
else
  echo "ℹ️  Recuerda que 'OnnxBrain' en appsettings.json debe apuntar a este modelo"
  echo "   (ModelPath/VocabPath/TokenizerType) y que cambiar de modelo denso exige"
  echo "   re-ingestar las colecciones (rag ingest --force)."
fi
