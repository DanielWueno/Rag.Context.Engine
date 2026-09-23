# Modo Simple — de "ocultar identificadores" a "explicar el concepto"

## Contexto

Fase 1 (filtro determinístico) y Fase 2 (swap de contexto a resumen cacheado) del plan
`modo-respuesta-simple-codigo.md` ya están en producción. Un fix posterior (commit `39e674f`)
cambió el sanitizer para **humanizar** identificadores en vez de borrarlos con
`[detalle técnico]`: `ProvisionaFactura` → "provisiona factura",
`CFDCompraViewController` → "cfd compra view controller".

Eso resolvió el "muro" visual, pero **no es una explicación semántica**: decodifica el
NOMBRE del identificador, no dice qué SIGNIFICA para el negocio. "provisiona factura" sigue
siendo jerga; un usuario de soporte no sabe qué es "provisionar". El objetivo real del modo
Simple —"explícaselo a alguien que nunca vio el código"— todavía no se cumple del todo cuando
el identificador porta el significado.

## Distinción clave: tres modos de describir el mismo código (no confundir dos ejes)

Antes de hablar de la raíz hay que separar dos ejes que es fácil mezclar. Tomando una propiedad
real como ejemplo:

```
public bool IsCancelable => Status != Cancelado && Status != Finalizado;
```

Hay **tres** formas distintas de describirla:

1. **Explicar la sintaxis** — *"Es una propiedad calculada que usa una expresión lambda `=>`
   para comparar el enum Status contra dos valores con el operador `!=`."* Esto es **más**
   técnico, no menos. El prompt de ingesta **ya lo prohíbe, y hace bien** — permitirlo llenaría
   los resúmenes de "foreach", "lambda", "método async", jerga de lenguaje. **No es la causa del
   problema.**
2. **Nombrar el identificador** — *"**IsCancelable:** valida si se permite cancelar…"* Aquí está
   la fuga. El prompt pide explicar el comportamiento y lo hace, pero **además** deja caer el
   nombre crudo como etiqueta, porque **nada se lo prohíbe**.
3. **Explicar el concepto** (la meta del modo Simple) — *"Una programación de pago solo se puede
   cancelar mientras no esté ya cancelada ni finalizada."* Sin identificador, sin sintaxis, puro
   significado de negocio.

El prompt de hoy ya pide el modo 3 ("en el lenguaje que usaría alguien que jamás vio código") y
prohíbe el modo 1 — pero **no prohíbe el modo 2**, así que el modelo hace 3 y 2 a la vez:
explica *y* encima nombra. Son **dos ejes independientes**:
- Eje sintaxis-vs-comportamiento: el prompt ya elige "comportamiento" (correcto, conservar).
- Eje nombrar-vs-traducir: el prompt está en silencio → ocurre la fuga.

**Corolario para el diseño:** el fix de raíz (Opción A) NO es quitar la prohibición de sintaxis
—eso empeoraría las cosas moviéndonos hacia el modo 1—. Es **agregar** una regla en el eje 2
("nunca nombres el identificador crudo; si el nombre porta el significado, tradúcelo") mientras
se **mantiene intacta** la del eje 1. Así el resumen nace ya en modo 3.

## Raíz del problema (verificada en código, no inferida)

El identificador llega a la salida por **dos rutas**, y las defensas actuales solo cubren una:

1. **Origen — el resumen de ingesta ya trae identificadores.**
   `OllamaBusinessSummaryGenerator.SystemPrompt`
   (`src/RagEngine.Core/Services/Summary/OllamaBusinessSummaryGenerator.cs:45-62`) instruye
   "no expliques sintaxis, nombres de frameworks ni construcciones del lenguaje" — pero
   **nunca prohíbe nombrar identificadores** (métodos, campos, propiedades). Verificado en
   vivo: resúmenes cacheados de `bsuite-repo` contienen literalmente
   `` **IsCancelable:** Valida si... ``, `` **DeshabilitaEdicion:** ... ``,
   `` `BeforeEstatusChange` verifica... ``. Fase 2 inyecta ese resumen como contexto tal cual,
   así que el modelo de generación ve el identificador y tiende a repetirlo.

2. **Superficie de fuga secundaria — `sources[].resumen`.**
   `BuildSourcesAsync` → `SourceDto.Redacted(r, hit.Summary)`
   (`src/RagEngine.Api/Contracts.cs`) solo aplica `StripEntityPrefix` (quita el prefijo
   `Entidad.cs:`). El **cuerpo** del resumen se muestra crudo al usuario Simple, con sus
   identificadores intactos. El sanitizer (`SanitizeSimpleAnswer`) actúa **solo sobre el texto
   de la respuesta**, nunca sobre este campo. Es decir: aunque la respuesta salga perfecta, el
   panel de fuentes debajo sigue mostrando `IsCancelable`, `DeshabilitaEdicion`, etc.

