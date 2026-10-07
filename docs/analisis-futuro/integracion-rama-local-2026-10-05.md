# Integración de la rama local `feat/rag-api-selector-coleccion` (5cc44a5) en main

Fecha: 2026-10-05. Estado: **preparación**; nada implementado. Ficha en el ledger:
`10.6-integrar-rama-local-en-main` (sombrilla) y sus unidades `10.6.1` a `10.6.4`.

Este documento es procedencia: inventario, evidencia y decisiones. El avance vive en
`docs/analisis-futuro/ejecucion-plan.estado.json`.

## 1. Situación de partida

| Ref | Commit | Nota |
|---|---|---|
| `feat/rag-api-selector-coleccion` (local) | `5cc44a5` | Referencia íntegra; no se modifica. Copia: rama local `respaldo/local-5cc44a5`. |
| `origin/main` | `6930081` | Merge del PR #1 (`5898c89`), historial reescrito el 2026-09-23. |
| `origin/feat/rag-api-selector-coleccion` | `5898c89` | Versión reescrita; ya contenida en main. |
| `docs/architectural-design` (local) | `a5ca2dd` | Sus 15 commits "divergentes" son originales pre-reescritura con árbol idéntico al gemelo de main: **sin trabajo propio**. |

`git fetch --prune origin` del 2026-10-05 no trajo cambios: las refs remotas ya estaban al día.

## 2. Contraste con el mapa de hashes (`mapa-de-hashes-2026-09-23.tsv`)

Comprobado commit a commit, no solo por asunto:

- La base común según git es `c075a4e`, pero es engañosa. De los 155 commits locales posteriores,
  **143 figuran en el mapa** y en los 143 el árbol del original es **idéntico** al del reescrito
  (0 diferencias), y todos los reescritos son ancestros de `origin/main`.
- Punto real de bifurcación: local `25503b7` ≡ main `d800129`, con el mismo árbol `ff761d8`.
- Después de ese punto, main tiene **65 commits** propios y local tiene **12** que el mapa no recoge.
- Consecuencia: un `git merge` de la rama local reintroduciría en main los 143 originales, con
  sus `Co-Authored-By`, que es justo lo que la reescritura eliminó. **La integración debe ser un porte**
  (cherry-pick o reaplicación) sobre una rama creada desde `origin/main`, nunca un merge de la rama
  local ni un push a `origin/feat/rag-api-selector-coleccion`, que exigiría forzar.
- `git merge-tree --write-tree --merge-base=d800129 origin/main 5cc44a5` (simulación exacta) produce
  conflictos de contenido en `ejecucion-plan.estado.json`, `infra/.env.example` (add/add),
  `infra/docker-compose.yml`, `src/RagEngine.Api/Program.cs`, `src/RagEngine.Cli/Commands/DoctorCommand.cs`
  y `src/RagEngine.Core/Extensions/GenerationServiceExtensions.cs`. Lo demás se auto-fusiona, pero
  con fallos semánticos ocultos (§4).

## 3. Inventario de los 12 commits locales

| Local | Contenido | Veredicto | Unidad |
|---|---|---|---|
| `b66d0fb` | 11.4: ratifica el control post-5.h | **Ya existe en main** (`f34c3f1`, con `docs/eval/quality/11.4/verify.py`). Las conclusiones coinciden; el doc fusionado duplica secciones y contradice a 5.e. Se conserva solo como resultado histórico; no se porta el doc. | 10.6.1 |
| `cee6bd4` | Reconcilia la ola 17 (consumo de Servidor.IA) y la ola 18 (dev/QA x64), y reabre 3.5 | **Requiere adaptación**: los IDs chocan con las olas 17/18/19 de main. Hay que renumerar (§5). | 10.6.1 |
| `04b0a30` | 3.5: binario ONNX x64 verificado en DSOFT03 | **Integrar**: cumple la condición de reentrada que fijó 10.2 en main. `SelectArchitectureBinary` no cambió en main (solo 8.g tocó `RagEnginePaths.cs`). | 10.6.1 |
| `24fcdd4` | 18.1 local: compose dev/QA, Dockerfile no root, `infra/qa/*` | **Requiere adaptación**: contradice 12.8 (loopback por defecto, publicación explícita) y le falta la clave de Qdrant de 12.1. Main fuerza ONNX `_qint8_arm64` en el compose, contra 3.5. | 10.6.3 |
| `595340a` | 18.2 local: colecciones en x64 y su evidencia | **Evidencia histórica, se porta íntegra**. Su baseline es de contrato 2, ya superado por 5.e (contrato v3), 8.f y 5.d de main, así que no es baseline vigente. Se registra una nueva medición. | 10.6.3 |
| `f1dc96b` | 17.1 local: perfil servidor de Ollama, TLS con CA propia, abort en 401/403 | **Requiere adaptación**; main no trae nada equivalente (12.8 es TLS de entrada, no de salida hacia Ollama). §4. | 10.6.2 |
| `0b57f01` | 11.5 local: calibración contra SentencePiece de MiniLM y `EstimateConservative` | **Ya existe en main** (`6973596`, cl100k, solo tests y docs). Fusionar dejaría dos calibraciones con conclusiones opuestas y cambiaría `rag search` (unos 2,2 veces menos fragmentos). Se conserva como resultado histórico; no se porta el código de producción. | 10.6.1 |
| `5e82592` | `docs/referencias-tecnologicas.md`, 10.4 | **Integrar tal cual**, en la versión de este commit y sin la línea `Co-Authored-By`. | 10.6.1 |
| `7d5ce88` | Propuesta de conversaciones v3 | **Diferido** al PR de conversaciones/ContextForge. | — |
| `2c7323c` | 11.6 y `.gitattributes` de fixtures | 11.6 **requiere adaptación**: se re-apunta a la calibración de main. Quedan abiertos el tokenizador de generación (Qwen) y el manifiesto de la muestra. `.gitattributes` no se porta porque los fixtures locales tampoco. | 10.6.1 |
| `55b9d87` | ContextForge y MCP (10.5, ola 20 local) | **Diferido**. | — |
| `5cc44a5` | Conversaciones, borrado lógico (12.12/12.13) y notas en 8.e, 8.f, 12.1, 12.2, 12.9, 13.7 y 16.2 | **Diferido**. | — |

