#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Valida un ledger. Sale 0 si está bien, 1 si no, y dice qué falta.

Por qué existe: el ledger lo escriben personas y modelos a mano. Un campo
ausente o un `ola` como string en vez de entero no rompe nada visiblemente —
simplemente hace que /plan-siguiente elija mal, y eso se descubre tarde. Este
repo ya tuvo ese bug: la Ola 4 tenía su número como string.

Dos niveles de exigencia, y la diferencia importa:

  · Los campos estructurales (CAMPOS_ITEM) se piden a TODO ítem. Sin ellos el
    arnés no sabe ni con qué modelo lanzarlo.
  · `rollback` se pide sólo a los ítems que todavía van a ejecutarse. Es la
    regla que ya dice la plantilla — "si no lo sabes escribir, el ítem no está
    listo para ejecutarse" — y no se puede aplicar hacia atrás: 24 de los 25
    ítems ya cerrados se escribieron antes de que el campo existiera, y
    reclamárselo sería ruido permanente sobre trabajo que ya no se va a tocar.

Un campo presente pero en blanco cuenta como ausente: `"rollback": ""` no es
un plan de reversión, y dejarlo pasar convierte la comprobación en teatro.

Uso:
  python3 infra/arnes/validar-ledger.py [ruta]        # todo el ledger
  python3 infra/arnes/validar-ledger.py --item 4.2    # sólo ese ítem, por prefijo
