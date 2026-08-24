#!/bin/bash
# ============================================================
# plan-run.sh — Ejecuta UN ítem del ledger en una sesión de
#               contexto limpio, anunciándolo antes.
# ============================================================
# Uso:
#   bash infra/arnes/plan-run.sh              # el siguiente pendiente
#   bash infra/arnes/plan-run.sh 5.0          # un ítem concreto
#   bash infra/arnes/plan-run.sh ola:5        # el siguiente de la Ola 5
#   bash infra/arnes/plan-run.sh 5.0 --solo-anunciar
#
# Por qué existe: /plan-siguiente delega la ejecución a un
# subagente, así que ESA parte ya corre en contexto limpio, pero
# el hilo principal acumula contexto entre invocaciones. Aquí
# cada ítem arranca un proceso `claude` nuevo: contexto limpio de
# verdad, con nombre de sesión propio, y el modelo y el esfuerzo
# tomados del ledger en vez de escritos a mano.
#
# El anuncio previo NO cuesta tokens: sale del ledger con python.
# ============================================================

set -euo pipefail

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'
CYAN='\033[0;36m'; NC='\033[0m'; BOLD='\033[1m'; DIM='\033[2m'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Raíz del proyecto: la del repo git si estamos dentro de uno, si no dos niveles
# por encima del script (infra/arnes/ -> raíz). No se asume dónde vive el arnés.
ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || (cd "$SCRIPT_DIR/../.." && pwd))"
LEDGER="$(cd "$ROOT" && python3 "$SCRIPT_DIR/ledger_path.py" 2>/dev/null || true)"

ARG="${1:-}"
SOLO_ANUNCIAR=0
[[ "${2:-}" == "--solo-anunciar" || "$ARG" == "--solo-anunciar" ]] && SOLO_ANUNCIAR=1
[[ "$ARG" == "--solo-anunciar" ]] && ARG=""

command -v claude >/dev/null || { echo -e "${RED}✗${NC} 'claude' no está en el PATH."; exit 1; }
if [[ -z "$LEDGER" || ! -f "$LEDGER" ]]; then
  echo -e "${RED}✗${NC} No encuentro el ledger."
  echo "   Se buscó en las rutas convencionales bajo $ROOT."
  echo "   Crea uno con:  cp $SCRIPT_DIR/ledger.plantilla.json docs/plan/ejecucion-plan.estado.json"
  echo "   o apunta a él: export PLAN_LEDGER=/ruta/al/ejecucion-plan.estado.json"
  echo "   Guía completa: $SCRIPT_DIR/README.md"
  exit 1
fi

# ── Resolver el ítem desde el ledger (coste: cero tokens) ────────────────────
FICHA="$(python3 - "$LEDGER" "$ARG" <<'PY'
import json, sys
ledger, arg = sys.argv[1], sys.argv[2]
d = json.load(open(ledger))
ola_filtro = int(arg.split(':', 1)[1]) if arg.startswith('ola:') else None
id_pedido  = arg if arg and not arg.startswith('ola:') else None

elegido = ola_elegida = None
for o in d['olas']:
    if ola_filtro is not None and o['ola'] != ola_filtro:
        continue
    for it in o['items']:
        if id_pedido:
            if it['id'].startswith(id_pedido):
                elegido, ola_elegida = it, o
                break
        elif it['estado'] in ('pendiente', 'en_curso'):
            elegido, ola_elegida = it, o
            break
    if elegido:
        break

if not elegido:
    print('ERROR\tNo hay ítem que encaje con: ' + (arg or '(siguiente pendiente)'))
    raise SystemExit(0)

campos = {
    'id': elegido['id'],
    'titulo': ' '.join(elegido['titulo'].split()),
    'modelo': elegido['modelo'],
    'esfuerzo': elegido['esfuerzo'],
    'horas': str(elegido['horas_maquina']),
    'estado': elegido['estado'],
    'multiagente': 'sí' if elegido['multiagente'] else 'no',
    'ola': str(ola_elegida['ola']),
    'ola_nombre': ola_elegida['nombre'],
    'verificacion': ' '.join(elegido['verificacion'].split()),
    'bloquea': ' '.join(elegido.get('bloquea', '').split()),
    'bloqueado_por': ' '.join(elegido.get('bloqueado_por', '').split()),
    'aviso_item': ' '.join(elegido.get('advertencia_de_coste', '').split()),
    'aviso_ola': ' '.join(ola_elegida.get('advertencia_de_coste', '').split()),
}
for k, v in campos.items():
    print(k + '\t' + v)
PY
)"

if grep -q '^ERROR' <<<"$FICHA"; then
  echo -e "${RED}✗${NC} $(sed -n 's/^ERROR\t//p' <<<"$FICHA")"; exit 1
fi
campo() { sed -n "s/^$1\t//p" <<<"$FICHA"; }