Defensas actuales y su límite:
- Regla 3 de `SimpleSystemPromptTemplate`: instrucción blanda; el modelo la desobedece cuando
  el resumen ya le pone el identificador enfrente (por eso este documento existe).
- `SanitizeSimpleAnswer` + `HumanizeIdentifier`: determinístico, pero solo decodifica el nombre
  (sin semántica) y solo sobre el *answer*, no sobre `sources[].resumen`.
- `StripEntityPrefix`: solo el prefijo del resumen, no su cuerpo.

## Datos que acotan el diseño

- **Latencia Simple hoy** (bsuite-repo, n=275 en logs): mediana 9.2s, p90 16.6s, max 30s.
  El modo Simple ya NO streamea (se buferea para poder sanitizar). Cualquier segunda pasada LLM
  cae encima de esto.
- **Cobertura de `--con-resumen` es desigual** (medido en Fase 2): bsuite-repo/
  bsuite-auditorias-test 100%; wiki-solis/rag-engine/innovapp-docs 0%. Las de 0% alimentan
  código CRUDO como contexto (no resumen) — cualquier fix que dependa del resumen no las cubre;
  para ellas el único filtro es el sanitizer de query-time.
- **Memoria de proyecto**: el cuello de botella del sistema es la GENERACIÓN, no el retrieval
  (`rag-engine-rerank-vs-generacion`). Duplicar generación es el costo más caro que se puede
  pagar.

## Opciones

| # | Opción | Explicación semántica real | Cubre `sources[].resumen` | Cubre colecciones sin resumen | Costo query-time | Re-ingesta | Riesgo |
|---|---|---|---|---|---|---|---|
| A | **Limpiar en origen**: agregar en `OllamaBusinessSummaryGenerator.SystemPrompt` la regla del **eje 2** (nunca nombrar el identificador crudo; traducir el nombre a concepto — "provisionar" → "registrar la factura como ya cubierta/pagada"), **conservando** la regla del eje 1 (no explicar sintaxis). Lleva el resumen del modo 2+3 al modo 3 puro. | Sí (el resumen ya nace en modo 3) | Sí (el resumen limpio se muestra tal cual) | **No** (esas no tienen resumen) | Cero | **Sí, completa** — cambiar el SystemPrompt cambia `ComputePromptVersion` → invalida TODA la caché → regenerar (~horas por colección con `--con-resumen`) | El LLM resumiendo código puede seguir colándose; hay que verificar sobre corpus, no confiar |
| B | **Segunda pasada de reescritura** (lo que se mencionó): tras generar, una segunda llamada reescribe la respuesta reemplazando identificadores por explicaciones, sin código en su contexto. | Sí (si se acota bien) | **No** (solo reescribe el answer) | Sí | ~2x (p90 16.6s → ~33s) | No | La 2ª pasada puede fabricar/derivar; más complejidad; no toca el panel de fuentes |
| C | **Glosario por-identificador cacheado**: micro-llamada LLM que explica cada identificador único ("¿qué es `Provisionada` en negocio?"), cacheada por identificador. | Parcial (el identificador aislado pierde contexto) | Sí (se puede aplicar a ambos) | Sí | Amortizado (hit de caché tras 1ª vez) | No | El identificador solo, sin su contexto, se explica mal; complejidad de caché nueva |
| D | **No hacer más** — dejar el humanizador de `39e674f` como piso. | No | No | (piso actual) | Cero | No | Ninguno nuevo; pero no cumple el objetivo del modo Simple |

## Enfoque recomendado (por fases, condicionado a medición)

**Recomendación: A como fix primario, con el humanizador (D) permaneciendo como red de seguridad;
B/C solo si A resulta insuficiente medido sobre corpus.** Razones:
- A ataca la raíz y de paso limpia `sources[].resumen` (la fuga secundaria que B/C no tocan sin
  trabajo extra).
- A no agrega latencia al modo ya-más-lento, sobre el cuello de botella conocido (generación).
- El costo de A es una re-ingesta one-time, no un impuesto por-request permanente.
- Contra A: no cubre colecciones sin `--con-resumen`. Pero para esas, el sanitizer de query-time
  (humanizador) ya es el piso, y son colecciones de menor uso hoy.

