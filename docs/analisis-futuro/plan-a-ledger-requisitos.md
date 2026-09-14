# Del plan al ledger: requisitos derivados de las reconciliaciones reales

> Insumo del ítem `10.3-plan-a-ledger-con-computo-local`. Registra qué decisiones exige convertir un
> documento de análisis en ítems del ledger, con el caso que las produjo. **Se escribe durante cada
> reconciliación, no después:** lo que se pierde al reconstruirlo es exactamente lo que costó decidir.
>
> **Lectura vigente (2026-09-14):** los casos de agosto describen el arnés de entonces, no el
> checkout actual. En 1.17.4 ya se validan esquema, IDs y dirección de dependencias; no se
> interpretan notas `_` ni se planifican múltiples prerequisitos. La corrección actual está en §5.

## Casos base

| Fecha | Documento de origen | Resultado |
|---|---|---|
| 2026-08-24 | [plan-refinamiento-arquitectonico-produccion.md](plan-refinamiento-arquitectonico-produccion.md) | Olas 5 a 8 y el ítem 1.15 (17 ítems desde 570 líneas) |
| 2026-08-31 | [arquitectura-puertos-y-adaptadores.md](arquitectura-puertos-y-adaptadores.md) | Olas 9 y 10, más los ítems 4.9 y 8.g (11 ítems desde 371 líneas) |
| 2026-09-12 | [lightrag-rag-anything-y-los-huecos-del-motor.md](lightrag-rag-anything-y-los-huecos-del-motor.md), [viabilidad-computo-remoto.md](viabilidad-computo-remoto.md) y pendientes de los dos casos anteriores | 32 ítems nuevos y 14 fichas ampliadas; propuestas incorporadas/absorbidas, sin cerrar trabajo técnico |

Del caso de 2026-08-24 sólo se conserva lo que dejó escrito el campo `_reconciliacion` del ledger; el
resto de sus decisiones no se registró y hoy no es recuperable. Ésa es la razón de este archivo.

## 1. Reglas del mapeo

### 1.1 No todo hallazgo es un ítem

Un hallazgo se convierte en ítem sólo si tiene **alcance acotado** y **criterio de verificación
mecánico**. El resto va a una sección del documento de análisis, no al ledger.

*Caso 2026-08-31:* de quince grietas identificadas, cuatro quedaron en "Registrados, no
planificados" (opciones repartidas por cuatro capas, RRF duplicado entre PoC y producción, métricas
como estático global, y un tipo de dominio muerto que además ya estaba en el ledger). Meterlas habría
subido el delta de 11 a 15 ítems sin que ninguno fuera ejecutable.

**Automatizable:** no. Es la decisión de más juicio de todo el proceso.

### 1.2 La ola se elige por criterio de salida, no por tema

Un ítem cae en la ola cuyo `criterio_de_salida` lo cubre literalmente, aunque otra ola se le parezca
más por tema.

*Caso 2026-08-31:* el contrato de score parecía de la ola nueva de arquitectura, pero el
`criterio_de_salida` de Ola 4 dice *"el score que decide la banda no cambia según el TopK"* — es
exactamente eso. Quedó como `4.9`. Y a la inversa: los ítems de dirección de dependencias encajaban
temáticamente en Ola 2 ("Deuda estructural que multiplica el coste de cada cambio"), pero Ola 2 está
cerrada con su criterio cumplido; añadir allí la habría reabierto.

**Regla derivada:** nunca añadir a una ola cuyos ítems estén todos en `hecho`.

**Automatizable:** parcial. La máquina puede descartar olas cerradas y ofrecer las candidatas; elegir
entre ellas requiere leer el criterio.

### 1.3 Una ola nueva necesita criterio de salida propio

No basta con que los ítems sean afines: hace falta un criterio que ninguna ola existente cubra.

*Caso 2026-08-31:* se crearon dos olas y no una. Ola 9 cierra cuando ningún host construye un cliente
de Qdrant; Ola 10 cierra cuando la documentación de proceso describe el proceso que existe. Son
condiciones independientes y mezclarlas habría dado una ola que no se puede declarar terminada.

