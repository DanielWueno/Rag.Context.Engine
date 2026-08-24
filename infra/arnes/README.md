# Arnés de ejecución de planes de ingeniería

Una forma de trabajar con un asistente de código en tareas largas sin que se pierda el hilo,
sin que se dispare el coste y sin que nadie tenga que acordarse de en qué iba.

Es portable: vive en `infra/arnes/`, no depende del lenguaje ni del stack de este proyecto, y se
instala en otro repositorio con un comando.

---

## El problema que resuelve

Cuando le pides a un asistente que ejecute un plan de varias semanas, pasan cuatro cosas:

1. **Se pierde el avance.** La sesión se corta —límite de uso, un `/clear`, cerrar la terminal— y
   con ella se va el "por dónde íbamos". Lo que quedó a medias no está en ningún sitio.
2. **El coste se descontrola.** Todo se hace con el modelo más caro porque nadie decidió por
   adelantado cuál merecía cada tarea, y el contexto de la sesión crece arrastrando lo anterior.
3. **El alcance se desborda.** Empieza arreglando una cosa, encuentra otra, y tres horas después
   hay un diff de 40 archivos que nadie puede revisar.
4. **"Ya está" no significa nada.** Sin un criterio escrito ANTES, el criterio se escribe después,
   viendo el resultado — que es la forma estándar de conseguir el número que uno quería.

El arnés ataca las cuatro con la misma idea: **el plan vive en un archivo del repositorio, no en la
memoria de una conversación**, y se ejecuta un ítem por sesión.

---

## Las piezas

| Pieza | Qué hace |
|---|---|
| `docs/plan/ejecucion-plan.estado.json` | **El ledger.** La fuente de verdad del avance. Sobrevive al reinicio del límite, a `/clear` y a cerrar la terminal. Todo lo demás lo lee. |
| `/plan-siguiente` | Ejecuta **un** ítem, lo verifica con su propio criterio, actualiza el ledger, commitea y **se detiene**. Acepta un id o `ola:N`. |
| `/plan-estado` | El avance sin ejecutar nada. Corre en el modelo barato; cuesta casi nada. |
| `infra/arnes/plan-run.sh` | Lanza un ítem en una **sesión nueva** de Claude Code: contexto limpio de verdad, no un `/clear`. Anuncia el ítem antes y frena si algo no cuadra. |
| Hook `SessionStart` | Cada sesión arranca sabiendo qué ítem toca. Lo resuelve un script, así que averiguarlo cuesta cero tokens. |
| `infra/arnes/validar-ledger.py` | Comprueba que el ledger está bien formado. Un campo mal escrito no rompe nada visiblemente: sólo hace que la próxima invocación elija mal. |
| `infra/arnes/instalar.sh` | Copia todo esto a otro repositorio. |

---

## Empezar en este repositorio

Ya está instalado. Para usarlo:

```bash
bash infra/arnes/plan-run.sh --solo-anunciar   # ¿qué toca? (no ejecuta nada)
bash infra/arnes/plan-run.sh                   # ejecutar el siguiente, en sesión limpia
bash infra/arnes/plan-run.sh 5.0               # ejecutar uno concreto
bash infra/arnes/plan-run.sh ola:5             # el siguiente de una ola
```

Las banderas van en cualquier orden y se combinan con el id.

### Cuánta supervisión

| Modo | Qué cambia | Cuándo |
|---|---|---|
| *(por defecto)* | Sesión interactiva. Apruebas los permisos y las confirmaciones. | Casi siempre. Es el que no te sorprende. |
| `--auto` | Interactiva, pero el clasificador de auto mode resuelve los permisos rutinarios. Lo destructivo sigue frenando y tú sigues delante para confirmar una corrida larga. | Ítems con mucho toqueteo de archivos donde aprobar uno por uno sólo cansa. La primera vez, Claude Code pide aceptar el modo. |
| `--desatendido[=N]` | Sin sesión interactiva (`claude -p`), con techo de gasto en dólares (`N`, por defecto 5). | Los ítems mecánicos: `haiku`, cero horas de máquina. Revisas el commit al terminar, no durante. |

`--desatendido` **se niega** —y dice por qué— si el ítem pide más de una hora de máquina, lleva
`multiagente`, está bloqueado, o el árbol tiene cambios sin commitear. No es celo: en modo `-p` no
hay a quién preguntar, y el protocolo exige preguntar justo en esos casos. Una pregunta que nadie
puede responder no es una puerta, es un cuelgue. `--igual` salta las guardas bajo tu
responsabilidad.