### Fase A0 — Reforzar el prompt de resumen y medir en 1 colección barata primero
- Editar `SystemPrompt` en `OllamaBusinessSummaryGenerator`: **agregar** la regla del eje 2
  ("nunca nombres métodos/campos/propiedades/clases; si el nombre porta el significado,
  tradúcelo a lenguaje de negocio — ej. un método `ProvisionaFactura` → 'registrar la factura
  como ya cubierta'"), **sin tocar** la regla del eje 1 ("no expliques sintaxis") que ya está y
  debe quedarse — quitarla movería el resumen hacia el modo 1 (más técnico), justo lo contrario
  del objetivo. Añadir 1-2 ejemplos BAD/GOOD concretos que contrasten modo 2 (malo) vs modo 3
  (bueno), igual que se hizo con el prompt de generación.
- `PromptVersion` sube (o el hash del SystemPrompt cambia solo) → caché invalidada por diseño.
- **NO re-ingestar bsuite-repo (22.6k chunks, ~horas) todavía.** Primero re-ingestar UNA
  colección chica con resumen (`bsuite-auditorias-test`, ~1.3k chunks) y medir:
  - **Criterio cuantitativo**: correr el corpus histórico de esa colección con
    `verify-simple-mode.py` y contar identificadores residuales en `answer` Y en
    `sources[].resumen` (extender el script para revisar ambos campos, hoy solo mira `answer`).
    Objetivo: reducción sustancial vs. el prompt viejo, medida, no anecdótica.
  - **Escepticismo n=1**: no aceptar con "se ve mejor en 2 ejemplos"; exigir el conteo sobre
    todo el histórico de la colección.

### Fase A1 — Extender el sanitizer/API a `sources[].resumen`
- Aplicar el mismo `SanitizeSimpleAnswer` (o al menos `HumanizeIdentifier`) al cuerpo del
  resumen en `SourceDto.Redacted`, no solo el prefijo. Independiente de A0 y sin re-ingesta —
  cierra la fuga secundaria incluso mientras la caché vieja siga vigente.
- Flag de rollback propio (`EnableSimpleModeSourceResumenSanitizer`), patrón `IOptionsMonitor`
  como los de Fase 1/2.

### Fase A2 — Re-ingestar el resto solo si A0 pasó el criterio
- Recién con A0 validado sobre la colección chica, re-ingestar bsuite-repo y las demás con
  `--con-resumen`. Documentar el costo real (memoria: un run completo puede ser horas; el caché
  ya no ayuda porque `prompt_version` cambió → todo es MISS).

**Fuera de alcance salvo que A0-A2 no basten:** la segunda pasada (B) y el glosario (C). Se
reevalúan solo si, tras limpiar el origen, todavía se ve jerga técnica en pruebas reales — mismo
criterio de parking-lot que usó el plan original con B/C.

## Verificación (criterios de "hecho")

1. `dotnet build` limpio; contenedor reconstruido y confirmado con `docker cp` + grep del
   literal nuevo en el DLL desplegado.
2. Extender `verify-simple-mode.py` para marcar identificadores residuales también en
   `sources[].resumen`, no solo en `answer`.
3. **A0 (colección chica)**: conteo de identificadores residuales (answer + sources) antes vs.
   después del prompt nuevo, sobre el histórico COMPLETO de `bsuite-auditorias-test`. Reducción
   medida, no "se ve mejor".
4. **A1**: confirmar que `sources[].resumen` ya no expone identificadores en modo Simple, con el
   flag de rollback revirtiendo a comportamiento previo.
5. **A2**: solo tras A0 aprobado. Re-verificar que bsuite-repo (release) no regresiona en el
   corpus de 55 preguntas ya capturado (`replicate-env/data/questions/bsuite-repo.json`),
   comparando calidad de explicación (jerga vs. lenguaje de negocio), no solo ausencia de código.
6. Confirmar que las colecciones SIN resumen (wiki-solis) siguen dependiendo del humanizador de
   query-time y no empeoran.

## Notas de riesgo

- **A no es gratis en calidad de retrieval**: el resumen también alimenta el 3er vector
  (`dense-resumen`). Cambiar el prompt cambia los resúmenes → cambia esos embeddings → el recall
  de la rama resumen puede moverse. Medir recall@k (runner `rag eval`) sobre un eval-set con
  resumen ANTES de aceptar A2, no solo la limpieza de identificadores. Referencia:
  `rag-engine-3bandas-pesos-calibrados`.
- El identificador a veces ES la respuesta que el usuario técnico quería. El modo Technical
  debe seguir mostrándolo crudo — nada de esto toca ese camino.
