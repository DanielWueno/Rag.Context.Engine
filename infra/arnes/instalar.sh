#!/bin/bash
# ============================================================
# instalar.sh — Copia el arnés de ejecución a otro proyecto.
# ============================================================
# Uso:
#   bash infra/arnes/instalar.sh /ruta/al/otro/repo
#   bash infra/arnes/instalar.sh /ruta/al/otro/repo --dry-run
#
# Qué instala:
#   infra/arnes/                       el arnés (scripts, plantilla, guía)
#   .claude/commands/plan-*.md         los dos comandos
#   .claude/settings.json              el hook SessionStart (MERGE, no reemplazo)
#   docs/plan/ejecucion-plan.estado.json   semilla del ledger, sólo si no hay uno
#
# Qué NO hace: no toca tu código, no commitea, y no sobrescribe un ledger
# existente ni el resto de tu settings.json.
# ============================================================

set -euo pipefail

# Escapes reales ($'...') y no cadenas '\033[...]': así funcionan tanto con
# `echo -e` como dentro del heredoc final, que no interpreta secuencias.
GREEN=$'\033[0;32m'; YELLOW=$'\033[1;33m'; RED=$'\033[0;31m'
CYAN=$'\033[0;36m'; NC=$'\033[0m'; BOLD=$'\033[1m'; DIM=$'\033[2m'

ORIGEN="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ORIGEN_RAIZ="$(cd "$ORIGEN/../.." && pwd)"
DESTINO="${1:-}"
DRY=0
[[ "${2:-}" == "--dry-run" ]] && DRY=1

if [[ -z "$DESTINO" || "$DESTINO" == "--help" || "$DESTINO" == "-h" ]]; then
  sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit 0
fi
[[ -d "$DESTINO" ]] || { echo -e "${RED}✗${NC} No existe el directorio: $DESTINO"; exit 1; }
DESTINO="$(cd "$DESTINO" && pwd)"
[[ "$DESTINO" == "$ORIGEN_RAIZ" ]] && { echo -e "${RED}✗${NC} El destino es el propio origen."; exit 1; }
command -v python3 >/dev/null || { echo -e "${RED}✗${NC} Hace falta python3."; exit 1; }

echo
echo -e "${BOLD}Arnés de ejecución${NC}"
echo -e "  origen:  $ORIGEN_RAIZ"
echo -e "  destino: $DESTINO"
[[ $DRY -eq 1 ]] && echo -e "  ${YELLOW}(dry-run: no se escribe nada)${NC}"
echo

hacer() { [[ $DRY -eq 1 ]] || eval "$@"; }
paso()  { echo -e "  ${GREEN}+${NC} $1"; }
salto() { echo -e "  ${DIM}=${NC} ${DIM}$1${NC}"; }

# ── 1. El arnés ─────────────────────────────────────────────────────────────
paso "infra/arnes/ (scripts, plantilla y guía)"
hacer "mkdir -p '$DESTINO/infra/arnes'"
for f in plan-run.sh plan-siguiente-linea.py ledger_path.py validar-ledger.py \
         ledger.plantilla.json instalar.sh README.md; do
  [[ -f "$ORIGEN/$f" ]] && hacer "cp '$ORIGEN/$f' '$DESTINO/infra/arnes/$f'"
done
hacer "chmod +x '$DESTINO/infra/arnes/'*.sh '$DESTINO/infra/arnes/'*.py 2>/dev/null || true"

# ── 2. Los comandos ─────────────────────────────────────────────────────────
paso ".claude/commands/plan-siguiente.md y plan-estado.md"
hacer "mkdir -p '$DESTINO/.claude/commands'"
for f in plan-siguiente.md plan-estado.md; do
  hacer "cp '$ORIGEN_RAIZ/.claude/commands/$f' '$DESTINO/.claude/commands/$f'"
done

