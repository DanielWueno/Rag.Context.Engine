---
description: Muestra el avance del plan de ingeniería sin ejecutar nada
model: haiku
---

Lee el ledger —la ruta la da `python3 infra/arnes/ledger_path.py`— y muéstrame, sin ejecutar ni
modificar nada:

1. Una tabla por ola: ítems `hecho` / total, y horas de máquina consumidas vs pendientes.
2. El ítem `en_curso` si hay uno, con su fecha — si lleva más de un día ahí, márcalo como sospechoso
   de haber quedado a medias.
3. Los `bloqueado` con su razón.
4. El siguiente ítem `pendiente` que tomaría `/plan-siguiente`, con su modelo, esfuerzo y horas de
   máquina.
5. Si hay ítems con `horas_maquina` mayor a 1 por delante en la ola actual, dime cuáles y cuánto
   suman, para que yo decida cuándo lanzarlos.

6. Si `python3 infra/arnes/validar-ledger.py` sale distinto de 0, dime qué se queja. Sólo eso: no
   lo arregles.

Sé breve. Nada de recomendaciones ni análisis: solo el estado.