**Automatizable:** no.

### 1.4 El sufijo del id se hereda de la ola, no del esquema global

El ledger mezcla dos convenciones y no hay una regla global: Ola 4 numera (`4.4`, `4.9`), Olas 5 a 8
usan letras (`5.f`, `8.g`). Un ítem nuevo toma la convención **de su ola**.

*Caso 2026-08-31:* `4.9` y `8.g` en la misma reconciliación, con sufijos de distinto tipo, por esta
razón.

**Automatizable:** sí, inspeccionando los ids existentes de la ola destino.

### 1.5 `bloqueado_por` es un id literal y forma un grafo que nadie valida

Hoy nada comprueba que el id referenciado exista, ni que el grafo sea acíclico.

*Caso 2026-08-31:* se crearon cuatro aristas (`4.9→4.5`, `9.2→9.1`, `9.5→9.4`, `10.3→4.8`), todas
verificadas a mano contra la lista de ids.

*Caso 2026-08-31, hallado al ejecutar por primera vez la comprobación que esta regla pide:*
`7.c-rerank-no-por-defecto` tenía como `bloqueado_por` una frase en prosa — *"4.2 (score de gate
estable). Ver el campo bloquea de 4.2."* — y no un id. Ninguna comprobación automática podía
resolverla. Normalizado a `4.2-score-de-gate-estable`; el texto no se perdió porque sólo remitía al
campo `bloquea` de 4.2, que sigue ahí.

**Automatizable:** sí, y debería ser una validación de esquema, no una comprobación manual. La
primera ejecución de esa validación ya encontró un defecto que llevaba en el ledger desde su
redacción.

### 1.6 `verificacion` es el campo que no se puede derivar del análisis

Es el más caro y el único que no está contenido en el documento de origen: exige saber qué comando u
observación demuestra que el ítem cerró.

*Caso 2026-08-31:* la verificación de `9.2` es *"añadir `PrivateAssets=\"all\"` y que la solución
compile"*. Ese criterio no estaba en el análisis inicial — lo aportó la revisión adversarial al notar
que ni `Api.csproj` ni `Cli.csproj` declaran `Qdrant.Client`. Sin esa observación el ítem habría
llevado una verificación mucho más débil.

**Automatizable:** no.

### 1.7 `rollback` es obligatorio, y para ítems con riesgo de datos no vale "git revert"

*Caso 2026-08-31:* tres de los once ítems pueden destruir trabajo y su `rollback` nombra el mecanismo
concreto: el cursor de dominio de `9.1` puede romper la reanudación de Fase 2 (~19 h de resúmenes);
`UpsertBatchAsync` borra `SummaryVector` si se omite en el upsert; tocar `prompt_version` al pasar por
`9.4` invalida la caché entera.

**Automatizable:** no. Pero sí es automatizable **exigir** el campo y marcar como sospechoso un
`rollback` que sólo diga "git revert" en un ítem que toca ingesta, caché o esquema.

### 1.8 La reconciliación es también una pasada de auditoría sobre lo que ya existe

No es sólo añadir. Ambos casos corrigieron ítems vigentes.

*Caso 2026-08-24:* corrigió el título de 2.2 (decía 1042 líneas, eran 680 desde que 2.3 externalizó
los prompts) y su criterio de verificación, que invitaba a citar 89 preguntas del arnés de calidad
como si midieran recall.

*Caso 2026-08-31:* detectó que `4.7` llevaba `bloqueado_por: 4.3` con 4.3 ya en `hecho` — desbloqueado
sin que nada lo señalara — y que su causa raíz era un ítem que no existía todavía (`9.3`).

**Automatizable:** parcial (ver 2.1).

### 1.9 Hay que deduplicar contra el ledger vigente

Un análisis re-descubre cosas ya registradas. Sin este paso se crean ítems duplicados.

*Caso 2026-08-31:* dos de los quince hallazgos ya estaban en el ledger — la regla de banda duplicada
en la API (`4.7`) y el `CollectionManifest` sin uso (`5.f`). Ninguno generó ítem nuevo.

