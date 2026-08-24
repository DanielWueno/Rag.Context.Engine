#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Valida un ledger. Sale 0 si está bien, 1 si no, y dice qué falta.

Por qué existe: el ledger lo escriben personas y modelos a mano. Un campo
ausente o un `ola` como string en vez de entero no rompe nada visiblemente —
simplemente hace que /plan-siguiente elija mal, y eso se descubre tarde. Este
repo ya tuvo ese bug: la Ola 4 tenía su número como string.

Uso: python3 infra/arnes/validar-ledger.py [ruta]
"""
import json, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ledger_path import resolver  # noqa: E402

CAMPOS_OLA = {'ola', 'nombre', 'criterio_de_salida', 'items'}
CAMPOS_ITEM = {'id', 'titulo', 'modelo', 'esfuerzo', 'multiagente',
               'verificacion', 'horas_maquina', 'estado', 'por_que_este_modelo'}
ESTADOS = {'pendiente', 'en_curso', 'hecho', 'bloqueado', 'descartado'}
MODELOS = {'haiku', 'sonnet', 'opus'}
ESFUERZOS = {'low', 'medium', 'high', 'xhigh', 'max'}


def main():
    ruta = sys.argv[1] if len(sys.argv) > 1 else resolver()
    if not ruta or not os.path.isfile(ruta):
        print('✗ No encuentro el ledger.', file=sys.stderr)
        return 1

    try:
        data = json.load(open(ruta, encoding='utf-8'))
    except json.JSONDecodeError as e:
        print(f'✗ JSON inválido en {ruta}: {e}', file=sys.stderr)
        return 1

    errores, ids = [], {}
    if not isinstance(data.get('olas'), list) or not data['olas']:
        print("✗ Falta la lista 'olas' o está vacía.", file=sys.stderr)
        return 1

    numeros = []
    for n, o in enumerate(data['olas']):
        donde = f"olas[{n}]"
        faltan = CAMPOS_OLA - set(o)
        if faltan:
            errores.append(f'{donde}: faltan campos {sorted(faltan)}')
            continue
        if not isinstance(o['ola'], int):
            errores.append(f"{donde}: 'ola' es {type(o['ola']).__name__}, "
                           f"debe ser entero (un `ola:N` no emparejaría)")
        numeros.append(o['ola'])
        for m, it in enumerate(o['items']):
            donde_i = f"{donde}.items[{m}]"
            iid = it.get('id', 'sin id')
            faltan = CAMPOS_ITEM - set(it)
            if faltan:
                errores.append(f'{donde_i} ({iid}): faltan campos {sorted(faltan)}')
            # Se sigue comprobando lo que SÍ está: un campo ausente no debe
            # tapar los otros problemas del mismo ítem. Con `continue` aquí,
            # un id duplicado pasaba invisible.
            if 'id' in it:
                if iid in ids:
                    errores.append(f'{donde_i}: id duplicado "{iid}" (ya en {ids[iid]})')
                else:
                    ids[iid] = donde_i
            for campo, validos in (('estado', ESTADOS), ('modelo', MODELOS),
                                   ('esfuerzo', ESFUERZOS)):
                if campo in it and it[campo] not in validos:
                    errores.append(f'{donde_i} ({iid}): {campo} "{it[campo]}" '
                                   f'no es uno de {sorted(validos)}')
            if 'multiagente' in it and not isinstance(it['multiagente'], bool):
                errores.append(f'{donde_i} ({iid}): multiagente debe ser booleano')
            if 'horas_maquina' in it and not isinstance(it['horas_maquina'], (int, float)):
                errores.append(f'{donde_i} ({iid}): horas_maquina debe ser un número')

    if len(set(numeros)) != len(numeros):
        errores.append(f'Números de ola repetidos: {numeros}')

    if errores:
        print(f'✗ {len(errores)} problema(s) en {ruta}:', file=sys.stderr)
        for e in errores:
            print(f'  · {e}', file=sys.stderr)
        return 1

    total = len(ids)
    por_estado = {}
    for o in data['olas']:
        for it in o['items']:
            por_estado[it['estado']] = por_estado.get(it['estado'], 0) + 1
    resumen = ', '.join(f'{v} {k}' for k, v in sorted(por_estado.items()))
    print(f'✓ Ledger válido: {len(data["olas"])} olas, {total} ítems ({resumen}).')
    return 0


if __name__ == '__main__':
    sys.exit(main())