"""
import json, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ledger_path import resolver  # noqa: E402

CAMPOS_OLA = {'ola', 'nombre', 'criterio_de_salida', 'items'}
CAMPOS_ITEM = {'id', 'titulo', 'modelo', 'esfuerzo', 'multiagente',
               'verificacion', 'horas_maquina', 'estado', 'por_que_este_modelo'}
# Se exigen sólo a los ítems que aún se van a ejecutar. Ver el docstring.
CAMPOS_EJECUTABLE = {'rollback'}
EJECUTABLES = {'pendiente', 'en_curso'}
ESTADOS = {'pendiente', 'en_curso', 'hecho', 'bloqueado', 'descartado'}
MODELOS = {'haiku', 'sonnet', 'opus'}
ESFUERZOS = {'low', 'medium', 'high', 'xhigh', 'max'}


def presente(it, campo):
    """Un campo en blanco cuenta como ausente. Los no-texto (bool, número)
    valen por el solo hecho de estar: `horas_maquina: 0` es un valor legítimo
    y `multiagente: false` también, así que aquí no se juzga su verdad."""
    if campo not in it:
        return False
    v = it[campo]
    return bool(str(v).strip()) if isinstance(v, str) else True


def sitio(o, n, m):
    """Etiqueta de posición legible, idéntica por las dos rutas del validador.
    Se prefiere el número de ola al índice de la lista: 'ola 4' es lo que la
    persona ve en el ledger, mientras que 'olas[3]' la obliga a contar. Si el
    número falta o no es entero cae al índice, porque ese caso ES un error que
    se reporta aparte y aquí sólo hace falta poder señalarlo."""
    ola = o.get('ola')
    cabeza = f'ola {ola}' if isinstance(ola, int) else f'olas[{n}]'
    return f'{cabeza}.items[{m}]'


def revisar_item(it, donde, errores, ids=None):
    """Acumula en `errores` los problemas de un ítem. `ids` opcional para
    detectar duplicados cuando se recorre el ledger entero."""
    iid = it.get('id', 'sin id')
    estado = it.get('estado')

    exigidos = set(CAMPOS_ITEM)
    if estado in EJECUTABLES:
        exigidos |= CAMPOS_EJECUTABLE
    faltan = {c for c in exigidos if not presente(it, c)}
    if faltan:
        # Se nombra la razón cuando el campo se exige por el estado: si no, un
        # "falta rollback" sobre un ítem hecho parecería un bug del validador.
        solo_ejecutable = sorted(faltan & CAMPOS_EJECUTABLE)
        estructurales = sorted(faltan - CAMPOS_EJECUTABLE)
        if estructurales:
            errores.append(f'{donde} ({iid}): faltan campos {estructurales}')
        if solo_ejecutable:
            errores.append(f'{donde} ({iid}): faltan campos {solo_ejecutable} '
                           f'(obligatorios con estado "{estado}": sin plan de '
                           f'reversión escrito, el ítem no está listo para ejecutarse)')

    # Se sigue comprobando lo que SÍ está: un campo ausente no debe tapar los
    # otros problemas del mismo ítem. Con un `continue` aquí, un id duplicado
    # pasaba invisible.
    if ids is not None and 'id' in it:
        if iid in ids:
            errores.append(f'{donde}: id duplicado "{iid}" (ya en {ids[iid]})')
        else:
            ids[iid] = donde
    for campo, validos in (('estado', ESTADOS), ('modelo', MODELOS),
                           ('esfuerzo', ESFUERZOS)):
        if campo in it and it[campo] not in validos:
            errores.append(f'{donde} ({iid}): {campo} "{it[campo]}" '
                           f'no es uno de {sorted(validos)}')
    if 'multiagente' in it and not isinstance(it['multiagente'], bool):
        errores.append(f'{donde} ({iid}): multiagente debe ser booleano')
    if 'horas_maquina' in it and not isinstance(it['horas_maquina'], (int, float)):
        errores.append(f'{donde} ({iid}): horas_maquina debe ser un número')


def informar(errores, ruta):
    print(f'✗ {len(errores)} problema(s) en {ruta}:', file=sys.stderr)
    for e in errores:
        print(f'  · {e}', file=sys.stderr)
    return 1


def main():
    args = sys.argv[1:]
    item_pedido = None
    if '--item' in args:
        i = args.index('--item')
        if i + 1 >= len(args):
            print('✗ --item necesita un id (o un prefijo).', file=sys.stderr)
            return 1
        item_pedido = args[i + 1]
        del args[i:i + 2]

    ruta = args[0] if args else resolver()
    if not ruta or not os.path.isfile(ruta):
        print('✗ No encuentro el ledger.', file=sys.stderr)
        return 1

    try:
        data = json.load(open(ruta, encoding='utf-8'))
    except json.JSONDecodeError as e:
        print(f'✗ JSON inválido en {ruta}: {e}', file=sys.stderr)
        return 1

    if not isinstance(data.get('olas'), list) or not data['olas']:
        print("✗ Falta la lista 'olas' o está vacía.", file=sys.stderr)
        return 1

    errores = []

    # ── Un solo ítem: la comprobación que corre antes de gastar ──────────────
    if item_pedido is not None:
        for n, o in enumerate(data['olas']):
            for m, it in enumerate(o['items']):
                if str(it.get('id', '')).startswith(item_pedido):
                    revisar_item(it, sitio(o, n, m), errores)
                    if errores:
                        return informar(errores, ruta)
                    if it['estado'] in EJECUTABLES:
                        print(f'✓ {it["id"]} está listo para ejecutarse '
                              f'(estado "{it["estado"]}").')
                    else:
                        # Válido, pero no es lo mismo que estar listo: a un ítem
                        # cerrado no se le pidió rollback, así que no se puede
                        # afirmar que se sabría revertir.
                        print(f'✓ {it["id"]} bien formado, pero su estado es '
                              f'"{it["estado"]}": no es un ítem ejecutable.')
                    return 0
        print(f'✗ Ningún ítem empieza por "{item_pedido}".', file=sys.stderr)
        return 1

    # ── Ledger completo ─────────────────────────────────────────────────────
    ids, numeros = {}, []
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
            revisar_item(it, sitio(o, n, m), errores, ids)

    if len(set(numeros)) != len(numeros):
        errores.append(f'Números de ola repetidos: {numeros}')

    if errores:
        return informar(errores, ruta)

    por_estado = {}
    for o in data['olas']:
        for it in o['items']:
            por_estado[it['estado']] = por_estado.get(it['estado'], 0) + 1
    resumen = ', '.join(f'{v} {k}' for k, v in sorted(por_estado.items()))
    print(f'✓ Ledger válido: {len(data["olas"])} olas, {len(ids)} ítems ({resumen}).')
    return 0


if __name__ == '__main__':
    sys.exit(main())