**Automatizable:** parcial. Buscar por archivo y símbolo citados en el hallazgo contra los títulos y
`origen` de los ítems existentes da candidatos; confirmarlos requiere leer.

### 1.10 Las afirmaciones del análisis se verifican contra el árbol en el momento de reconciliar

Los documentos de análisis envejecen. Un hallazgo puede haber cambiado de gravedad entre que se
escribió y que se convierte en ítem.

*Caso 2026-08-31:* el análisis reportó que el appsettings de la API no tiene sección
`RetrievalFusion` mientras el del CLI sí, con los pesos calibrados. Verificado contra el código,
resultó que los defaults de `RetrievalFusionOptions` **son** esos mismos pesos: no hay divergencia
hoy, sólo una trampa para la próxima recalibración. El ítem `8.g` está redactado como trampa, no como
bug. Escribirlo sin verificar habría dejado en el ledger un defecto que no existe.

**Automatizable:** no, pero el script debe **exigir** que cada ítem cite evidencia (`archivo:línea`)
y puede comprobar que esos archivos y líneas siguen existiendo.

### 1.11 El orden de ejecución es información que el esquema no guarda

`bloqueado_por` expresa dependencias duras, pero no el orden recomendado cuando no hay dependencia.

*Caso 2026-08-31:* la decisión "la barrera va después de tapar, no antes" no es una dependencia
técnica, es una lección — un test de arquitectura escrito primero sólo produce una lista de
excepciones que nadie vacía. Se codificó a la fuerza como `bloqueado_por: 9.4` en `9.5` y como un
campo `_orden` inventado en la ola. Ninguna de las dos es la forma correcta.

**Automatizable:** no, pero el esquema debería tener dónde ponerlo.

## 2. Defectos de esquema detectados

### 2.1 No hay vista derivada de "listo para ejecutar"

Un ítem `pendiente` cuyo `bloqueado_por` apunta a un ítem `hecho` está desbloqueado, pero nada lo
dice. Se descubre leyendo.

*Caso 2026-08-31:* `4.7` llevaba desbloqueado desde el commit `18d00b6` y salió a la luz por
casualidad, durante una revisión que iba de otra cosa. Su campo `origen` incluso avisaba de que
importaba justo entonces.

*Segundo caso, misma fecha:* al normalizar el `bloqueado_por` de `7.c` (ver 1.5) y poder resolverlo
por fin, resultó que su bloqueo — `4.2` — también estaba en `hecho`. Dos de los veintisiete ítems
pendientes estaban ejecutables y ninguno de los dos lo anunciaba. El defecto no es raro: es lo que
pasa por defecto.

**Requisito para 10.3:** calcular y mostrar los ítems ejecutables, y señalar los `bloqueado_por` que
apuntan a ítems ya cerrados. Es la funcionalidad de mayor retorno de todo el ítem, y la más barata.

### 2.2 `_reconciliacion` es un string que crece por concatenación

Cada reconciliación añade texto al mismo campo. En 2026-08-31 se concatenó con un separador ` | `
porque convertirlo en lista cambiaría el tipo y podría romper el plugin `arnes-plan`, que lo consume.

**Decisión aplazada, no resuelta.** Si 10.3 toca el esquema, éste es el momento de convertirlo en
lista de objetos con fecha, documento de origen e ítems añadidos — y de verificar qué hace el plugin
con ese campo antes.

### 2.3 El esquema no valida nada

No hay comprobación de ids únicos, de `bloqueado_por` resoluble, de campos obligatorios ni de tipos
consistentes entre olas. En 2026-08-31 todo eso se comprobó con un script desechable después de
escribir.

*Precedente:* la reconciliación de 2026-08-24 encontró que la Ola 4 tenía su número como string
`"4"` mientras las otras lo tenían como entero, de modo que un argumento `ola:4` no la habría
emparejado. Un defecto de tipo, invisible, que sólo apareció al tocar el archivo.

**Requisito para 10.3:** el validador es más valioso que el generador, y es la parte fácil.

