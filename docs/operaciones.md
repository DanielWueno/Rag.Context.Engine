# Operaciones

> Runbook: infraestructura, observabilidad y resolución de los problemas conocidos.

## Infraestructura

```bash
docker compose -f infra/docker-compose.yml up -d    # Qdrant (gRPC 6334, REST 6333)
curl -s http://localhost:6333/collections           # sanity check REST
ollama serve                                        # requerido solo para `rag ask`
```

`rag doctor` valida todo el stack en un comando (Qdrant, modelo ONNX, tokenizador, disco).

### Caché de resúmenes de negocio (SQLite)

`~/Library/Application Support/rag-engine/summary-cache.sqlite3` guarda los resúmenes generados por
la Fase 2, con clave `content_hash + prompt_version` y **compartida entre todas las colecciones**.
Es lo que convierte una re-ingesta de horas en uno de minutos: si el contenido del chunk no cambió,
no se vuelve a llamar a Ollama.

> ⚠️ **Nunca la escribas desde el host y la leas desde el contenedor a la vez.** El contenedor
> `rag-api` la monta por bind mount y SQLite la abre en modo WAL. El índice WAL vive en memoria
> compartida (`*-shm`) y exige coherencia de `mmap` entre todos los procesos que abren la base;
> VirtioFS/gRPC-FUSE no la garantiza entre el host y la VM de Linux. **Baja `rag-api` antes de
> correr una ingesta con `--con-resumen`.** Ver el runbook de recuperación más abajo.

## Eval nocturno local (ítem 14.1)

El runner ejecuta secuencialmente los **cinco perfiles** de
`infra/eval-nocturno/inventory.json`, con `top_k=10` y `min_score=0.1`.
Requiere Python 3.9+, .NET 10 restaurado, Qdrant local y los dos modelos ONNX
con sus tokenizadores; **no necesita Ollama ni genera resúmenes**.

```bash
# Manual: árbol de trabajo autorizado por el operador; no instala el schedule.
python3 infra/eval-nocturno/runner.py run

# Tras revisar y COMMITTEAR el código, con el checkout limpio:
# instala un LaunchAgent diario a las 03:00, hora local.
python3 infra/eval-nocturno/runner.py install --hour 3 --minute 0
launchctl print "gui/$(id -u)/com.rag-engine.eval-nocturno"
```

`--models-dir` (default `$RAG_MODELS_DIR` o `~/models`) fija la ubicación.
El destino es deliberadamente loopback; el runner manual permite
`--http-port`/`--grpc-port`. La clave opcional `Qdrant__ApiKey` se toma del
entorno del proceso, nunca se guarda en el plist ni en los informes. Un servicio
autenticado requiere que el operador la aprovisione también en el entorno de
launchd; sin ella la alarma será de infraestructura, no un falso recall cero.

**Aislamiento y preflight.** Verifica hashes de datasets/referencias, denominadores,
archivos de modelos, conexión gRPC y colecciones no vacías. Publica una vez el CLI
en el directorio de la corrida, comprueba el JSON completo de cada eval y compara
hashes de todos los puntos/payloads/vectores antes y después. Rechaza configuraciones
`appsettings.*.json` adicionales. La configuración es la versionada del CLI más
`config/shared.appsettings.json`; no hereda overrides arbitrarios del terminal.
Los cambios de esos archivos, de modelos o del índice invalidan la comparación.
No toca colecciones, manifiestos ni WAL compartido: logs, auditoría y eventual
caché del proceso se aíslan dentro de la corrida. Un lock evita dos runners simultáneos.

| Exit | Estado | Acción |
|---|---|---|
| 0 | `ok` | Como máximo una pregunta neta perdida @10 por set; también se listan pérdidas brutas aunque haya ganancias. |
| 1 | `regression` | Más de una pérdida neta en algún set; investigar sus IDs y categorías. Dos pérdidas con n=47 son 4,26 pp, no 2 pp. |
| 2 | `incomparable` | Cambió/falta dataset, configuración, identidad, cobertura o índice; no interpretar como descenso ni éxito de recall. |
| 3 | `infrastructure` | Modelos/Qdrant ausentes, build/comando fallido, timeout o checkout no autorizado. |

