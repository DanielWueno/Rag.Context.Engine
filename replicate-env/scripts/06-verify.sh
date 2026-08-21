#!/usr/bin/env bash
# Verifica que la ingesta quedó como se espera: compara points_count real contra
# el valor conocido de la máquina original (donde existe evidencia).
set -euo pipefail

check() {
  local col="$1" expected="$2"
  local actual
  actual=$(curl -sf "http://localhost:6333/collections/$col" | python3 -c "import json,sys; print(json.load(sys.stdin)['result']['points_count'])" 2>/dev/null || echo "ERROR")
  if [ "$actual" = "ERROR" ]; then
    echo "❌ $col: no se pudo consultar (¿existe la colección? ¿Qdrant arriba?)"
  elif [ -n "$expected" ]; then
    echo "$col: $actual puntos (original: $expected)"
  else
    echo "$col: $actual puntos (sin valor de referencia conocido)"
  fi
}

echo "=== Verificación de colecciones ==="
check bsuite-repo 22986
check bsuite-auditorias-test ""
check innovapp-docs ""
check micro-repo ""
check wiki-solis ""
check rag-engine ""

echo
echo "=== rag doctor (dependencias: Qdrant, ONNX, disco) ==="
REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
dotnet run --project "$REPO_ROOT/src/RagEngine.Cli" -- doctor || true

echo
echo "=== Smoke test: pregunta real de bsuite-repo ==="
dotnet run --project "$REPO_ROOT/src/RagEngine.Cli" -- ask \
  "Como se genera el plan de auditoria?" -c bsuite-repo || true