### 2.4 El orden del documento ES el calendario, y nada protege ese invariante

El arnés elige el siguiente ítem con una regla lineal: el primero en orden de documento cuyo estado
sea `pendiente` o `en_curso` (`scripts/ver.py:139-152` del plugin `arnes-plan`). No pondera
prioridad y **no consulta `bloqueado_por`**. Sí salta los que tienen `estado: bloqueado` — por eso
`3.5-portabilidad-x64` nunca se propone.

De ahí que el ledger no planifique, sino que ordene: `bloqueado_por` es documentación y la posición
en el archivo es el calendario real. La selección lineal es correcta exactamente mientras se cumpla
un invariante:

> **todo `bloqueado_por` apunta hacia atrás en el orden del documento.**

*Caso 2026-08-31:* las cuatro aristas creadas en esta reconciliación (`4.9→4.5`, `9.2→9.1`,
`9.5→9.4`, `10.3→4.8`) lo cumplen, y no hay ninguna que apunte hacia adelante en todo el ledger.
Pero lo cumplen porque se colocaron así, no porque nada lo exija: `scripts/validar-ledger.py` trata
`bloqueado_por` sólo como clave opcional conocida (línea 75) y no comprueba ni que el id exista ni
hacia dónde apunta.

La arista hacia adelante no es un caso raro: es lo que ocurre por defecto cuando un hallazgo nuevo
se añade al final del ledger y un ítem anterior pasa a depender de él. Entonces el arnés propone un
ítem bloqueado. Lo avisa (`scripts/plan-siguiente-linea.py:60-61` imprime `BLOQUEADO POR:`), pero no
lo salta.

**Requisito para 10.3 — validador, no planificador.** La comprobación de dirección es una condición
de escritura, no de lectura: rechazarla al reconciliar cuesta mover un ítem de sitio; resolverla al
leer exige inventar semántica de prioridad y cambia qué significa "el siguiente", que es lo único
que el protocolo mantiene deliberadamente aburrido para no relitigarlo cada sesión.

## 3. Lo que no se debe automatizar

### Caso 2026-09-12: capacidades condicionales, orden y compatibilidad

- **Incorporar no es ejecutar.** La ausencia de corpus binario, XAML o SQL hoy no descarta
  su capacidad futura: fichas abiertas con inventario, condición medible y rollback;
  desde la corrección del 14 de septiembre las condiciones incumplidas tienen estado `bloqueado`.
  `5.a` conserva su descarte histórico; `15.6` registra el trabajo futuro sin reescribirlo.
- **Deduplicar incluye la verificación.** `12.3 → 5.f`, `12.4/12.6 → 9.1`,
  `12.5 → 7.b` y `12.7 → 5.e`; se amplían las fichas destino y se guardan IDs completos
  en `_trazabilidad_propuestas`. No crear fichas «hechas» por editar criterios.
- **La fuente no aporta prioridades irrevocables.** La decisión local del usuario sustituye
  el orden empresarial de aquel análisis. Actor/estado locales y contratos con fixtures no
  requieren IdentityServer; integración real, Postgres compartido y cómputo remoto quedan
  bloqueados con momento de activación explícito, no descartados.
- **Compatibilidad comprobada, no supuesta.** Se inspeccionó el plugin local `arnes-plan`
  1.16.0: `bloqueado_por` es un único ID literal. `_dependencias_adicionales` documenta IDs
  secundarios verificados en esta reconciliación, pero el consumidor no los interpreta.
  Tampoco salta `_condicion_de_ejecucion` ni `_diferido`. La pauta anterior de mantener
  `pendiente` y continuar por ID era incorrecta: ahora se usa `bloqueado` hasta su reentrada.
  Para prerequisitos independientes no cubiertos por una cadena real se aplica el mismo estado;
  una nota por sí sola no frena nada.