Regla práctica: **si el ledger dice `haiku` y 0 horas, `--desatendido` es seguro; si dice `opus`,
quédate delante.** El campo `modelo` ya codifica cuánto criterio hace falta.

Dentro de una sesión de Claude Code ya abierta, lo mismo se pide con `/plan-estado` y
`/plan-siguiente`. La diferencia es el contexto: `plan-run.sh` arranca un proceso nuevo, así que el
ítem no hereda nada de lo que estuvieras haciendo antes.

**Cadencia recomendada:** `--solo-anunciar` para ver qué viene, ejecutar, revisar el commit, decidir
si sigues. Nada avanza sin que lo pidas.

---

## Instalarlo en otro proyecto

```bash
bash infra/arnes/instalar.sh /ruta/al/otro/repo --dry-run   # ver qué haría
bash infra/arnes/instalar.sh /ruta/al/otro/repo             # hacerlo
```

Copia el arnés, los dos comandos, **mezcla** el hook en el `settings.json` que ya hubiera —sin pisar
otros hooks— y siembra un ledger de ejemplo sólo si no encuentra uno. No toca tu código y no
commitea.

No hace falta que el otro proyecto sea .NET, ni que use las mismas carpetas: el ledger se busca en
`docs/plan/`, `docs/analisis-futuro/` y `.claude/plan/`, y la variable `PLAN_LEDGER` manda sobre
todas si lo tienes en otro sitio.

Después de instalar quedan tres cosas por hacer, y el instalador te las recuerda: **escribir tus
ítems** (lo único que no se puede automatizar), validar, y abrir `/hooks` una vez en Claude Code
para que cargue el hook nuevo.

---

## El ledger

```json
{
  "olas": [
    {
      "ola": 1,
      "nombre": "Red de seguridad y reproducibilidad",
      "criterio_de_entrada": "Ninguno. Es la primera.",
      "criterio_de_salida": "Una frase falsable. Si no se comprueba con un comando, no es criterio.",
      "advertencia_de_coste": "Opcional. Si la ola cuesta caro, dilo aquí: se repite antes de ejecutar.",
      "items": [ ... ]
    }
  ]
}
```

Cada ítem lleva estos campos, y todos son obligatorios:

| Campo | Para qué |
|---|---|
| `id` | `<ola>.<letra o número>-<slug>`. Es lo que se escribe en `plan-run.sh 5.0`. |
| `titulo` | **Qué pasa hoy**, no qué hacer. "Los logs viven dentro de `src/` y pesan 46 MB" se verifica; "mejorar el logging" no. |
| `verificacion` | El comando exacto que decide si está hecho, y qué salida cuenta como verde. Es el campo que más se descuida y el que más importa. |
| `modelo` | `haiku` \| `sonnet` \| `opus`. Ver abajo. |
| `esfuerzo` | `low` \| `medium` \| `high`. |
| `horas_maquina` | Tiempo de reloj, **no** tokens. Por encima de 1 el arnés pide confirmación. |
| `multiagente` | `true` sólo donde un error sería silencioso y caro de detectar. |
| `estado` | `pendiente` \| `en_curso` \| `hecho` \| `bloqueado` \| `descartado`. |
| `por_que_este_modelo` | Una línea. Obliga a justificar el gasto en vez de elegir por inercia. |

Opcionales que valen su peso: `archivos` (qué toca — evita que el ítem se desborde), `rollback`
(cómo se revierte; si no lo sabes escribir, el ítem no está listo), `bloquea` / `bloqueado_por`,
`origen` (por qué existe este ítem), y `resultado` (qué pasó, al cerrarlo).

### Cómo se elige el modelo

- **haiku** — lo verifica un `find`, el compilador o un hash. Pagar más aquí es tirar dinero.
- **sonnet** — el resto.
- **opus** — donde el CRITERIO es el trabajo: diseñar el oráculo de un test, decidir una
  descomposición, verificar un hallazgo dudoso. Un test que afirma el bug actual es peor que no
  tener test, y evitar eso es criterio, no ejecución.

### Las olas

No son sprints ni fases: son **dependencias**. La 1 construye la verificación de la que dependen
las demás, así que no se empieza la 2 con ítems de la 1 abiertos. Cada una declara qué tiene que
ser cierto para entrar y para salir.

---

## Las reglas que hacen que esto funcione

Están escritas dentro de los comandos, no dependen de que nadie se acuerde:

