#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Localiza el ledger. Importado por el resto del arnés y ejecutable suelto
(imprime la ruta encontrada, o nada y exit 1).

Orden: la variable PLAN_LEDGER manda; si no está, se prueban las rutas
convencionales en orden. Existe porque el arnés se instala en repos que ya
tienen su propia carpeta de documentación y no todos la llaman igual.
"""
import os, sys

CANDIDATAS = (
    os.path.join('docs', 'plan', 'ejecucion-plan.estado.json'),
    os.path.join('docs', 'analisis-futuro', 'ejecucion-plan.estado.json'),
    os.path.join('.claude', 'plan', 'ejecucion-plan.estado.json'),
)


def raiz():
    """Raíz del proyecto: la que dice Claude Code, o el cwd."""
    return os.environ.get('CLAUDE_PROJECT_DIR') or os.getcwd()


def resolver(base=None):
    """Ruta absoluta del ledger, o None si no hay ninguno."""
    explicita = os.environ.get('PLAN_LEDGER')
    if explicita:
        return explicita if os.path.isfile(explicita) else None
    base = base or raiz()
    for c in CANDIDATAS:
        ruta = os.path.join(base, c)
        if os.path.isfile(ruta):
            return ruta
    return None


if __name__ == '__main__':
    ruta = resolver()
    if not ruta:
        sys.exit(1)
    print(ruta)
