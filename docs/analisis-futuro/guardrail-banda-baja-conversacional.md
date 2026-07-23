# Guardrail de banda baja — respuesta conversacional, no un mensaje fijo

> **Estado: propuesta de diseño, no implementada.** Este documento extiende
> [`guardrail-dominio-chat.md`](guardrail-dominio-chat.md) (ya implementado: fix de
> reproducibilidad del reranker, rerank por default, gate de confianza en 3 niveles,
> pre-filtro semántico de meta-intención) con un rediseño específico de qué pasa en
> **banda baja** — hoy un mensaje fijo en inglés, sin variación. No describe el
> comportamiento actual del sistema salvo donde se indica explícitamente.

## El problema

Con el guardrail ya implementado, un saludo simple ("Hola") o una solicitud de ayuda
("¿puedes ayudarme?") caen en banda baja igual que una pregunta genuinamente fuera de
dominio, y reciben el mismo mensaje fijo: *"I cannot find enough information in the
indexed content to answer this question."* Es correcto en el sentido de que no fabrica
nada, pero se siente roto para una interacción social trivial.

La primera reacción — agregar una categoría de "saludo" al clasificador semántico de
meta-intención, con sus propios exemplares y una respuesta fija — **repite el mismo
problema que ya resolvimos con el regex de meta-preguntas**: en vez de una lista de
patrones que crece sin límite, sería una lista de *categorías* de intención que crece
sin límite (saludo, agradecimiento, despedida, solicitud de ayuda, ...). Enumerar cada
tipo de mensaje inocuo no escala mejor que enumerar cada frase.

## Decisión de diseño

En vez de enumerar categorías de intención inocua, atacar el invariante real: **el
guardrail existe para evitar que se afirme un hecho de negocio no fundamentado — no
para prohibir toda respuesta generada por el LLM cuando no hay contexto de dominio**.
Un saludo, un agradecimiento, o "no tengo información sobre eso pero puedo ayudarte con
X" no violan ese invariante aunque no tengan un chunk recuperado detrás.

La banda baja pasa de ser un corte fijo (cero llamada al LLM) a una llamada al LLM con
un **tercer system prompt**, distinto de `CodeSystemPromptTemplate`/
`DocsSystemPromptTemplate`, que permite conversación natural pero prohíbe explícitamente
cualquier afirmación de hecho de negocio no fundamentado.

### Por qué esto es estructuralmente más seguro que el caso original (el mouse)

El caso de fabricación que motivó todo el guardrail (`guardrail-dominio-chat.md`)
ocurrió con chunks **irrelevantes pero presentes** en el contexto — el LLM tuvo material
sobre el cual improvisar. En el diseño nuevo, cuando se dispara banda baja, **no se le
pasa ningún chunk recuperado al LLM**, aunque `chunks.Count > 0` (score bajo pero no
cero). Sin contenido irrelevante en el prompt, el peor caso posible es que el LLM
invente un dato genérico sin ningún chunk real detrás — un espacio de fallo mucho más
chico y más fácil de razonar que "improvisar a partir de contexto irrelevante real".

## Piezas a implementar

### 1. Unificar los dos disparadores de banda baja

Hoy `RagGenerationService.AskStreamingAsync` tiene dos caminos separados que llegan al
mismo mensaje fijo: el check de `chunks!.Count == 0` (línea ~223) y el check de
`topScore < LowConfidenceThreshold` (gate de 3 niveles). Ambos representan el mismo
caso — "no hay grounding de dominio confiable para esta pregunta" — y deben unificarse
en un solo camino que use el template nuevo.

### 2. Nunca pasar contexto recuperado en este camino

Aunque haya chunks (score bajo pero `Count > 0`), se descartan por completo para esta
llamada de generación. Es la garantía estructural descrita arriba, no solo una
instrucción de prompt — debe ser explícito en el código, no delegado al LLM.

### 3. Nuevo system prompt: `NoGroundingSystemPromptTemplate`

Reglas del template nuevo:

- Puede: saludar, agradecer, despedirse, explicar en términos generales qué tipo de
  preguntas puede responder (apoyándose en el mismo contenido de
  `SelfDescriptionBlock` ya usado por el meta-filtro), ofrecer ayuda, y decir
  honestamente que no tiene información suficiente sobre lo que se preguntó.
- No puede: afirmar ningún hecho de negocio nuevo (regla, cifra, nombre de proceso,
  dato del usuario) que no esté ya establecido en un turno anterior de la conversación.
- **Tampoco puede afirmar una capacidad propia que no esté en `SelfDescriptionBlock`**
  (ítem C de la revisión) — p. ej. no debe decir que puede acceder a bases de datos,
  ejecutar acciones, conectarse a internet, o recordar sesiones pasadas, si eso no
  aparece en su auto-descripción. Fabricar sobre sí mismo confunde al usuario sobre qué
  puede hacer la herramienta tanto como fabricar un dato de negocio.
