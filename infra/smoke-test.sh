#!/bin/bash
# ============================================================
# smoke-test.sh — Verifica el entorno RagEngine antes de usar
# ============================================================
# Uso: bash infra/smoke-test.sh
# ============================================================

set -e

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
CYAN='\033[0;36m'; NC='\033[0m'; BOLD='\033[1m'

pass() { echo -e "${GREEN}✓${NC} $1"; }
fail() { echo -e "${RED}✗${NC} $1"; EXIT_CODE=1; }
info() { echo -e "${CYAN}→${NC} $1"; }

EXIT_CODE=0
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
APPSETTINGS="$PROJECT_ROOT/src/RagEngine.Cli/appsettings.json"

echo ""
echo -e "${BOLD}🔍 RagEngine Smoke Test${NC}"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

# ── 1. .NET SDK ────────────────────────────────────────────
info "Verificando .NET SDK..."
if command -v dotnet &>/dev/null; then
    VER=$(dotnet --version)
    pass ".NET SDK instalado: $VER"
else
    fail ".NET SDK no encontrado. Instala desde https://dot.net"
fi

# ── 2. Build ───────────────────────────────────────────────
info "Compilando solución..."
if dotnet build "$PROJECT_ROOT/RagEngine.slnx" -c Release --verbosity quiet 2>&1 | grep -q "Compilación correcta\|Build succeeded"; then
    pass "Compilación exitosa"
else
    fail "Error de compilación. Ejecuta: dotnet build"
fi

# ── 3. Docker / Qdrant ────────────────────────────────────
info "Verificando Qdrant en Docker..."
if command -v docker &>/dev/null; then
    QDRANT_STATUS=$(docker ps --format '{{.Names}}: {{.Status}}' 2>/dev/null | grep -i qdrant || echo "")
    if [ -n "$QDRANT_STATUS" ]; then
        pass "Qdrant corriendo: $QDRANT_STATUS"
    else
        fail "Qdrant no está corriendo. Ejecuta: docker compose up -d"
    fi
else
    fail "Docker no encontrado"
fi

# ── 4. Qdrant gRPC health ─────────────────────────────────
info "Probando conectividad gRPC a Qdrant (localhost:6334)..."
if curl -s --max-time 3 "http://localhost:6333/collections" >/dev/null 2>&1; then
    COLL_COUNT=$(curl -s "http://localhost:6333/collections" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d.get('result',{}).get('collections',[])))" 2>/dev/null || echo "?")
    pass "Qdrant REST responde. Colecciones existentes: $COLL_COUNT"
else
    fail "Qdrant no responde en localhost:6333. ¿Está corriendo Docker?"
fi

# ── 5. Modelo ONNX ────────────────────────────────────────
info "Verificando archivos del modelo ONNX..."
MODEL_PATH=$(python3 -c "import json; d=json.load(open('$APPSETTINGS')); print(d['OnnxBrain']['ModelPath'])" 2>/dev/null || echo "")
VOCAB_PATH=$(python3 -c "import json; d=json.load(open('$APPSETTINGS')); print(d['OnnxBrain']['VocabPath'])" 2>/dev/null || echo "")

# Resolve relative to CLI output dir
CLI_OUTPUT="$PROJECT_ROOT/src/RagEngine.Cli/bin/Release/net10.0"
MODEL_FULL="$CLI_OUTPUT/$MODEL_PATH"
VOCAB_FULL="$CLI_OUTPUT/$VOCAB_PATH"

if [ -f "$MODEL_FULL" ]; then
    MODEL_SIZE=$(du -h "$MODEL_FULL" | cut -f1)
    pass "model.onnx encontrado ($MODEL_SIZE)"
elif [ -f "$MODEL_PATH" ]; then
    pass "model.onnx encontrado (ruta absoluta)"
else
    fail "model.onnx no encontrado en: $MODEL_FULL"
    info "Copia tu modelo: cp /ruta/a/model.onnx $CLI_OUTPUT/$MODEL_PATH"
fi

if [ -f "$VOCAB_FULL" ]; then
    pass "vocab.txt encontrado"
elif [ -f "$VOCAB_PATH" ]; then
    pass "vocab.txt encontrado (ruta absoluta)"
else
    fail "vocab.txt no encontrado en: $VOCAB_FULL"
fi

# ── 6. CLI ejecutable ─────────────────────────────────────
info "Verificando CLI ejecutable..."
CLI_BIN=$(find "$PROJECT_ROOT" -name "RagEngine.Cli" -path "*/bin/*" -not -path "*/obj/*" 2>/dev/null | head -1)
if [ -n "$CLI_BIN" ]; then
    pass "CLI compilado en: $CLI_BIN"
else
    info "CLI no compilado aún. Ejecuta: dotnet build -c Release"
fi

# ── Resumen ───────────────────────────────────────────────
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
if [ "$EXIT_CODE" -eq 0 ]; then
    echo -e "${GREEN}${BOLD}✅ Entorno listo. Puedes usar RagEngine.${NC}"
    echo ""
    echo -e "Próximos pasos:"
    echo -e "  ${CYAN}dotnet run --project src/RagEngine.Cli -- ingest ./src --collection test${NC}"
    echo -e "  ${CYAN}dotnet run --project src/RagEngine.Cli -- search \"tu consulta\" --collection test${NC}"
else
    echo -e "${RED}${BOLD}⚠  Hay problemas pendientes. Revisa los errores arriba.${NC}"
fi
echo ""
exit $EXIT_CODE
