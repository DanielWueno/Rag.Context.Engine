# Contrato de trabajo del proyecto

Protocolo compartido por Claude Code, Copilot CLI y Gemini CLI. Estas instrucciones
orientan al agente; NO instalan hooks, cambian permisos ni sustituyen las puertas
ejecutables de `arnes-plan`. Se aplican a las sesiones de este repositorio, no a
otros proyectos.

## Fuente de verdad y alcance

- El trabajo y su avance viven en `docs/analisis-futuro/ejecucion-plan.estado.json`.
  Leerlo al iniciar o retomar trabajo; no sustituirlo por memoria, chat o listas
  internas del asistente. No copiar aqui el siguiente ID: cambia con el ledger.
- Los analisis en `docs/analisis-futuro/` son procedencia y propuestas. Incorporar
  sus decisiones al ledger antes de ejecutar; documentado no significa implementado.
- El foco actual es local. Servidores, identidad corporativa y multicomputo siguen
  como capacidades futuras, no como prerrequisitos de mejoras locales.
- No descartar una capacidad porque hoy falte el corpus o la infraestructura.
  Conservarla con su condicion de entrada y estado honesto.
- Una invocacion de ejecucion trabaja UN item y se detiene. Una peticion explicita
  de analizar o reconciliar el plan puede abarcar varias fichas, pero no autoriza
  ejecutar en paralelo sus cambios de producto.
- Ejecutar cada item en una conversacion nueva, cargando instrucciones, ficha,
  fuentes y codigo necesarios, no todo el chat anterior. Compactar conserva un
  resumen y no equivale a empezar sin historial. Una conversacion nueva comparte
  el arbol de trabajo: guardar el trabajo conocido antes del relevo y documentar
  cualquier cambio que deliberadamente quede fuera, sin borrarlo ni mezclarlo.

## Antes de ejecutar

1. Leer la ficha completa, sus fuentes y `git status`/`git diff`. Preservar cambios
   ajenos y no incorporarlos a un commit de otro item.
2. Retomar trabajo `en_curso` antes de abrir otro. El hook de Claude y el lanzador
   no siempre eligen igual: si el anuncio discrepa, usar el ID completo del trabajo
   a retomar, no asumir que el visor decide dependencias.
3. Comprobar estado, alcance, prerrequisitos y condiciones. `bloqueado_por` admite
   un ID literal; el bloqueo sigue vivo hasta que ese destino este `hecho`.
   Las notas `_...` no son controles automaticos. No ejecutar un item condicionado
   solo porque una nota no haya frenado al lanzador.
4. Anunciar ID, alcance, modelo/esfuerzo efectivos y `horas_maquina`. Mas de una
   hora de maquina requiere confirmacion antes de gastar. Si el coste estimado
   crece durante el trabajo, volver a aplicar esa puerta.
5. Exigir rollback y criterio literal antes de implementar. Si la ficha es ambigua
   o falta el oraculo, detener la ejecucion y completar su preparacion: no inventar
   el criterio despues de ver el resultado. Con el oraculo ya definido, construir
   fixtures y tests forma parte del item; su ausencia actual no justifica bloquearlo
   ni cambiar su prioridad.
6. Marcar `en_curso` antes de implementar o delegar. No hacer fan-out de ejecucion
   salvo `multiagente: true`; anunciar cualquier desviacion del modelo de la ficha.

## Evidencia y cierre

- Usar el criterio exacto, con negativos y escenario de riesgo. Un build verde,
  un archivo existente o cero tests seleccionados no prueban una mejora de recall.
- `verificacion_comando` debe ejecutar una comprobacion real y fallar si falta
  evidencia, cobertura o infraestructura requerida; nunca convertir una omision
  en exito. No poner comandos ficticios ni verificaciones parciales como cierre total.
- Maximo tres intentos ante el mismo fallo de implementacion. Si la ambiguedad
  esta en la ficha, parar inmediatamente, sin consumir esos intentos.
- No modificar anclas, umbrales o corpus para favorecer un resultado. Si el
  instrumento cambia justificadamente, registrar el cambio y medir ambos brazos
  con la misma procedencia. Una hipotesis nula es un resultado valido.
- Ejecutar el comando de aceptacion en una invocacion separada de la edicion, leer
  su salida y registrar en `resultado` que se hizo, evidencia y limites. No afirmar
  que lo ejecuto un hook si se corrio manualmente.
- `arnes validar --al-cerrar ID` comprueba la ficha; NO ejecuta por si mismo su
  `verificacion_comando`. No confundir validacion del JSON con aceptacion funcional.