- **El calendario obliga a mover sin renumerar.** Ola 11 se sitúa antes de Ola 5; `5.h`
  conserva su ID y se ubica primero en Ola 11. Su experimento posterior queda bloqueado
  por calidad y desactivado por defecto. La prioridad sigue siendo `11.1 → 11.2`;
  el anuncio actual es `11.1`, que incluye construir su instrumento (ver §5).
  Medir truncamiento no depende de cerrar ese filtro. Las dependencias duras apuntan hacia atrás. `11.4`
  nunca depende de `5.e`: mide el control previo; si el experimento es nulo no exige
  cerrar `11.3`, cuya condición alternativa se revisa manualmente.
- **Se reconcilian contradicciones de aceptación.** Rechazo 400 no es clamp; con n=47
  una pregunta equivale a 2,13 pp, no ±1 pp; 19 h no es coste fijo de re-ingesta.
  Contar misses de `(raw content_hash, prompt_version)` y verificar longitud entrenada
  antes de ampliar ventana. Para salto, reconciliar ≥4 preguntas y 25 pp como
  `max(4, ceil(0,25*n_salto))`, fijando n antes de medir.
- **Validación realizada en este caso:** JSON, campos obligatorios, unicidad de IDs/olas,
  referencias y ciclos de ambas clases de dependencias, dirección hacia atrás, cobertura
  de todas las filas propuestas de §7, destinos de absorciones y preservación de estados.
  El validador instalado sigue avisando de campos históricos fuera de esquema; ninguna
  nota nueva pretende que el plugin ejecute una funcionalidad que no tiene.

Estas notas son requisitos para `10.3`, no una ampliación ya implementada del arnés.

Registrado para que el alcance de 10.3 no crezca hasta volverse irrealizable:

- Decidir si un hallazgo merece ítem (1.1).
- Redactar la `verificacion` (1.6) y el `rollback` (1.7).
- Elegir `modelo` y `esfuerzo`, que dependen de si el trabajo es mecánico o de juicio.
- Crear olas y redactar criterios de salida (1.3).

El reparto que sugieren los dos casos: **la máquina valida, numera, deduplica candidatos y calcula
el estado derivado; la persona decide qué entra y con qué criterio se cierra.** Una herramienta que
intente generar ítems completos desde el documento producirá `verificacion` plausibles y no
comprobables, que es el modo de fallo exacto que el ledger existe para evitar.

## 4. Cómo se actualiza este archivo

En la misma sesión en que se reconcilia, y en el mismo commit que el delta del ledger. Cada regla
nueva cita el caso que la produjo. Una regla sin caso es una opinión.

## 5. Corrección documental contra el plugin real — 2026-09-14

### Contrato utilizado y límites

Leídos `commands/plan-siguiente.md`, `scripts/validar-ledger.py`, el selector de
`scripts/plan-run.sh` y las secciones **Para qué no sirve** / **El ledger** del checkout
`arnes-plan`, `main` **8550859**, **1.17.4**. El lanzador instalado sigue en **1.16.0**:
no se actualizó, no se modificó el plugin ni se invocó inferencia.

El protocolo normal es `/arnes-plan:plan-siguiente`: un ítem por invocación, modelo/esfuerzo
de la ficha, confirmación por más de una hora, criterio literal, máximo tres intentos de
depuración, pausa inmediata si la ficha es ambigua, y resultado más commit al cerrar.
**Esta corrección de datos no ejecuta ese protocolo ni cierra ningún ítem.** Durante su
preparación no se hicieron commits, implementación de producto, migración ni cambios de
runners. El versionado posterior solicitado por el usuario se registra al final de esta sección.

La constitución compartida del proyecto está en [AGENTS.md](../../AGENTS.md); complementa
el protocolo, no sustituye hooks ni el cierre explícito. Su política de modelos Copilot y
procedencia de ejecución no cambia los valores `haiku/sonnet/opus` del ledger ni el
enrutamiento nativo del plugin.

`schema_version` permanece en 1. `bloqueado_por` es un único ID; el selector no lo usa para
elegir sino para advertir/frenar después. Las notas `_` no son guardas. No se afirma que
«formato válido» signifique «aceptación automatizada» ni que un `pendiente` con dependencia
abierta sea ejecutable sin resolverla. Un ID explícito incluso puede anunciar un bloqueado:
eso no autoriza saltarse su reentrada.