1. **Un ítem por invocación, y luego DETENERSE.** Nada de "ya que estoy aquí". Es lo que mantiene
   los diffs revisables.
2. **Commit al cerrar cada ítem.** Lo máximo que se puede perder si el límite corta la sesión es el
   ítem en curso.
3. **El criterio de éxito se declara antes de ejecutar**, dentro del ledger. Y en unidades que
   existan: con 40 preguntas etiquetadas, "+5 puntos porcentuales" no es una medida — "gana 4
   preguntas y no pierde ninguna" sí.
4. **Aviso de coste antes de gastar.** Por encima de 1 hora de máquina se pregunta.
5. **El alcance no se expande.** Si aparece otro problema, se anota como ítem nuevo al final de su
   ola y se sigue con el que estaba.
6. **Nada de multiagente por defecto.** Cuando ya hay verificación mecánica, un panel de agentes
   votando sobre un diff es peor y mucho más caro que correr los tests.
7. **Escepticismo con n=1**, y probar el escenario de riesgo real, no sólo el benigno.
8. **Un ítem que se descarta no se borra**: pasa a `descartado` con su razón. Es lo que evita que
   vuelva a proponerse desde cero dentro de tres meses.

---

## Para quien llega nuevo al equipo

Lo mínimo que necesitas saber, en orden:

1. **`bash infra/arnes/plan-run.sh --solo-anunciar`.** Te dice qué toca. No ejecuta nada, no gasta
   nada. Empieza por ahí.
2. **El ledger es el plan.** Si quieres saber por qué algo está como está, búscalo por su `id`: los
   ítems cerrados llevan un campo `resultado` con qué se hizo y qué evidencia lo prueba.
3. **No ejecutes un ítem "a mano" y lo des por hecho.** El valor está en que la verificación se
   corrió de verdad y quedó registrada. Un `hecho` optimista contamina todo lo que venga después.
4. **Si te encuentras otro problema mientras trabajas, no lo arregles de paso.** Añádelo como ítem
   `pendiente` al final de su ola, con su verificación. Cuesta dos minutos y ordena el trabajo.
5. **Si algo del arnés te confunde, es un fallo del arnés.** Está pensado para que la primera vez
   sea leer esta página y correr un comando.

---

## Preguntas que salen siempre

**¿Por qué una sesión nueva por ítem y no un `/clear`?**
`/clear` vacía la conversación pero reutiliza la sesión. Cada invocación de `claude` es un proceso
nuevo: contexto limpio de verdad, con su propio id recuperable desde `/resume`, y con el modelo y
el esfuerzo que dice el ledger en vez de los que arrastrara la sesión anterior.

**¿El hook gasta tokens en cada arranque?**
Averiguar qué ítem toca lo resuelve un script de Python, no el modelo: eso es gratis. Lo que sí
ocupa contexto son las dos líneas que inyecta, y por eso son dos y no veinte. Si el ledger no
existe o no se puede leer, el hook no imprime nada.

**¿Y si mi proyecto no tiene "horas de máquina"?**
Cambia la unidad por la que sea escasa —minutos de CI, cuota de una API, tiempo de un despliegue— y
dilo en el campo `_moneda` del ledger. Lo que importa es que exista un número que dispare la
confirmación antes de gastar.

**¿Puedo ejecutar los ítems sin supervisión?**
Los mecánicos sí: añade `-p --permission-mode acceptEdits --max-budget-usd N` a la llamada de
`claude` dentro de `plan-run.sh`. `--max-budget-usd` da un techo duro de gasto y sólo funciona con
`-p`. Los ítems que piden confirmación (más de una hora de máquina) deben seguir siendo
interactivos: en modo `-p` no hay a quién preguntar.

**¿Esto sirve si no uso Claude Code?**
El ledger, el validador y la disciplina sí — son un JSON y unas reglas. Los comandos y el hook son
específicos de Claude Code.

---

## Ficheros

```
infra/arnes/
  README.md                  esta guía
  plan-run.sh                lanza un ítem en sesión limpia
  plan-siguiente-linea.py    el hook SessionStart
  ledger_path.py             localiza el ledger (PLAN_LEDGER manda)
  validar-ledger.py          valida el ledger; sale 1 y dice qué falta
  ledger.plantilla.json      semilla para un proyecto nuevo
  instalar.sh                copia el arnés a otro repositorio
.claude/commands/
  plan-siguiente.md          /plan-siguiente
  plan-estado.md             /plan-estado
.claude/settings.json        el hook SessionStart
```