En estados mixtos prevalece infraestructura sobre incomparable sobre regresión;
`report.json` conserva el estado de cada perfil. Las alarmas aparecen en stderr
y en el informe, **sin correo ni servicio externo**. Cada ejecución conserva
`logs/eval-nocturno/<UTC>/report.json`, resultados JSON y stdout/stderr por perfil;
el contenido detallado permanece local/ignorado por git. `schedule.stdout` y
`schedule.stderr` están en `~/Library/Application Support/rag-engine/eval-nocturno/`.
Consultar además `last exit code` con `launchctl print`. No hay rotación automática
de estos artefactos: archivar corridas revisadas antes de retirar directorios
individuales, especialmente el `app/` publicado de cada corrida.

**Estado inicial y referencias.** Los cinco perfiles se ejecutan realmente, pero
sus referencias históricas carecen de campos necesarios: la alarma inicial es
`incomparable`. No se han sustituido para ocultarlo. Para habilitar una comparación
futura, revisar explícitamente la procedencia y los resultados nuevos, conservar
la referencia histórica y seleccionar un archivo nuevo con su SHA-256 en el
inventario, en un cambio revisado. No completar retrospectivamente campos que no
se midieron, ni actualizar referencias porque haya una regresión.

**Código de confianza.** El LaunchAgent ejecuta una copia del runner fuera del
checkout, fijada al commit limpio instalado. Rechaza cambios de HEAD/árbol antes
de compilar o ejecutar el CLI. No hace fetch, pull ni checkout, ni ejecuta código
de PR; el CI público sólo ejecuta fixtures sin modelos, corpus ni secretos.
Después de un commit nuevo el job alarma hasta que el operador lo revise y
reinstale. Una máquina dormida no ejecuta a las 03:00; launchd puede ejecutar el
evento al despertar. No se configura wake ni `KeepAlive`.

**Rollback/reinstalación**, sin borrar runner, resultados ni baselines:

```bash
launchctl bootout "gui/$(id -u)/com.rag-engine.eval-nocturno"
# Conservar el plist anterior con otro nombre, fuera de LaunchAgents:
mv ~/Library/LaunchAgents/com.rag-engine.eval-nocturno.plist \
  ~/Library/Application\ Support/rag-engine/eval-nocturno/schedule-anterior-$(date +%Y%m%dT%H%M%S).plist
# Solo para reinstalar, tras revisar el nuevo commit limpio:
python3 infra/eval-nocturno/runner.py install --hour 3 --minute 0
```

## Observabilidad

- **Logs estructurados:** Serilog → `logs/rag-engine-YYYYMMDD.json` (CompactJsonFormatter). La consola solo muestra `Warning+`; el archivo lo tiene todo, incluyendo `CorrelationId` por búsqueda. Fuente de verdad para tiempos de ingesta y conteos:

```bash
grep -h "Ingestion complete" logs/rag-engine-*.json | tail -3
grep -h "Search completed"  logs/rag-engine-*.json | tail -10
```