### Estados y preservación

| Estado | Antes | Después |
|---|---:|---:|
| `hecho` | 44 | 44 |
| `descartado` | 1 | 1 |
| `bloqueado` | 3 | 25 |
| `pendiente` | 55 | 33 |
| Total | 103 | 103 |

No se añaden ni renumeran ítems; quedan intactos los **44 hechos**, el descarte histórico
`5.a` y las fichas completas `3.5`, `5.g`, `5.h`, incluidos resultados y fechas.
Se conservan las **44 correspondencias** de `_trazabilidad_propuestas`, los **32 incorporados**
y las **14 ampliaciones** del 12 de septiembre. LightRAG/RAG-Anything, relaciones, grafo,
formatos binarios, modalidades, XAML, SQL, identidad y despliegue empresarial permanecen
representados. La falta actual de corpus o autorización **no es descarte**.

Los 22 cambios `pendiente → bloqueado` son:

- Preparación/decisión del instrumento o alcance: **11.3, 11.4, 5.d, 10.3**.
- Convergencia local no representable fielmente con una arista: **5.e, 9.5, 13.1,
  13.4, 13.5, 13.6, 15.1**.
- Activación/corpus/decisión aún ausente (algunos también tienen convergencias):
  **12.2, 13.2, 13.7, 15.2, 15.4, 15.5, 15.6, 15.7, 16.1, 16.2, 16.3**.

Cada ficha incluye motivo, responsable funcional y evidencia para su reentrada.
Los 33 pendientes no se han bloqueado solo porque sus implementaciones/test fixtures sean
futuros: cuando el alcance está definido, prepararlos forma parte de la ejecución del ítem.
Si aparece ambigüedad al ejecutarlo, el protocolo exige parar, no inventar el esperado.
En particular, `8.g` permanece pendiente: eliminar o enlazar las claves son alternativas
expresamente admitidas, no una decisión externa que deba bloquearlo. La revisión final
conserva `11.1` pendiente porque su oráculo ya queda definido; construirlo pertenece al
ítem, no a un trabajo invisible fuera del ledger. `11.2` espera `11.1` por su dependencia
ordinaria, sin exigir que su comparador exista antes de iniciar su preparación.
`11.5` espera `11.4`, cuyo cierre ya resuelve la rama de `11.2/11.3`; no necesita otro
desbloqueo manual, inventar un umbral agregado de error ni disponer hoy de sus tests.

### Las 14 dependencias adicionales ya no permiten omitir prerequisitos

No se han creado dependencias artificiales entre trabajos independientes:

| Ficha | Representación efectiva |
|---|---|
| `7.b` | `3.2` ya hecho; el único prerequisito abierto es `5.f`, en `bloqueado_por`. |
| `12.10` | `4.5` ya hecho; el único prerequisito abierto es `7.a`, en `bloqueado_por`. |
| `13.7` | Cadena existente `13.1 → 12.11 → 9.1 → 7.b → 5.f` cubre los adicionales; sigue bloqueado por activación compartida. |
| `5.e`, `9.5`, `12.2`, `13.1`, `13.4`, `13.5`, `13.6`, `15.1`, `15.2`, `16.2`, `16.3` | Estado bloqueado y reentrada que exige **todos** los IDs literales. Las listas siguen siendo documentación; cerrar solo la arista principal no los habilita. |

Además, `7.c` ahora apunta a `7.a`: el perfil que consume es un prerequisito real, mientras
`4.2` sigue hecho y documentado, sin tocar su cierre. Todas las referencias apuntan hacia atrás.
`11.4` permanece bloqueado hasta resolver la alternativa: si `11.2` gana, exige `11.3` hecho
y se cambia su arista a `11.3` antes de habilitarlo; si el resultado es nulo, basta `11.2` hecho.
Nunca se exige cerrar `11.3` artificialmente ni se crea un ciclo con `5.e`.

### Verificación honesta, no 55 comandos aparentes