Los diferidos se reconcilian después, contra el código ya integrado, en un PR separado. Siguen
disponibles en `5cc44a5`, en `respaldo/local-5cc44a5` y en el bundle de 10.6.1.

Nota: `5cc44a5`, `55b9d87` y `5e82592` llevan `Co-Authored-By`. Ningún commit portado puede llevarlo.

## 4. Adaptaciones técnicas de 17.1 que la fusión textual no muestra

1. **No compila.** El generador de resúmenes se movió en main a `Core/Infrastructure/Summary/`, y el
   auto-merge deja allí una llamada a `OllamaHttpClientFactory` sin `using`.
2. **Viola 9.5.** Añadir `using RagEngine.Core.Extensions` rompe la regla
   `Ninguna_capa_de_Core_depende_de_Extensions`. Hay que mover la fábrica a `Core/Infrastructure/`
   (solo usa `System.*`).
3. **9.8.** `OllamaOptions` vive en `Core/Domain/`, así que `ApiKey` y `CaCertificatePath` van allí.
   Los tests de 17.1 y el `cref` de `IBusinessSummaryGenerator` necesitan los namespaces nuevos.
4. **9.2.** `DoctorCommand` recibe `IVectorStoreAdmin`, no `QdrantClient`. El chequeo de perfil
   Ollama se integra sobre esa firma.
5. **12.1/12.9 en `Program.cs`.** Mismo punto de inserción. Conviene convertir `Validate` en
   `IValidateOptions<OllamaOptions>` + `ValidateOnStart`, el patrón de 12.8.
6. **8.e.** Los pipelines Polly de chat y de `ollama-summary` manejan `Exception`. Un 401/403 se
   reintentaría y contaría para el circuit breaker antes de abortar. Para conservar la semántica de
   17.1 hay que excluir 401/403 del manejo.

## 5. Ledger: colisiones y numeración

| Ola | En main | En local | Decisión |
|---|---|---|---|
| 17 | Capacidades verificadas (17.1-auditoria…) | Consumo de Servidor.IA (17.1–17.3) | Main conserva la 17. La local pasa a **ola 20**: 17.1 → 20.1, 17.3 → 20.2. |
| 18 | Motor frente al servidor de IA (18.1-contexto-efectivo…) | Dev/QA x64 (18.1–18.4) | Main conserva la 18. La local pasa a **ola 21**: 18.x → 21.x. **17.2 local se absorbe en 18.1 de main**: es la misma preocupación; quedan por conciliar `n_ctx` 4096 frente a 8192/32768 y la muestra de la fase de resumen. |
| 19 | Traslado (19.1–19.6) | Conversaciones (19.1–19.10) | Main conserva la 19. Conversaciones queda **reservada como ola 22**, en el PR diferido. |
| 20 | — | ContextForge (20.1–20.10) | **Reservada como ola 23**, en el PR diferido. |

Otros puntos:

- **Sin colisión de ID:** 10.4 (se integra); 10.5, 12.12 y 12.13 (se difieren con su PR); 11.6 (se integra re-apuntado).
- **11.4 y 11.5**, ejecutados en ambos lados: vale el `resultado` de main, que tiene verificación reproducible. El local se conserva íntegro en una nota `_resultado_paralelo_local_2026_10` con su hash original. No se reescribe ningún resultado de ninguno de los dos lados.
- **3.5:** pasa a `hecho` con el resultado local. La nota de reentrada que main dejó dentro de 10.2 (su `resultado` dice que está en 3.5) se cita en 3.5 sin borrarla de 10.2.
- **Hashes:** las citas locales a hashes pre-reescritura (`bc746bf` en 18.2, `fb34424` en 11.4) se traducen con el mapa existente en una nota, sin reescribir la evidencia. Las citas a los 12 commits locales (`f1dc96b`, `0b57f01`, `24fcdd4` en `docs/eval/18.2/micro-repo.x64.eval.json`…) quedan resueltas con un mapa adicional `mapa-de-hashes-integracion-2026-10.tsv` y con el bundle.

## 6. Hallazgos registrados sin ampliar el alcance

- El `eval_set_hash` de la baseline 5.e de main (`2ae380bb02df`) coincide con el de la corrida x64
  de 18.2, lo que debilita la explicación de 18.2 (autocrlf). Se revisa en la nueva medición x64
  (21.5).
- El `_riesgo_aceptado` de 18.1 local ("el log guarda preguntas y respuestas completas") puede
  haber quedado obsoleto tras 12.9. Se revisa en 10.6.3.
- Con 13.3, `/metrics` se declara de scrape local. Publicar la API en la LAN también lo expondría.
  Lo cubre la decisión de 10.6.3.
- La tabla de no-regresión de 4 eval-sets del 11.4 local es un aporte propio, con n desfasados. No se
  porta; queda citada en la nota histórica.