- **Métricas** (`System.Diagnostics.Metrics`, medidor `Rag.Context.Engine`): `rag_chunks_indexed_total`, `rag_ingestion_errors_total` (etiquetadas por `stage`), `rag_search_latency_ms`, `rag_search_errors_total`, `rag_chunks_truncated_total`, `rag_tokens_discarded_total` — todos con tag `collection` (o `stage`), acotado al conjunto real de colecciones/etapas, nunca a datos de consulta. Exportación configurable vía OpenTelemetry (ítem `13.3-opentelemetry`), ver más abajo. También verificables directo con `dotnet-counters monitor -p <PID> --counters Rag.Context.Engine` ([dotnet-counters](https://github.com/dotnet/diagnostics)), sin depender del exportador.

### Exportador OpenTelemetry (ítem `13.3-opentelemetry`)

Apagado por defecto (`Metrics:Enabled=false`): el `Meter` sigue emitiendo, pero nada lo
recoge — cero endpoints nuevos, cero overhead. Reemplaza el listener/exportador de
solo-log de `3.3-exportador-otel` (`MetricsListener`/`SimpleMetricsExporter`, retirados);
nunca corren los dos a la vez, para no duplicar conteos.

```json
{
  "Metrics": {
    "Enabled": false,
    "Exporter": "Prometheus",
    "OtlpEndpoint": null
  }
}
```

| Clave | Efecto |
|---|---|
| `Metrics:Enabled` | `false` (default): sin exportador. `true`: habilita exactamente UNO de los siguientes. |
| `Metrics:Exporter` | `"Prometheus"` (default): expone `GET /metrics` (scrape) en este mismo host. `"Otlp"`: empuja al colector de `Metrics:OtlpEndpoint`. |
| `Metrics:OtlpEndpoint` | Solo con `Exporter="Otlp"`. Requerido, URI absoluta (p. ej. `http://localhost:4317`); sin él, el host **falla al arrancar** con un mensaje accionable en vez de exportar en silencio a ningún lado. |

`GET /metrics` sigue la misma política de exposición de red que el resto del host — no
es una superficie autenticada aparte, es scrape local: sin `Transport:Published=true`
(ver más abajo) solo se alcanza en loopback, igual que `/api/*`.

Verificado empíricamente al cerrar este ítem: con `Metrics:Enabled=false`, `GET /metrics`
da 404 y el resto del servicio no cambia. Con `Metrics:Enabled=true` y
`Exporter=Prometheus`, tras una búsqueda real el scrape muestra
`rag_search_errors_total{...,collection="..."} 1` con `# TYPE ... counter` correcto, sin
duplicar el conteo del listener retirado. Cubierto por
`MetricsExporterHttpHarnessTests` (fixture: graba directamente sobre el `Meter` estático
compartido con producción y scrapea `/metrics` a través de un host HTTP real —
sin `/api/test-metrics`).

**Rollback**: fijar `Metrics:Enabled=false` (o revertir el commit) restaura el listener
de solo-log anterior (`MetricsListener`/`SimpleMetricsExporter`) sin tocar índices ni
datos — el `Meter` en sí no cambia, solo quién lo escucha.

- **Resiliencia:** las llamadas a Qdrant pasan por Polly (3 reintentos exponenciales + circuit breaker 50%/30s). Los reintentos se loguean con `Execution attempt`.

## Auditoría local (ítem `12.11-auditoria-local-con-actor`)

Registro **durable e inmutable** de consultas (`rag search`, `rag ask`, `/api/search`, `/api/ask`,
`/api/ask/stream`) e ingestas (`rag ingest`), independiente de los logs de Serilog: los logs son
para diagnóstico y se rotan/purgan, la auditoría es el histórico de "quién hizo qué" que no se
reescribe. Un evento se emite exactamente una vez por operación, sea cual sea su desenlace.

### Dónde vive y cómo se configura

```
~/Library/Application Support/rag-engine/audit.sqlite3   # default (SectionName "Audit")
```

| Clave (`appsettings.json` / env `Audit__X`) | Efecto |
|---|---|
| `Audit:DbPath` | Ruta del archivo SQLite. Cambiarla no migra eventos previos. |
| `Audit:ActorId` | Id explícito del operador. Sin configurar, cae en `Environment.UserName` del proceso — **nunca** en un claim de identidad corporativa. |

### Esquema del evento (tabla `audit_events`)

| Campo | Contenido |
|---|---|
| `event_id` (PK) | Único por escritura; reintentar con el mismo id no duplica (`INSERT OR IGNORE`). |
| `correlation_id` | Id de la operación causal (una consulta, una corrida de ingesta). |
| `operation` | `query.search` / `query.ask` / `ingest.repository`. |
| `actor_type` | Siempre `local_operator` (ver más abajo). |
| `actor_id` | Resuelto de `Audit:ActorId` o del usuario del SO. |
| `collection` | Colección afectada; `null` si no aplica. |
| `outcome` | `Success` / `Denied` / `Failed` / `Cancelled`. |
| `detail` | Mensaje corto de diagnóstico (p. ej. `Exception.Message` en `Failed`). **Nunca** contenido de fuentes ni tokens/embeddings. |
| `timestamp`, `version` | ISO-8601 UTC y versión de esquema (1 al cerrar este ítem). |

Consulta directa (no hay comando de CLI todavía — es un registro de auditoría, no una feature de
producto en esta ficha):

```bash
sqlite3 ~/Library/Application\ Support/rag-engine/audit.sqlite3 \
  "SELECT timestamp, operation, outcome, collection, actor_id FROM audit_events ORDER BY timestamp DESC LIMIT 20;"
```

### El actor es siempre `local_operator` — por diseño

Ningún camino de código (API, CLI, ingesta) infiere un `actor_type` distinto a partir del
`RetrievalContext` empresarial del ítem `9.1` (tenant/módulo/scopes). Mientras no exista un IDP
corporativo real y verificable, tratar ese contexto como identidad auditable sería fabricar una
identidad que el sistema nunca autenticó. Un registro anterior a este ítem (o cualquier fila sin
`actor_id`) queda como actor desconocido para siempre — no se rellena retroactivamente.

### Qué NO se audita hoy

- **Ingesta no tiene desenlace `Denied`**: `DefaultIngestionPipeline` no tiene una puerta de
  autorización (el ítem `9.1` es solo de lectura/retrieval); sus eventos son `Success`/`Failed`/`Cancelled`.
- **`rag search` (CLI) no tiene `Cancelled`**: el comando no acepta cancelación hoy; solo emite
  `Success`/`Failed`. Limitación preexistente, no introducida por este ítem.
- Un fallo al **escribir** el evento de auditoría (disco lleno, archivo corrupto) se loguea vía
  Serilog pero **nunca** convierte una operación exitosa en un error de cara al usuario, ni
  viceversa — la escritura de auditoría es best-effort respecto al resultado reportado.

### Rollback

Antes de revertir el escritor de auditoría (código o config), respaldar el archivo completo —
es la única copia del histórico:

```bash
cp ~/Library/Application\ Support/rag-engine/audit.sqlite3 /tmp/audit.sqlite3.bak-$(date +%Y%m%d)
```

Revertir el código (`git revert`) o apagar la escritura no borra eventos ya persistidos; el
archivo sigue siendo legible con `sqlite3` aunque el escritor deje de usarse. Si en el futuro se
habilita un acceso empresarial real y no hay forma de auditarlo con un actor verificado,
**rechazar la operación o aplicar una política explícita — nunca continuar en silencio**.

## Retención y minimización de logs (ítem `12.9-retencion-del-log`)

Dos mecanismos independientes, uno por tipo de registro. Ninguno se activa ni purga nada por sí
solo — ambos requieren una acción/configuración explícita del operador.

### 1. Logs operativos (`rag-api-*.json` / `rag-engine-*.json`) — minimización por defecto

El `QueryEvent` que registra cada consulta **NO incluye por defecto** la pregunta, la respuesta
generada ni las fuentes citadas — sólo metadatos (colección, TopK, MinScore, duración, y
`QueryLength`/`AnswerLength`/`ResultCount` en vez del contenido). Esto es así tanto en el evento de
éxito como en el `LogError` que se emite si `/api/ask/stream` falla a mitad de la generación.

Plazo operativo (independiente de la auditoría): ya existente desde antes de este ítem —
`retainedFileCountLimit` en la configuración de Serilog de cada host (`Program.cs`): 30 días para
la API, 7 días para el CLI. Es una retención por rotación de archivo, no requiere acción manual.

**Diagnóstico local acotado** (opt-in, con caducidad obligatoria y aviso): un operador que necesita
ver preguntas/respuestas reales para depurar un problema puntual lo activa así, en la sección
`Logging` de `appsettings.json`:

```json
{
  "Logging": {
    "EnableQueryContentDiagnostics": true,
    "QueryContentDiagnosticsExpiresAt": "2026-09-20T00:00:00Z"
  }
}
```

- Sin `QueryContentDiagnosticsExpiresAt` (o ya vencida), el diagnóstico **no tiene efecto** — se
  registra un `LogWarning` al arrancar el host avisando que quedó inactivo, para que un
  "se me olvidó apagarlo" no pase inadvertido.
- El contrato de expiración (`RagEngine.Core.Diagnostics.QueryContentDiagnostics`) está cubierto
  por `QueryContentDiagnosticsTests` con reloj sintético: al instante exacto de la caducidad, ya
  está inactivo (borde estricto, a favor de minimizar).
- Activarlo no cambia lo que se **audita** (sección anterior): la auditoría nunca guarda contenido,
  con o sin diagnóstico activo.

### 2. Auditoría (`audit.sqlite3`) — retención diferenciada con retención legal

A diferencia de los logs operativos, la auditoría es historia inmutable y **no se purga
automáticamente al arrancar nada**. La purga es una acción explícita del operador:

```bash
# Ver cuántos eventos serían purgados sin borrar nada:
rag audit purge --dry-run --older-than-days 90

# Purgar de verdad los eventos con Timestamp anterior a (ahora - 90 días):
rag audit purge --older-than-days 90

# Sin --older-than-days, usa Audit:RetentionDays de la configuración; si tampoco
# está configurado, el comando falla en vez de asumir un plazo arbitrario.
```

| Clave | Efecto |
|---|---|
| `Audit:RetentionDays` | Plazo por defecto (en días) para `rag audit purge` sin `--older-than-days`. `null` (default, sin configurar) obliga a pasar el plazo explícitamente cada vez. |

**Retención legal**: un evento marcado con retención legal activa **nunca** se purga, sin importar
cuán vencido esté su plazo operativo.

```bash
rag audit hold evt-1234              # activa la retención legal
rag audit hold evt-1234 --release    # la libera (vuelve a ser elegible para purga)
```

El contrato de purga (`IAuditEventStore.PurgeExpiredAsync`/`SetLegalHoldAsync`) está cubierto por
`AuditEventStoreTests` con timestamps sintéticos fijos (nunca `DateTimeOffset.UtcNow` dentro del
store — el reloj es el parámetro `olderThan` que decide el llamador): se fija el borde exacto del
plazo (`Timestamp == olderThan` se conserva, `Timestamp < olderThan` se purga) y que un evento con
retención legal activa sobrevive aunque esté vencido por años.

**Rollback**: igual que en auditoría — respaldar el archivo `audit.sqlite3` antes de purgar. Lo ya
purgado no se recupera; `rag audit purge --dry-run` existe justamente para ensayar antes de borrar.

```bash
cp ~/Library/Application\ Support/rag-engine/audit.sqlite3 /tmp/audit.sqlite3.bak-antes-de-purgar
```

### La política empresarial real de retención NO está definida aquí

Los plazos de ejemplo (90 días, etc.) son ilustrativos, no una política aprobada. Cuando exista una
política empresarial de retención/compliance real, se configura explícitamente con
`Audit:RetentionDays` y `rag audit purge` — este ítem construye el mecanismo, no inventa el
cumplimiento de una política que todavía no existe.

## Perfil publicado y TLS (ítem `12.8-secretos-y-tls`)

Por default (`Transport:Published=false`) este host es **HTTP puro, loopback**: el
compose de `12.1` publica `127.0.0.1:5080`, no todas las interfaces. Publicarlo más
allá de eso (LAN/VPN interna, nunca internet público directo) exige
`Transport:Published=true`, que a su vez exige credencial de Qdrant y TLS resuelto —
ver [docs/configuracion.md](configuracion.md#transportpublished--perfil-local-vs-perfil-publicado).
El arranque falla con un mensaje accionable si falta cualquiera de las dos.

### Opción A — TLS en el proceso (Kestrel), probado con un certificado local

```bash
# Certificado de desarrollo self-signed, NUNCA comiteado (fuera del repo)
dotnet dev-certs https -ep /tmp/rag-cert.pfx -p <password-local>

ASPNETCORE_URLS=https://127.0.0.1:5443 \
Kestrel__Certificates__Default__Path=/tmp/rag-cert.pfx \
Kestrel__Certificates__Default__Password=<password-local> \
Transport__Published=true \
Qdrant__ApiKey=<clave-real> \
dotnet run --project src/RagEngine.Api

curl -sk https://127.0.0.1:5443/api/health   # -k porque el cert es self-signed y no está confiado por el sistema
```

Verificado empíricamente al cerrar este ítem: sin `Qdrant:ApiKey` ni certificado, el
arranque falla (`OptionsValidationException`); con ambos, arranca y `/api/health`
responde 200 sobre el handshake TLS del certificado de prueba.

### Opción B — TLS en un proxy/Ingress externo (`Transport:TlsTerminatedUpstream=true`)

Si un reverse proxy (nginx, Caddy, el Ingress de un clúster) ya termina TLS y reenvía
HTTP simple a este proceso por loopback o una red interna de confianza, fijar
`Transport:TlsTerminatedUpstream=true` reconoce esa topología explícitamente sin
exigir un certificado dentro de este proceso. Sigue exigiendo `Qdrant:ApiKey`. Ejemplo
mínimo de terminación TLS con Caddy (no incluido en `infra/docker-compose.yml`, es
responsabilidad del entorno que publique):

```
# Caddyfile
rag.ejemplo.interno {
    reverse_proxy 127.0.0.1:5080
}
```

Caddy obtiene/renueva el certificado por su cuenta (o se le apunta uno propio); este
proceso no necesita saber nada de TLS, solo que `Transport:TlsTerminatedUpstream=true`
está reconocido.

### Rotación de `Qdrant:ApiKey`

Cambiar la variable de entorno (`RAG_QDRANT_API_KEY` en `infra/docker-compose.yml`, o
`Qdrant__ApiKey` del proceso) y reiniciar — no exige recompilar. No hay revocación
automática de la clave anterior: coordinar el cambio en Qdrant y en todos los clientes
antes de dar por cerrada la rotación.

## Troubleshooting

### "I cannot find enough information…" en `rag ask`

Grounding estricto: el contexto recuperado no contiene la respuesta, **o el LLM local (7B) no
supo sintetizarla desde el contexto**. En orden:
1. ¿La colección se ingestó con el motor actual? (ver [matriz de re-ingesta](configuracion.md#cuándo-re-ingestar))
2. Sube `-k` (p. ej. 10) y verifica con `rag search ... -o markdown` qué contexto llega realmente. Si el contexto SÍ contiene la respuesta y el modelo se rehúsa, es un límite de síntesis del LLM: reformula hacia lo concreto ("¿qué valor tiene el atributo Persistent de X?" en vez de "¿en qué tabla se guarda X?").
3. Reformula con el [patrón verificado](guia-cli.md#cómo-formular-buenas-preguntas-patrón-verificado-empíricamente): entidades nombradas + relaciones/reglas, en el idioma de los identificadores del corpus.
4. Prueba `--rerank`: si el chunk correcto está en el pool ampliado (3×TopK) pero fuera del
   TopK final por un mal ranking RRF, el Cross-Encoder suele subirlo. Requiere el modelo
   descargado (`bash infra/download-model.sh reranker`) — `rag doctor` avisa si falta.

### 0 resultados en `rag search`

- `--min-score` demasiado alto para la escala real (relevante ≈ 0.12–0.25). Prueba `-s 0`.
- Colección equivocada (`rag status --all`).

### `exit code 134` / `mutex lock failed: Invalid argument` al terminar

Ruido de teardown de ONNX Runtime en Apple Silicon, **posterior** a completar el trabajo (la ingesta/búsqueda ya terminó — verifícalo en el log). Mitigado en `Program.cs` con dispose asíncrono + `Task.Delay(300)`.

### `--rerank` lento o falla con "model not found"

El re-ranker carga su modelo de forma perezosa (`Lazy<T>`) — solo al primer uso de `--rerank`,
no al arrancar la CLI. Si falla con `FileNotFoundException`, descarga el modelo con
`bash infra/download-model.sh reranker` y confirma la ruta en la sección `CrossEncoder` de
`appsettings.json`. La latencia esperada es de cientos de ms a pocos segundos según el TopK
(cada candidato del pool 3×TopK paga una inferencia completa) — medido: pool de 15 candidatos
en ~700ms sobre el modelo int8 ARM64.

### Ingesta lenta

1. Confirma en el log que el lote usa el modelo **int8** (`model_qint8_arm64.onnx`), no fp32 (~2.3× más lento).
2. El costo dominante es ONNX; ver palancas en [pipeline-de-ingesta.md](pipeline-de-ingesta.md#rendimiento-medido).
3. Qdrant con disco lleno o CPU saturada degrada los upserts (aunque `wait:false` los saca de la ruta crítica).

### `SQLite Error 11: 'database disk image is malformed'`

La caché de resúmenes está **corrupta en disco**; no es transitorio y no se cura reiniciando. Toda
petición a `/api/search` y `/api/ask` revienta en `SummaryCache.TryGetAsync`. Causa conocida:
escritor nativo en el host (una ingesta con `--con-resumen`) y lector dentro del contenedor sobre
el mismo archivo en WAL, a través del bind mount de Docker — ver el aviso de la sección
Infraestructura.

Diagnóstico y recuperación (`.recover` rescató 21.082 de ~21.205 filas, 99,4 %):

```bash
D=~/Library/Application\ Support/rag-engine
sqlite3 "$D/summary-cache.sqlite3" "PRAGMA integrity_check;"   # confirma la corrupción

docker stop rag-api                                            # sin lectores durante el swap
cp "$D/summary-cache.sqlite3" /tmp/cache.corrupt.bak            # SIEMPRE respaldar primero
sqlite3 /tmp/cache.corrupt.bak ".recover" > /tmp/recover.sql
rm -f /tmp/rebuilt.sqlite3 && sqlite3 /tmp/rebuilt.sqlite3 < /tmp/recover.sql

sqlite3 /tmp/rebuilt.sqlite3 "PRAGMA integrity_check; SELECT count(*) FROM resumen_cache;"
mv "$D/summary-cache.sqlite3" "$D/summary-cache.sqlite3.corrupt-$(date +%Y%m%d)"
rm -f "$D"/summary-cache.sqlite3-wal "$D"/summary-cache.sqlite3-shm
cp /tmp/rebuilt.sqlite3 "$D/summary-cache.sqlite3"
docker start rag-api
```

Las filas basura que `.recover` arrastra las descarta el `INSERT OR IGNORE` del volcado. Perder
entradas sólo cuesta regenerar esos resúmenes con Ollama en la siguiente ingesta; no hay pérdida
de datos irrecuperable.

### `dense vector must not be empty` e `Indexed: 0`

Todos los lotes rechazados por Qdrant y la colección sin actualizar. Ocurría al **re-ingestar de
forma incremental una colección que ya tenía resúmenes**: Qdrant ≥ 1.14 devuelve el denso en el
oneof `dense` y deja vacío el campo plano legacy `VectorOutput.Data`, que era el que se leía —
daba `float[0]` en vez de `null`, y ese vector vacío se reenviaba en el upsert. Como el upsert es
atómico, se perdía el lote entero, puntos sanos incluidos.

Corregido en `a341e06`. Si vuelves a verlo tras actualizar el cliente o el servidor de Qdrant,
sospecha del mismo patrón en cualquier lectura nueva de vectores. La guarda de "generó chunks pero
no indexó ninguno" hace que hoy la ingesta aborte en vez de reportar éxito.

### Falla la compilación de proyectos NUEVOS fuera del repo

El SDK .NET de la máquina puede tener un workload set corrupto (`dotnet workload repair`). Este repo compila porque `Directory.Build.props` fija `MSBuildEnableWorkloadResolver=false`; copia ese archivo a proyectos auxiliares.

### `--force` en scripts/CI

El prompt de confirmación exige TTY, pero la CLI ya lo detecta y aborta con un mensaje explícito
(`Agrega --yes para confirmar sin preguntar`) en vez de colgarse. En automatización:
`rag ingest ... --force --yes`. Ya no hace falta el rodeo de borrar la colección por REST.

## Checklist de despliegue en una máquina nueva

1. .NET 10 SDK + Docker + (opcional) Ollama con `qwen2.5-coder`.
2. `docker compose -f infra/docker-compose.yml up -d`
3. `bash infra/download-model.sh` y ajustar rutas en `appsettings.json` si difieren.
4. (Opcional) `bash infra/download-model.sh reranker` si vas a usar `--rerank`.
5. `dotnet build && rag doctor`
6. `rag ingest <repo> -c <colección>` y una búsqueda de humo en ambos idiomas.