Se incorpora **un** `verificacion_comando`, en **9.6**: comprobación negativa de referencias
a Qdrant/HNSW en la documentación XML de `Abstractions/` y `Domain/`, seguida de
`dotnet test RagEngine.slnx`, incluidos golden masters. Ambas partes corresponden literalmente
a su criterio; no se usa existencia de archivos para inferir funcionalidad.
La comprobación textual falla hoy por menciones reales y, por `&&`, no ejecuta tests:
la ficha sigue pendiente, no se corrige el código durante esta reconciliación.

En las otras **54 fichas que estaban pendientes** se deja el comando ausente y se documenta
individualmente `_preparacion_de_verificacion`: componente/fixture/oráculo faltante y cómo
prepararlo. Los tres bloqueos anteriores se preservan, sin fabricar comandos para ellos.
En particular:

- **Recall:** `rag eval --baseline` informa comparabilidad, pero no falla por perder aciertos;
  no decide los umbrales de `11.2–11.4`, `5.b/5.e`, `6.a`, `7.a/7.c`, `9.7`, `14.1`
  o las capacidades de Ola 15. Faltan comparadores emparejados, instrumentos y/o anclas.
  `infra/verificar-anclas-eval.py` valida anclas, no ganancia de recall.
- **Calidad semántica:** `infra/quality-baseline.py` termina en 0 y mide heurísticas, no
  «ninguna respuesta empeora». `6.c/6.d`, `8.c`, `9.3`, `12.10`, `13.6`, `15.3`
  y los negativos de modalidades conservan la necesidad de etiquetado/revisión independiente.
- **Contratos locales:** faltan suites específicas de manifiesto/ACL, HTTP/SSE, reanudación,
  métricas/trazas, binding de hosts y migración. Los tests actuales no cubren lo que aún se
  debe implementar. `9.2` no recibe solo `dotnet build`, pues también exige equivalencia de
  cuatro caminos operativos; `9.4` exige cero regeneración de resúmenes, no solo SQLite verde.
- **CI/remoto:** `14.2` requiere observar la annotación/artefacto de controles rojo/aviso en CI.
  Los scripts existentes de `infra/bench/` no deciden toda la aceptación de `16.1–16.3`;
  faltan equipos/entornos autorizados y protocolos. No se modifican ni ejecutan.
- **Documentación/proceso:** `10.2` sigue abierto con nombre `_condicion_de_reingreso`
  compatible; no se toca `3.5`. `10.3` necesita revisiones entrada/salida exactas y oráculo
  de mapeo independiente; un script que afirme su propio JSON no demuestra corrección.

### Preparación prioritaria de 11.1, sin infraestructura nueva

El criterio original no se rebaja. Su nota `_oraculo_de_aceptacion` concreta:

- `M` es el límite efectivo; `S` los especiales verificados y `L=M-S`.
  `T` cuenta tokens reales del texto completo enriquecido **antes de cortar**, sin especiales
  ni padding. Descartados `max(0,T-L)`; truncado si y solo si `T>L`.
- Fixtures C#/TS/prosa con prefijo y hashes/IDs de tokenizador: `T=L-1,L,L+1`
  produce descartados `0,0,1` y truncados `0,0,1`.
- Percentiles **nearest-rank**: elemento `ceil(p*n)` con índice desde 1.
  Una tripleta da `n=3`, `p50=L`, `p95=L+1` y delta `rag_chunks_truncated_total=1`.
  Se advierte cuando `p95>L`, no cuando supera `M`; el caso vacío no inventa percentiles.
- La salida CLI y el contador de una ingesta aislada deben afirmar esos mismos valores,
  sin cambiar cortes, ventana ni `IndexShortTypeDeclarations=false`.
- `11.3` usa el mismo límite: texto y prefijo caben en `L`; al añadir especiales,
  la entrada completa cabe en `M`. No descontar los especiales dos veces.

**Lo inexistente:** `OnnxVectorizationBrain` corta WordPiece antes de publicar el conteo total;
SentencePiece cuenta internamente pero no lo expone. `IngestionSummary` y la tabla final de
`IngestCommand` no tienen esas mediciones. No hay test de límite real/salida que pueda citarse
como comando existente.

