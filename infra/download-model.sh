#!/usr/bin/env bash
# =============================================================================
# download-model.sh
# Downloads the all-MiniLM-L6-v2 ONNX model and tokenizer files
# from the Hugging Face Hub for use with Microsoft.ML.OnnxRuntime.
#
# Usage: bash infra/download-model.sh
# Output: models/all-MiniLM-L6-v2/
# =============================================================================

set -euo pipefail

MODEL_DIR="models/all-MiniLM-L6-v2"
HF_BASE="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main"

echo "📦 Creating model directory: $MODEL_DIR"
mkdir -p "$MODEL_DIR"

FILES=(
  "onnx/model.onnx"
  "tokenizer.json"
  "tokenizer_config.json"
  "vocab.txt"
  "special_tokens_map.json"
)

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