- El cierre completo requiere resultado y cambios del item junto con el ledger en
  el mismo commit. Si no hay commit, decir "sin commit", no "cerrado conforme al
  arnes". No marcar `hecho` lo que quedo parcial: usar `en_curso` o `bloqueado`
  con motivo y condicion concreta de reentrada.
- No borrar descartados ni reescribir resultados historicos. Si aparece otro
  problema, registrarlo sin expandir el item actual.

## Puertas segun el entorno

| Entorno | Entrada y responsabilidad |
|---|---|
| Claude Code | `/arnes-plan:plan-siguiente ID` o `arnes ID`; respetar el protocolo y los hooks instalados del plugin. La forma corta `/plan-siguiente` no existe. |
| Copilot / Gemini | Aplicar este protocolo explicitamente. No asumir que los eventos de edicion de Claude se disparan aqui. Invocar las comprobaciones de forma visible; no simular protecciones automaticas. |
| Herramientas locales | `arnes ver --live`, `arnes validar` y `arnes --solo-anunciar` consultan el plan sin ejecutar un item. `arnes` sin esas opciones lanza Claude, no el asistente de esta sesion. |

La puerta de cierre de Claude informa al modelo, pero sale con codigo 0. El
lanzador puede devolver error al comprobar un cierre, sin revertirlo. Ninguno
garantiza que una instruccion en Markdown se cumpla. No usar `--igual` para saltar
puertas ni modos de permisos como sustituto de autorizacion de coste o alcance.

Actualizar el checkout de `arnes-plan` no actualiza el plugin instalado ni un
visor ya abierto. Consultar `arnes --version` cuando importe la compatibilidad.

## Seleccion de modelos sin romper el plugin

Conservar `modelo: haiku|sonnet|opus` y `esfuerzo` en el ledger: son el contrato del
plugin Claude, no IDs universales. No sustituirlos por `gpt-6-astra`.

Para ejecucion nativa en Copilot, esta tabla es una politica inicial de seleccion,
NO una equivalencia de calidad demostrada ni una configuracion automatica:

| Perfil de la ficha | Tipo de trabajo | Modelo candidato en Copilot |
|---|---|---|
| `haiku` | Mecanico, alcance pequeno y comprobacion directa | `gpt-5.4-mini` o `claude-haiku-4.5` |
| `sonnet` | Implementacion acotada con criterio ya definido | `claude-sonnet-5` o `gpt-6-astra`, segun resultados para ese trabajo |
| `opus` | Diseno experimental, causa raiz, arquitectura u oraculo dificil | `gpt-6-astra`; `claude-opus-5` como alternativa |

Usar solo modelos realmente disponibles. La sesion actual no cambia de modelo
por escribir esta tabla: seleccionar en la interfaz o mediante el override real
del subagente. Si no puede seleccionarse, declarar la limitacion; no reportar
como usado un modelo que solo figura en la ficha. Gemini debe declarar su modelo
efectivo disponible; no simular un alias Claude ni inventar una equivalencia.

Antes del trabajo, registrar la seleccion en una nota deliberada `_ejecucion`
de la ficha: entorno, modelo solicitado, modelo efectivo si se conoce, esfuerzo
efectivo y motivo. Al cerrar, incluir lo relevante en `resultado`. El plugin no
interpreta esa nota; sirve para procedencia, no para cambiar el enrutamiento.

GPT-6 Astra puede ser un candidato mejor para ciertas tareas, pero no se presume
superior por nombre o generacion. Si muestra mejores resultados para el trabajo,
preferirlo a Opus o Sonnet: los alias historicos del ledger no obligan a usar un
modelo Claude en Copilot. Priorizar correccion y fiabilidad; considerar coste y
latencia sin sacrificar el criterio de aceptacion.

Ajustar esta politica con resultados comparables:
mismo criterio, errores, retrabajo, latencia y consumo observado. No organizar un
panel de modelos por defecto; comparar solo cuando aporte evidencia al item.

## Reglas locales de datos

- Tests/builds: usar los comandos existentes y el alcance minimo que cubra el
  cambio. No fijar aqui un numero de tests o colecciones como si fuera permanente.
- Ingestas experimentales: coleccion aislada y cache SQLite separada. Nunca dos
  escritores host/contenedor sobre el mismo WAL; detener el consumidor compartido
  cuando el procedimiento requiera usar esa cache.
- El coste de resumen depende de misses de `(content_hash, prompt_version)`.
  Contarlos antes: ~19 h es referencia de regeneracion total, no de toda reingesta.
- Conservar control y rollback del indice; cambiar el codigo o apagar una bandera
  no revierte puntos ya indexados. No tocar colecciones servidas por una prueba.