**Responsable/entrada:** ejecutor local de `11.1`. La ficha permanece pendiente, con
preparación explícita como primera fase: fixtures con IDs esperados independientes,
captura CLI y `MeterListener` por corrida, antes de implementar la instrumentación.
Registrar el comando cuando exista y demostrar rojo por instrumentación ausente; con
la implementación, comprobar sensibilidad a mutaciones off-by-one, especiales/prefijo
omitidos y aviso contra el límite bruto. Un oráculo definido no es lo mismo que un
harness ya construido: no se bloquea la tarea por faltar lo que debe construir.
Preparar fixtures no cierra `11.1`; implementar y medir sigue pendiente. Si surge una
imposibilidad concreta de establecer los esperados, documentarla como bloqueo sin
ajustar el criterio a la salida. No necesita `5.h` cerrado, IdentityServer, x64,
Postgres ni cómputo remoto.

### Costes y siguiente operativo

Se corrigen ceros que escondían builds/barridos y marcadores de exactamente una hora para
experimentos de varios brazos/modalidades/pilotos. Son **estimaciones provisionales**, no
duraciones medidas ni autorización: deben recalcularse con misses, corpus y repeticiones
reales; más de una hora exige confirmación. `10.2` conserva cero horas de máquina porque
es edición documental, no una ingesta disfrazada. No se normalizan costes/modelos históricos.

El selector real anuncia **`11.1-medir-truncamiento`**, `sonnet/medium`, **0,2 h**,
sin multiagente. Es la selección nativa Claude del ledger; en otro entorno rige la
política de modelos de `AGENTS.md`. Su harness todavía debe construirse como parte
del ítem, sin fingir que hay un comando de cierre disponible hoy.
Se conserva la prioridad `11.1 → 11.2`. El selector lineal puede
advertir después sobre una dependencia ordinaria aún abierta: no es un planificador de
«siguiente ejecutable» y esta reconciliación no pretende convertirlo en uno.

### Comprobaciones de esta corrección

Desde la raíz del proyecto, sin usar el lanzador instalado:

```bash
python3 /Users/DevStudio/Documents/Projects/arnes-plan/scripts/validar-ledger.py \
  docs/analisis-futuro/ejecucion-plan.estado.json
bash /Users/DevStudio/Documents/Projects/arnes-plan/scripts/plan-run.sh --solo-anunciar
```

También se comprueban las 55 fichas modificadas con `--item`, IDs/olas únicos, referencias
literales, dirección y ausencia de ciclos (arista principal y listas documentales),
44 correspondencias vigentes e igualdad del historial preservado. La simulación de cierres
se realiza **en memoria** usando el código real del selector: las fichas condicionales y
de convergencia bloqueadas no se proponen al cerrar solo su prerequisito principal.

El validador conserva el aviso de **53 campos históricos fuera de esquema**; no se renombra
el historial para silenciarlo. No se ejecutan pruebas de producto, builds, benchmarks ni
ingestas por una corrección documental. El único rojo comprobado es la parte textual real
del comando de `9.6`, coherente con que la tarea no está hecha.

### Versionado antes del relevo de sesión — 2026-09-14

Por solicitud del usuario, el trabajo conocido se guarda en esta sesión y por alcance:
experimento `5.h` con su ficha y evidencias (`a737f9f`, permanece bloqueado),
constitución compartida, y reconciliación del ledger con sus fuentes. No se inicia `11.1`.
Las marcas `git_dirty` y `cambios_sin_commit` de los artefactos de septiembre 12 describen
el momento de la medición: no se reescriben al guardar los commits.

`infra/bench/` es un prototipo local de la investigación anterior, no parte de esos
cambios ni del próximo ítem. Se conserva sin versionar, modificar o borrar; su inventario,
límites y precaución con `--out`/`--no-bench` quedan en `16.1`.
Los registros de máquinas y `.DS_Store` ya están ignorados. Esta excepción explícita evita
que la siguiente sesión deba decidir a ciegas si incorpora esos archivos a `11.1`.
