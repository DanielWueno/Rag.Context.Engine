#!/usr/bin/env bash
# Descarga los dos modelos ONNX que este proyecto usa hoy (ver appsettings.json):
#   - OnnxBrain (embeddings densos)  -> paraphrase-multilingual-MiniLM-L12-v2
#   - CrossEncoder (--rerank)        -> mmarco-mMiniLMv2-L12-H384-v1
# NO se necesita la variante "english" (all-MiniLM-L6-v2): existe en la máquina
# original pero ningún appsettings.json activo la usa.
set -euo pipefail

cd "$(dirname "$0")/../.."   # raíz del repo Rag.Context.Engine

bash infra/download-model.sh multilingual
bash infra/download-model.sh reranker

echo
echo "Modelos descargados en ./models/. Actualiza appsettings.json (OnnxBrain / CrossEncoder"
echo "ModelPath/VocabPath) si esta máquina usa una ruta absoluta distinta a /Users/DevStudio/models."
