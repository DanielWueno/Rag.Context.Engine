#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Hook SessionStart: escribe en el contexto de la sesión, en dos líneas, qué ítem
del plan toca ahora.

Por qué existe: con `infra/arnes/plan-run.sh` cada ítem corre en una sesión nueva, y
una sesión nueva no sabe nada. Esto hace que el "qué toca" aparezca solo tras un
/clear o al abrir una ventana, sin gastar una llamada al modelo para averiguarlo.

Contrato de silencio: si el ledger no está, no se puede leer o no tiene ítems
pendientes, este script NO imprime nada y sale con 0. Un hook que se queja en
cada arranque es peor que no tener hook.
"""
import json, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ledger_path import resolver  # noqa: E402

def main():
    ruta = resolver()
    if not ruta:
        return 0  # silencio deliberado: ver docstring
    try:
        with open(ruta, encoding='utf-8') as fh:
            data = json.load(fh)
        olas = data['olas']
    except Exception:
        return 0

    # Un ítem en_curso manda sobre el siguiente pendiente: el protocolo de
    # /plan-siguiente pide retomarlo antes de tomar uno nuevo.
    elegido = ola = None
    for estado_buscado in ('en_curso', 'pendiente'):
        for o in olas:
            for it in o['items']:
                if it['estado'] == estado_buscado:
                    elegido, ola = it, o
                    break
            if elegido:
                break
        if elegido:
            break

    if not elegido:
        return 0

    pendientes = sum(1 for o in olas for i in o['items'] if i['estado'] == 'pendiente')
    titulo = ' '.join(elegido['titulo'].split())
    if len(titulo) > 110:
        titulo = titulo[:107].rstrip() + '...'

    marca = ' [EN CURSO — retomar antes de tomar otro]' if elegido['estado'] == 'en_curso' else ''
    linea = (
        f"Plan de ingeniería — siguiente: {elegido['id']}{marca} "
        f"(Ola {ola['ola']}: {ola['nombre']}) · {elegido['modelo']}/{elegido['esfuerzo']} · "
        f"{elegido['horas_maquina']} h de máquina · {pendientes} ítems pendientes en total.\n"
        f"{titulo}"
    )
    if elegido.get('bloqueado_por'):
        linea += f"\nBLOQUEADO POR: {' '.join(elegido['bloqueado_por'].split())}"
    linea += ("\nNo lo ejecutes por iniciativa propia: se lanza con "
              "`bash infra/arnes/plan-run.sh` (sesión limpia) o `/plan-siguiente`.")

    json.dump({'hookSpecificOutput': {
        'hookEventName': 'SessionStart',
        'additionalContext': linea}}, sys.stdout, ensure_ascii=False)
    return 0


if __name__ == '__main__':
    sys.exit(main())