# ── 3. El hook, mezclado sin pisar nada ─────────────────────────────────────
AJUSTES="$DESTINO/.claude/settings.json"
RESULTADO=$(DRY=$DRY AJUSTES="$AJUSTES" python3 - <<'PY'
import json, os

ruta, dry = os.environ['AJUSTES'], os.environ['DRY'] == '1'
COMANDO = ('cd "${CLAUDE_PROJECT_DIR:-.}" && python3 infra/arnes/plan-siguiente-linea.py '
           '2>/dev/null || true')
ENTRADA = {'hooks': [{'type': 'command', 'command': COMANDO, 'timeout': 5,
                      'statusMessage': 'Leyendo el ledger del plan'}]}

datos = {}
if os.path.isfile(ruta):
    try:
        datos = json.load(open(ruta, encoding='utf-8'))
    except json.JSONDecodeError:
        print('ERROR|El settings.json del destino no es JSON válido. No se toca.')
        raise SystemExit(0)

sesion = datos.setdefault('hooks', {}).setdefault('SessionStart', [])
ya = any(COMANDO in h.get('command', '')
         for grupo in sesion for h in grupo.get('hooks', []))
if ya:
    print('SALTO|El hook ya estaba instalado; no se duplica.')
else:
    sesion.append(ENTRADA)
    datos.setdefault('$schema', 'https://json.schemastore.org/claude-code-settings.json')
    if not dry:
        os.makedirs(os.path.dirname(ruta), exist_ok=True)
        with open(ruta, 'w', encoding='utf-8') as fh:
            json.dump(datos, fh, ensure_ascii=False, indent=2)
            fh.write('\n')
    otros = sum(len(g.get('hooks', [])) for k, v in datos['hooks'].items()
                for g in v if k != 'SessionStart')
    print(f'OK|Hook SessionStart añadido (se conservaron {otros} hook(s) de otros eventos).')
PY
)
case "${RESULTADO%%|*}" in
  ERROR) echo -e "  ${RED}✗${NC} ${RESULTADO#*|}" ;;
  SALTO) salto "${RESULTADO#*|}" ;;
  *)     paso "${RESULTADO#*|}" ;;
esac

# ── 4. La semilla del ledger, sólo si no hay uno ────────────────────────────
LEDGER_EXISTENTE="$(cd "$DESTINO" && python3 "$ORIGEN/ledger_path.py" 2>/dev/null || true)"
if [[ -n "$LEDGER_EXISTENTE" ]]; then
  salto "Ya hay un ledger en ${LEDGER_EXISTENTE#$DESTINO/} — no se toca."
else
  paso "docs/plan/ejecucion-plan.estado.json (semilla, con un ítem de ejemplo que hay que borrar)"
  hacer "mkdir -p '$DESTINO/docs/plan'"
  hacer "cp '$ORIGEN/ledger.plantilla.json' '$DESTINO/docs/plan/ejecucion-plan.estado.json'"
fi

# ── 5. Comprobar que lo instalado funciona ──────────────────────────────────
echo
if [[ $DRY -eq 1 ]]; then
  echo -e "${YELLOW}Dry-run: nada escrito.${NC} Quita --dry-run para instalar."
  exit 0
fi
echo -e "${BOLD}── Verificación ──${NC}"
(cd "$DESTINO" && python3 infra/arnes/validar-ledger.py) | sed 's/^/  /'
echo -n "  hook: "
(cd "$DESTINO" && echo '{}' | CLAUDE_PROJECT_DIR="$DESTINO" \
  python3 infra/arnes/plan-siguiente-linea.py >/dev/null 2>&1 \
  && echo -e "${GREEN}responde sin error${NC}" || echo -e "${RED}falla${NC}")

cat <<FIN

${BOLD}Siguientes pasos en $DESTINO${NC}
  1. Edita ${CYAN}docs/plan/ejecucion-plan.estado.json${NC}: borra el ítem de ejemplo y escribe los tuyos.
     Cada ítem necesita una ${BOLD}verificacion${NC} que sea un comando, no una intención.
  2. Valida:      ${CYAN}python3 infra/arnes/validar-ledger.py${NC}
  3. Mira el mapa:${CYAN}bash infra/arnes/plan-run.sh --solo-anunciar${NC}
  4. Ejecuta uno: ${CYAN}bash infra/arnes/plan-run.sh${NC}
  5. Abre ${CYAN}/hooks${NC} una vez en Claude Code para que cargue el hook nuevo.
  Guía completa: ${CYAN}infra/arnes/README.md${NC}
FIN
