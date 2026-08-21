# Línea base de calidad de respuesta

`rag eval` mide **recall**: si el chunk correcto aparece en el top-K. Eso no dice
nada de lo que el usuario percibe. Estas tres cosas se venían juzgando a ojo:

1. ¿Responde cosas que no están en el corpus?
2. ¿Explica, o es tan corta que no dice nada?
3. ¿Sigue siendo técnica cuando se pidió lenguaje simple?

`infra/quality-baseline.py` re-corre el bundle de preguntas **reales**
(`replicate-env/data/questions/`, 427 registros → 143 únicas) contra la API y
guarda las respuestas completas junto a métricas mecánicas. La generación es la
parte cara (~8-11 s por pregunta); guardar las respuestas permite volver a
puntuarlas —con otra heurística, con un juez LLM o a mano— sin pagar otra corrida.

## Las tres posturas

La métrica central no es binaria. El motor casi nunca fabrica; lo que hace es
avisar que no encontró coincidencia clara y **entregar contenido igual**. Eso se
lee como responder fuera del corpus, así que se cuenta aparte:

| Postura | Qué significa |
|---|---|
| `rechazo_pleno` | Declina y no entrega contenido del corpus. |
| `banda_baja` | Avisa que la coincidencia es débil y aun así relata contenido. |
| `directo` | Responde sin reservas. |

`directo_con_score_bajo_pct` es el proxy más cercano a "responde fuera del
corpus": ni rechazo, ni aviso, con evidencia débil.

## Cómo comparar dos configuraciones

Se levantan dos instancias del **mismo binario** cambiando solo la bandera, y se
corre el arnés contra cada puerto:

```bash
ASPNETCORE_URLS=http://localhost:5083 \
  RagGeneration__EnableSimpleModeResumenContext=true \
  dotnet run --project src/RagEngine.Api &

ASPNETCORE_URLS=http://localhost:5081 \
  RagGeneration__EnableSimpleModeResumenContext=false \
  dotnet run --project src/RagEngine.Api &

python3 infra/quality-baseline.py --base-url http://localhost:5083 \
  --collection innovapp-docs --limit 20 --force-mode Simple --etiqueta ab-ON
```

Antes de confiar en un A/B, **verifica que la bandera se aplicó**. Una variable
mal nombrada produce un falso "no hay diferencia". La forma barata de comprobar
el mecanismo: arrancar con `RagGeneration__EnableSimpleModeSanitizer=false`, que
imprime un aviso al arranque. Si ese aviso sale, la convención
`RagGeneration__<Propiedad>` funciona.

## Límites conocidos

Las métricas son heurísticas, no un juez. Los contadores de léxico optimista y
vago señalan candidatos para revisión manual, no veredictos. La detección de
postura es por frases: al construirla, "que hora es" quedó mal clasificada como
`directo` porque el rechazo real —"no tengo la capacidad de proporcionar la hora
actual"— no estaba en la lista. Fue un falso negativo de la métrica, no un fallo
del motor. Si aparece un patrón de rechazo nuevo, hay que agregarlo.


## Línea base al 2026-08-21 (antes de la re-ingesta)

`linea-base-post-ajustes.json` — 40 preguntas reales en modo Simple (20 de
`innovapp-docs`, 20 de `bsuite-repo`), con el índice **anterior** a la re-ingesta y
los prompts ya ajustados. Es el archivo contra el que hay que comparar después.

| Métrica | Antes de los ajustes | Línea base actual |
|---|---|---|
| Rechazo pleno | 15,0% | **30,0%** |
| Banda baja | 40,0% | 32,5% |
| Directo con score bajo | 22,5% | **15,0%** |
| Palabras (mediana) | 63 | 54,5 |
| Respuestas cortas (<40 palabras) | 12,5% | 35,0% |
| Simple con tecnicismos | 7,5% | 7,5% |

El salto en "respuestas cortas" es **deseado**: son los rechazos de 28-52 palabras
que sustituyeron a fabricaciones de 222-349. No confundir brevedad con vaguedad
sin leer las respuestas.

Con n=40, un delta porcentual aislado cae dentro del ruido (error estándar ≈7 pp).
Lo que sostiene estos cambios es el comportamiento **caso por caso**, verificable
en el detalle del artefacto.

### Lo que sigue abierto

- La frase degenerada de banda baja aparece en **5 de 40**: "el fragmento más
  cercano dice que el contexto proporcionado no tiene una relevancia alta". El
  caso (b) del addendum no bastó.
- Una pregunta empeoró (372 → 524 palabras) y sigue fabricando.
- Los saludos "Hola" y "Continua" mantienen 93 palabras con identificadores
  PascalCase: van por un camino de código que estos prompts no tocan.
