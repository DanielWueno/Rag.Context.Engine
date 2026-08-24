---
description: Ejecuta UN solo ítem pendiente del plan de ingeniería y se detiene, dejando el trabajo commiteado
argument-hint: "[id del ítem, u ola:N para restringir a una ola]"
---

Ledger: la ruta la da `python3 infra/arnes/ledger_path.py`. Es la fuente de verdad del avance y
sobrevive al reinicio del límite de sesión, a `/clear` y a cerrar la terminal. Guía del arnés:
`infra/arnes/README.md`.

Argumento recibido: `$ARGUMENTS` (si viene vacío, toma el siguiente ítem pendiente en orden de ola).

## Protocolo — no lo negocies

1. **Lee el ledger** y elige el ítem: el primero en estado `pendiente`, respetando el orden de olas.
   Si `$ARGUMENTS` trae un id, usa ese. Si trae `ola:N`, restringe a esa ola.
   No empieces la Ola 2 con ítems de la Ola 1 pendientes, salvo que yo lo pida explícitamente: la
   Ola 1 construye la verificación de la que dependen las demás.

2. **Antes de tocar nada**, dime en tres líneas: qué ítem es, con qué modelo y esfuerzo lo vas a
   hacer según el ledger, y cuántas `horas_maquina` cuesta.
   - Si `horas_maquina` es mayor a 1, **pregúntame antes de arrancar**. Nada que ocupe la máquina
     durante horas se lanza por iniciativa propia.
   - Si el ítem tiene `advertencia_de_coste` en su ola, repítemela.

3. **Ejecuta delegando** a un subagente con el `modelo` y el `esfuerzo` que dice el ledger — usa el
   override de modelo del tool Agent, no lo hagas tú en el hilo principal salvo que el ledger diga
   `opus`. Ese campo existe para no pagar Opus por un `find`.
   Marca el ítem `en_curso` en el ledger antes de delegar, con la fecha.

4. **Verifica con el criterio que dice el ítem**, literal, no uno parecido. Si el criterio exige
   correr algo y no lo corriste, el ítem NO está hecho: déjalo `en_curso` y dímelo.
   Escepticismo con n=1: un caso que pasa no es el criterio. Y prueba el escenario de riesgo real,
   no sólo el benigno.

5. **Cierra el ítem**: actualiza el ledger a `hecho` con una línea de qué se hizo y qué evidencia lo
   prueba, y commitea el trabajo junto con el ledger en el mismo commit. Sin `Co-Authored-By` ni
   referencias a la herramienta en el mensaje.
   Si quedó a medias, estado `bloqueado` con la razón concreta — nunca `hecho` optimista.

6. **DETENTE.** Un ítem por invocación. No sigas con el siguiente ni "aprovechando que ya estoy
   aquí". Termina con: qué quedó hecho, qué sigue, y el comando exacto para continuar.

## Reglas de coste

- El trabajo se hace incremental por diseño: como cada ítem cierra con commit, lo máximo que se
  puede perder si el límite te corta es el ítem en curso.
- Si el ledger tiene un ítem en `en_curso` de una sesión anterior, retómalo antes de tomar uno nuevo,
  y empieza revisando `git status` y `git diff` para ver qué quedó a medio hacer.
- No expandas el alcance del ítem. Si encuentras otro problema, anótalo como ítem nuevo en `pendiente`
  al final de su ola y sigue con el tuyo.
- No uses fan-out multi-agente salvo que el ítem diga `"multiagente": true`. Cuando ya existe una
  verificación mecánica —tests, un linter, un eval reproducible— un panel de agentes votando sobre
  un diff es peor y mucho más caro que correr esa verificación. Resérvalo para los cambios donde un
  error sería silencioso y caro de detectar.

- Antes de cerrar, valida el ledger: `python3 infra/arnes/validar-ledger.py`. Un campo mal escrito
  no rompe nada visiblemente, sólo hace que la próxima invocación elija mal.
