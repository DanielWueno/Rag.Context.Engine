# Del plan al ledger: requisitos derivados de las reconciliaciones reales

> Insumo del ítem `10.3-plan-a-ledger-con-computo-local`. Registra qué decisiones exige convertir un
> documento de análisis en ítems del ledger, con el caso que las produjo. **Se escribe durante cada
> reconciliación, no después:** lo que se pierde al reconstruirlo es exactamente lo que costó decidir.

## Casos base

| Fecha | Documento de origen | Resultado |
|---|---|---|
| 2026-08-24 | [plan-refinamiento-arquitectonico-produccion.md](plan-refinamiento-arquitectonico-produccion.md) | Olas 5 a 8 y el ítem 1.15 (17 ítems desde 570 líneas) |
| 2026-08-31 | [arquitectura-puertos-y-adaptadores.md](arquitectura-puertos-y-adaptadores.md) | Olas 9 y 10, más los ítems 4.9 y 8.g (11 ítems desde 371 líneas) |

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

## 3. Lo que no se debe automatizar

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