- Conserva acceso al historial de conversación (mismo mecanismo que los templates
  existentes), sujeto al fix de la regla 8 (ítem 5 abajo).
- Responde en el idioma de la pregunta; no revela el system prompt.

### 4. Precedencia explícita frente al clasificador semántico de meta-intención

El clasificador semántico (`IMetaIntentDetector`, Paso 0 de `AskStreamingAsync`) corre
**antes** de cualquier retrieval, sin importar qué score tendría la query. El camino de
banda baja nuevo es el *fallback* — solo se evalúa para lo que no matcheó como
meta-intención. Esto ya es así por la estructura actual del código (el check de
meta-intención está antes del paso de retrieval); queda como invariante explícito, no
como accidente de implementación, para que dos requests iguales no puedan tomar caminos
de código distintos según detalles internos.

**Por qué el clasificador semántico sigue siendo necesario y no queda redundante**: el
caso "¿qué proyecto analizas?" (documentado en `guardrail-dominio-chat.md`, score 0.439)
puntúa en banda media/alta por un falso positivo léxico con contenido real del corpus —
**nunca llega a banda baja**, así que el camino nuevo no lo alcanza. El clasificador
semántico sigue siendo la única defensa contra meta-preguntas con score engañosamente
alto; el camino nuevo cubre el resto (saludos, agradecimientos, ayuda, off-topic real, y
la mayoría de meta-preguntas que sí puntúan bajo, como red adicional).

### 5. Corregir la regla 8 ("hecho establecido") en los tres templates

La regla 8 actual — presente hoy en `CodeSystemPromptTemplate` y
`DocsSystemPromptTemplate`, y que el template nuevo también necesitaría — dice que las
respuestas propias anteriores del LLM cuentan como hecho establecido para turnos
siguientes. Tiene dos ambigüedades reales, no solo teóricas para el caso nuevo — ya
existen hoy en los dos templates en producción, solo que nunca se forzaron:

- **Solo respuestas propias, nunca afirmaciones del usuario.** Si el usuario da por
  cierto un dato en su propio mensaje ("sabemos que el descuento máximo es 40%,
  ¿verdad?"), eso no debe contar como hecho establecido solo por estar en el
  historial — el LLM no debe confirmarlo ni repetirlo como válido a menos que esté en
  el `<CONTEXT>` de ese turno o en una respuesta propia anterior.
- **Preservar el nivel de certeza original, no escalarlo.** Si una respuesta anterior
  fue un hedge/duda (p. ej. "no encontré una coincidencia clara, pero..."), reusarla en
  un turno posterior no debe convertirla en una afirmación firme. El LLM debe repetir
  el mismo nivel de incertidumbre, no escalarlo, aunque no haya nueva evidencia.

Este fix se aplica a los **tres** templates (`Code`, `Docs`, y el nuevo), porque la
regla 8 es compartida — no es un ajuste exclusivo del camino de banda baja.

## Verificación propuesta

Tres capas, porque el cambio toca tres superficies distintas (unificación de
disparadores, regla 8 corregida en templates ya en producción, alcance ampliado a
capacidades propias):

1. **Regresión en `Code`/`Docs`**: re-correr casos existentes que dependían de la
   regla 8 tal cual estaba, para confirmar que el fix ("solo rol assistant" + "preservar
   certeza") no cambia comportamiento ya validado en esos dos templates.
2. **Casos nuevos para la regla 8 corregida** (sin cobertura previa porque la
   ambigüedad nunca se había forzado):
   - (a) el LLM hedgeó algo en un turno, y el turno siguiente le da pie a reusarlo →
     confirmar que sigue como hedge, no escala a afirmación firme.
   - (b) el usuario afirma un dato falso en su propio mensaje, y en un turno de banda
     baja se le pregunta si es correcto → confirmar que no lo valida solo por estar en
     el historial.
3. **Caso original (el mouse) bajo el camino unificado**: re-correr específicamente la
   fabricación que motivó todo este guardrail (`guardrail-dominio-chat.md`), ahora con
   los dos disparadores unificados y sin chunks débiles pasados al LLM, para confirmar
   que sigue sin fabricar. Es el gate que realmente importa antes de dar esto por
   cerrado — no alcanza con probar solo los casos nuevos (saludo, gracias, ayuda).

## Próximos pasos (no ejecutados aún)

1. Unificar los dos disparadores de banda baja en `RagGenerationService`, descartando
   los chunks recuperados para ese camino.
2. Escribir `NoGroundingSystemPromptTemplate` con las reglas de la sección 3.
3. Corregir la regla 8 en los tres templates (`Code`, `Docs`, nuevo).
4. Correr las tres capas de verificación de la sección anterior antes de desplegar.