ID=$(campo id);         TITULO=$(campo titulo);   MODELO=$(campo modelo)
ESFUERZO=$(campo esfuerzo); HORAS=$(campo horas);  ESTADO=$(campo estado)
MULTI=$(campo multiagente); OLA=$(campo ola);      OLA_NOMBRE=$(campo ola_nombre)
VERIF=$(campo verificacion); BLOQUEA=$(campo bloquea)
BLOQ_POR=$(campo bloqueado_por); AVISO_I=$(campo aviso_item); AVISO_O=$(campo aviso_ola)

# ── Anuncio ─────────────────────────────────────────────────────────────────
echo
echo -e "${BOLD}────────────────────────────────────────────────────────────${NC}"
echo -e "${BOLD}  Ola $OLA — $OLA_NOMBRE${NC}"
echo -e "${BOLD}  $ID${NC}"
echo -e "${BOLD}────────────────────────────────────────────────────────────${NC}"
echo -e "  $TITULO"
echo
# Etiquetas ya alineadas a mano: printf cuenta BYTES, no caracteres, así que
# un %-12s desalinea en cuanto la etiqueta lleva un acento.
echo -e "  ${CYAN}modelo     ${NC} $MODELO / esfuerzo $ESFUERZO"
echo -e "  ${CYAN}máquina    ${NC} $HORAS h"
echo -e "  ${CYAN}multiagente${NC} $MULTI"
echo -e "  ${CYAN}estado     ${NC} $ESTADO"
echo
echo -e "  ${DIM}verificación:${NC} $VERIF" | fold -s -w 76 | sed '2,$s/^/               /'
[[ -n "$BLOQ_POR" ]] && { echo; echo -e "  ${RED}BLOQUEADO POR:${NC} $BLOQ_POR" | fold -s -w 76 | sed '2,$s/^/    /'; }
[[ -n "$BLOQUEA"  ]] && { echo; echo -e "  ${YELLOW}ESTE ÍTEM BLOQUEA:${NC} $BLOQUEA" | fold -s -w 76 | sed '2,$s/^/    /'; }
[[ -n "$AVISO_O"  ]] && { echo; echo -e "  ${YELLOW}COSTE (ola):${NC} $AVISO_O" | fold -s -w 76 | sed '2,$s/^/    /'; }
[[ -n "$AVISO_I"  ]] && { echo; echo -e "  ${YELLOW}COSTE (ítem):${NC} $AVISO_I" | fold -s -w 76 | sed '2,$s/^/    /'; }
echo

[[ $SOLO_ANUNCIAR -eq 1 ]] && exit 0

# ── Puertas antes de gastar ─────────────────────────────────────────────────
if [[ -n "$BLOQ_POR" ]]; then
  read -r -p "$(echo -e "${RED}Está bloqueado.${NC} ¿Ejecutar de todas formas? [s/N] ")" r
  [[ "$r" =~ ^[sSyY]$ ]] || { echo "Cancelado."; exit 0; }
fi
if awk "BEGIN{exit !($HORAS > 1)}"; then
  echo -e "${YELLOW}⚠${NC}  $HORAS horas de máquina. No se lanza por iniciativa propia."
  read -r -p "   ¿Confirmas? [s/N] " r
  [[ "$r" =~ ^[sSyY]$ ]] || { echo "Cancelado."; exit 0; }
fi
if ! git -C "$ROOT" diff --quiet || ! git -C "$ROOT" diff --cached --quiet; then
  echo -e "${YELLOW}⚠${NC}  El árbol tiene cambios sin commitear. El ítem cierra con commit y los arrastraría."
  git -C "$ROOT" status --short | sed 's/^/     /'
  read -r -p "   ¿Sigo? [s/N] " r
  [[ "$r" =~ ^[sSyY]$ ]] || { echo "Cancelado."; exit 0; }
fi

# ── Ejecución en contexto limpio ────────────────────────────────────────────
# Sesión nueva: no es un /clear sobre la sesión actual, es otro proceso.
# El nombre (-n) la hace identificable en /resume y en el título del terminal.
NOMBRE="plan $ID"
echo -e "${GREEN}▶${NC} Sesión nueva ${BOLD}\"$NOMBRE\"${NC} — modelo $MODELO, esfuerzo $ESFUERZO"
echo -e "${DIM}   (interactiva a propósito: el protocolo pide confirmación antes de una ingesta larga${NC}"
echo -e "${DIM}    y los permisos de escritura y commit se aprueban aquí)${NC}"
echo

set +e
claude -n "$NOMBRE" --model "$MODELO" --effort "$ESFUERZO" "/plan-siguiente $ID"
CODE=$?
set -e

echo
echo -e "${BOLD}── Cierre ──${NC}"
git -C "$ROOT" log --oneline -1
git -C "$ROOT" status --short | sed 's/^/  /'
python3 - "$LEDGER" "$ID" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
for o in d['olas']:
    for it in o['items']:
        if it['id'] == sys.argv[2]:
            print(f"  estado en el ledger: {it['estado']}")
PY
echo -e "${DIM}  siguiente: bash infra/arnes/plan-run.sh${NC}"
exit $CODE
