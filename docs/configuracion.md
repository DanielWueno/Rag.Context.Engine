# Configuración

> Referencia de `appsettings.json`, gestión de modelos ONNX y la matriz de "cuándo re-ingestar".

## `src/RagEngine.Cli/appsettings.json`

```jsonc
{
  "OnnxBrain": {
    "ModelPath":  "${RAG_MODELS_DIR}/paraphrase-multilingual-MiniLM-L12-v2/model_qint8_arm64.onnx",
    "VocabPath":  "${RAG_MODELS_DIR}/paraphrase-multilingual-MiniLM-L12-v2/sentencepiece.bpe.model",
    "TokenizerType": "SentencePiece",   // "SentencePiece" (XLM-R) | "WordPiece" (BERT)
    "MaxSequenceLength": 256,           // tokens; el padding real es dinámico por lote
    "BatchSize": 32,                    // chunks por inferencia
    "EmbeddingDimensions": 384          // debe coincidir con el modelo Y con la colección
  },
  "CrossEncoder": {
    "ModelPath": "${RAG_MODELS_DIR}/mmarco-mMiniLMv2-L12-H384-v1/model_qint8_arm64.onnx",
    "VocabPath": "${RAG_MODELS_DIR}/mmarco-mMiniLMv2-L12-H384-v1/sentencepiece.bpe.model",
    "MaxSequenceLength": 512,           // XLM-R admite hasta 512; no hay índice que re-ingestar
    "BatchSize": 8,                     // secuencias más largas que el bi-encoder → batch menor
    "StableGateScore": true             // score del #1 recalculado en lote de 1 (ver abajo)
  },
  "Qdrant":   { "Host": "localhost", "GrpcPort": 6334, "HttpPort": 6333 },
  "Ingestion":{ "MaxConcurrentResumenCalls": 2 },
  "Ollama":   { "Endpoint": "http://localhost:11434/v1", "ModelId": "qwen2.5-coder", "TimeoutSeconds": 120 }
}
```

> `Ingestion:DefaultCollection`/`RepositoryName`/`BatchSize` no existen aquí: son
> `--collection`/`--repo-name`/`--batch-size` de `rag ingest` (`IngestCommand.Settings`),
> no claves que enlace `IngestionOptions`. Ver [ítem 8.g](analisis-futuro/ejecucion-plan.estado.json).

## `config/shared.appsettings.json` — fuente única de `RetrievalFusion` y el gate

Ítem 8.g. `RetrievalFusion` (pesos de fusión de las tres bandas) y los umbrales del
gate de confianza (`RagGeneration:LowConfidenceThreshold`/`HighConfidenceThreshold`)
viven en **un solo archivo**, `config/shared.appsettings.json` en la raíz del repo,
no copiados literalmente en `RagEngine.Api/appsettings.json` y `RagEngine.Cli/appsettings.json`.
Ambos `Program.cs` lo cargan (`RagEnginePaths.InsertSharedConfigSource`) con
precedencia **menor** que su propio `appsettings.json`: recalibrar un peso o un
umbral ahí lo toman los dos hosts sin tocar nada más; un `appsettings.json` de host
puede seguir sobreescribiéndolo puntualmente si hace falta un experimento local.

```jsonc
{
  "RetrievalFusion": { "WeightCodigo": 1.0, "WeightSparse": 1.3, "WeightResumen": 2.5, "RrfK": 60 },
  "RagGeneration": { "LowConfidenceThreshold": 0.05, "HighConfidenceThreshold": 0.60 }
}
```

Ruta resuelta en orden: `RAG_SHARED_CONFIG_DIR` (variable de entorno), luego
`<raíz del repo>/config/shared.appsettings.json` si aparece `RagEngine.slnx` subiendo
desde el binario. Si ninguno aplica (publish fuera del repo sin la variable), el host
arranca igual con los defaults de código en `RetrievalFusionOptions`/`RagGenerationOptions`.

### Campos críticos

| Campo | Regla |
|---|---|
| `TokenizerType` | Debe corresponder a la familia del modelo: `vocab.txt` → `WordPiece`; `sentencepiece.bpe.model` → `SentencePiece`. Un mismatch produce embeddings basura *sin error visible*. |
| `EmbeddingDimensions` | Debe coincidir con el modelo (384 en ambos MiniLM) y con las colecciones ya creadas. |
| `MaxSequenceLength` | Techo de truncamiento. El costo de inferencia escala con la longitud real del lote (padding dinámico), así que subirlo solo afecta a los chunks largos. |
| `StableGateScore` | Con `true`, el score del resultado #1 se recalcula en un lote de tamaño 1 tras el re-rank, y deja de depender del `TopK`. Es el número que lee el gate de confianza, así que cambiarlo obliga a revisar `LowConfidenceThreshold` / `HighConfidenceThreshold`. Ver [Score estable del gate](#score-estable-del-gate-crossencoderstablegatescore). |

## Autorización de lectura HTTP (`Authorization:Mode`)

`src/RagEngine.Api/appsettings.json` declara `"Authorization": { "Mode": "Local" }`.
También se puede fijar `Authorization__Mode=Local|Empresarial` al arrancar el host;
los modos desconocidos se rechazan, nunca se convierten en Local.

- **Local** (default): operador y evals usan un administrador sintético, sin consultar
  identidad ni IDP. Cualquier cliente que alcance esta API obtiene ese acceso:
  reservarlo para un entorno local de confianza, no para una frontera empresarial.
- **Empresarial**: `/api/search`, `/api/ask` y `/api/ask/stream` comprueban el manifiesto
  antes de recuperar contenido, generar texto o abrir SSE. Sin identidad autenticada
  devuelven 403; no hay fallback local ni headers de bypass. Este ítem no instala un
  IDP/JWT: sin un autenticador de confianza que establezca `HttpContext.User`, las
  lecturas empresariales quedan denegadas.

El adaptador acepta una única identidad autenticada: rol `admin` en `ClaimTypes.Role`
o el `RoleClaimType` configurado por el host; claims `scope` repetidos o separados
por espacios; un único valor distinto de `tenant` (opcional). No mezcla identidades
ni acepta tenants ambiguos. Todos los identificadores de permiso son ordinales y
sensibles a mayúsculas. Un administrador lee siempre; otro actor necesita algún
`RequiredScopes` coincidente **y**, si `Tenants` tiene elementos, un tenant permitido.
`Tenants=[]` no restringe tenant, pero sigue exigiendo scope. Sin manifiesto o con
`RequiredScopes=[]`, sólo lee el administrador.

El modo se fija al iniciar; cambiarlo requiere reiniciar, no re-ingestar ni modificar
vectores/manifiestos. Este control cubre las tres rutas HTTP, no convierte el CLI,
el acceso directo a Qdrant ni los endpoints de diagnóstico/listado en una frontera
empresarial. IDP real, auditoría y endurecimiento de despliegue quedan fuera de alcance.

## Cotas y cierre de la superficie HTTP (ítem `12.1-cotas-y-cierre-inmediato`)

### Validación de `TopK`/`MinScore`

`/api/search`, `/api/ask` y `/api/ask/stream` rechazan con **400 ProblemDetails**
antes de tocar Qdrant o el generador:

- `TopK` fuera de `[1, 100]` (incluye `0`, negativos y valores absurdos como `100000`).
- `MinScore` fuera de `[0, 1]` o no finito (`NaN`/`Infinity`).

Los límites son constantes (`MinTopK`/`MaxTopK`) en `Program.cs`, no configurables por
`appsettings` — endurecer el rango es un cambio de código, no de despliegue. Verificado
en `RetrievalParameterValidationHttpHarnessTests` (matriz completa de rechazo/aceptación
en los tres endpoints).

### Rate limiting y cota de concurrencia (`RateLimiting`)

```json
"RateLimiting": {
  "PermitLimit": 30,
  "WindowSeconds": 60,
  "QueueLimit": 0,
  "MaxConcurrentGenerationsPerActor": 2,
  "ConcurrencyQueueLimit": 1
}
```

- **Tasa por ventana** (`PermitLimit`/`WindowSeconds`/`QueueLimit`): se aplica a
  `/api/search`, `/api/ask` y `/api/ask/stream`. La partición es por actor autenticado
  (tenant/nombre) cuando hay identidad, o por IP remota si no la hay — nunca una cota
  global compartida por todos los clientes sin distinción.
- **Concurrencia en vuelo** (`MaxConcurrentGenerationsPerActor`/`ConcurrencyQueueLimit`):
  cota adicional, solo en `/api/ask` y `/api/ask/stream` (invocan a Ollama, que tarda
  segundos). Un actor puede estar dentro de su cupo de tasa y aun así tener demasiadas
  generaciones abiertas al mismo tiempo; esta cota lo evita.
- Al rechazar, la respuesta es **429** y el handler nunca se invoca: cero llamadas a
  retrieval/generación de más. Se implementa con `PartitionedRateLimiter` +
  `AddEndpointFilter` en vez de `AddRateLimiter`/`[EnableRateLimiting]`, porque ese
  middleware solo admite una política nombrada activa por endpoint y reemplaza en vez
  de sumar — no alcanza para aplicar tasa y concurrencia a la vez sobre la misma ruta.

Verificado en `RateLimitingHttpHarnessTests`: 3ª solicitud del mismo actor → 429 con
exactamente 2 invocaciones de retrieval (no 3); dos actores distintos no comparten cuota.

### Qdrant con API key (`Qdrant:ApiKey`)

```json
"Qdrant": { "Host": "localhost", "Port": 6334, "ApiKey": "" }
```

Vacío (default) preserva el comportamiento anterior sin autenticación — para desarrollo
local con Qdrant sin `QDRANT__SERVICE__API_KEY`. Con la clave configurada en ambos lados
(servidor Qdrant y `Qdrant__ApiKey` del host), toda llamada gRPC sin la clave correcta
recibe `Unauthenticated`. No hay revocación ni rotación automática: cambiar la clave
exige reiniciar Qdrant y el host con el nuevo valor en ambos.

### Binding a loopback y rutas en `infra/docker-compose.yml`

El puerto de la API se publica como `127.0.0.1:5080:5080` (antes `5080:5080`, alcanzable
desde cualquier interfaz). Acceder desde otra máquina de la LAN requiere un proxy
explícito o cambiar el binding, no es el default.

Las rutas de host antes fijas a `/Users/...` ahora son variables de entorno con
defaults relativos al repo, documentadas en `infra/.env.example`:

| Variable | Default si no se define | Uso |
|---|---|---|
| `RAG_MODELS_DIR` | `../models` | Modelos ONNX montados en el contenedor |
| `RAG_LOGS_DIR` | `../logs` | Logs de la API |
| `RAG_SUMMARY_CACHE_DIR` | volumen nombrado `rag-summary-cache` | Caché SQLite de resúmenes |
| `RAG_QDRANT_API_KEY` | vacío (sin auth) | Clave de `Qdrant:ApiKey` propagada al contenedor |

Copiar `infra/.env.example` a `infra/.env` (git-ignorado) y ajustar los valores reales
de cada máquina; sin ese archivo, `docker compose config` resuelve a rutas relativas al
repo y no falla. Verificado con `docker compose config`: ningún `/Users/` literal en
`docker-compose.yml`, y `host_ip: 127.0.0.1` en el binding del puerto.

## Perfil de transporte y secretos (ítem `12.8-secretos-y-tls`)

Ningún archivo versionado (`appsettings.json` de `RagEngine.Api`/`RagEngine.Cli`,
`infra/docker-compose.yml`) contiene valores de secretos: `Qdrant:ApiKey` queda vacío
por default (sin auth, comportamiento previo) y se propaga solo vía `RAG_QDRANT_API_KEY`
en el host o `Qdrant__ApiKey` como variable de entorno del proceso — ver [Qdrant con API
key](#qdrant-con-api-key-qdrantapikey) arriba. Rotar la clave es cambiar esa variable y
reiniciar el proceso; no exige recompilar ni tocar el archivo versionado.

### `Transport:Published` — perfil local vs. perfil publicado

```json
"Transport": { "Published": false, "TlsTerminatedUpstream": false }
```

- **`Published: false`** (default): perfil **local**. El proceso asume que solo lo
  alcanza tráfico HTTP en loopback de una máquina de confianza (el binding
  `127.0.0.1:5080` de `infra/docker-compose.yml`, ítem `12.1`) o un túnel/VPN ya cerrado
  corriente arriba. No exige credencial de Qdrant ni TLS — igual que siempre.
- **`Published: true`**: perfil **publicado** (para exponer el host más allá de ese
  loopback de confianza — LAN/VPN interna, nunca internet público sin más capas). Falla
  el arranque (`OptionsValidationException`, mismo mecanismo que `Authorization:Mode`)
  si falta cualquiera de estas dos cosas:
  1. `Qdrant:ApiKey` configurado — sin credencial, cualquiera que alcance el host
     alcanza también Qdrant sin autenticar.
  2. TLS resuelto, por una de dos vías: `Kestrel:Certificates:Default:Path` (Kestrel
     termina TLS en el propio proceso con un certificado) **o**
     `Transport:TlsTerminatedUpstream: true` (un proxy/Ingress externo hace la
     terminación TLS y reenvía HTTP simple a este proceso — la variable es solo un
     reconocimiento explícito; seguir siendo responsabilidad de quien despliega
     configurar ese proxy con un certificado válido).

Este ítem **no** instala un proxy ni un Ingress: documenta el contrato y lo hace fallar
rápido si no se cumple, para que publicar sin TLS/credencial sea un error de arranque,
no un descubrimiento en producción. `RagEngine.Cli` no expone red, así que
`Transport`/`Kestrel` no le aplican.

**Probado empíricamente** con un certificado de desarrollo local (`dotnet dev-certs
https -ep cert.pfx -p <password>`, self-signed, nunca comiteado):

```bash
Transport__Published=true dotnet run --project src/RagEngine.Api   # sin credencial ni TLS → falla el arranque, mensaje accionable

ASPNETCORE_URLS=https://127.0.0.1:5443 \
Kestrel__Certificates__Default__Path=cert.pfx \
Kestrel__Certificates__Default__Password=<password> \
Transport__Published=true \
Qdrant__ApiKey=<clave-real> \
dotnet run --project src/RagEngine.Api   # con ambos configurados → arranca

curl -sk https://127.0.0.1:5443/api/health   # handshake TLS + 200 sobre el certificado de prueba
```

Sin nada de esto (perfil local, default), el arranque no cambia. Ver el runbook con el
ejemplo de proxy con TLS externo en [docs/operaciones.md](operaciones.md#perfil-publicado-y-tls-ítem-128-secretos-y-tls).

## Minimización y retención de logs (ítem `12.9-retencion-del-log`)

```json
{
  "Logging": {
    "EnableQueryContentDiagnostics": false,
    "QueryContentDiagnosticsExpiresAt": null
  },
  "Audit": {
    "RetentionDays": null
  }
}
```

| Clave | Default | Efecto |
|---|---|---|
| `Logging:EnableQueryContentDiagnostics` | `false` | Con `true` **y** una `QueryContentDiagnosticsExpiresAt` futura, el `QueryEvent` de logs operativos incluye pregunta/respuesta/fuentes. Sin caducidad futura, no tiene efecto (se avisa al arrancar). |
| `Logging:QueryContentDiagnosticsExpiresAt` | `null` | Caducidad obligatoria del diagnóstico anterior — ISO-8601 con offset. |
| `Audit:RetentionDays` | `null` | Plazo por defecto (días) para `rag audit purge` sin `--older-than-days`. `null` obliga a pasar el plazo explícitamente. |

Por defecto (sin configurar nada) el `QueryEvent` **nunca** incluye contenido de la consulta, y
`rag audit purge` **nunca** se ejecuta solo — la retención de auditoría es siempre una acción
manual del operador. Detalle completo, ejemplos de `rag audit purge`/`rag audit hold` y el
contrato de retención legal en
[docs/operaciones.md](operaciones.md#retención-y-minimización-de-logs-ítem-129-retencion-del-log).

## Declaraciones cortas (`Ingestion:IndexShortTypeDeclarations`)

Experimento local de 5.h, **desactivado por defecto**. Con `true`, una declaración
de tipo de menos de 60 caracteres se indexa si su `ClassName` aparece en sus
`DefinedSymbols`. Los grupos de campos, métodos, propiedades y constructores
cortos siguen filtrados; los chunks vacíos nunca se admiten.

El A/B local de dos bandas mejoró la cobertura de tipos, pero perdió recall@10
(22/47 → 20/47). No habilitarlo en las colecciones de uso normal hasta resolver
esa regresión y medir con tres bandas. No demuestra una regresión de tres bandas.

Para una ingesta experimental en una colección aislada, configura
`Ingestion__IndexShortTypeDeclarations=true` y usa el mismo valor al correr
`rag eval`, que lo guarda en `provenance`. Cambiar el valor no modifica índices
existentes: tanto aplicar como revertir el filtro requiere re-ingestar. Mantén
la caché SQLite experimental separada de la compartida.

## Score estable del gate (`CrossEncoder:StableGateScore`)

El re-rank agrupa los candidatos en lotes y hace padding dinámico al máximo real de cada lote. Sobre
un modelo cuantizado a int8 eso hace que el score de un par dependa de sus vecinos de lote, y como el
pool de candidatos es `3 × TopK`, el mismo par puntúa distinto según el `TopK` pedido. El gate de
confianza lee justamente ese número (el score del resultado #1), así que un umbral absoluto calibrado
sobre él no se sostiene.

Con `StableGateScore: true` el ganador se vuelve a puntuar **solo**, en un lote de tamaño 1: `seqLen`
pasa a ser la longitud del propio par y el score queda como función únicamente de (consulta, chunk).

| | Rango del score del #1 al variar `TopK` ∈ {3,5,8,10,15,20} |
|---|---|
| `false` (default) | mediana 0,023 · máximo 0,147 · 19 de 22 consultas por encima de ±0,001 |
| `true` | 0,000000 en las 22 consultas — bit a bit idéntico |

**Coste:** una inferencia extra por consulta, 15 ms de mediana (máximo 33 ms) — por debajo del ruido
de corrida a corrida del propio re-rank, que sobre un pool de 30 tarda ~1.090 ms.

**Qué NO cambia:** el orden de los resultados. Lo sigue decidiendo la pasada por lotes; sólo se
sustituye el número de la posición #1, que puede quedar por debajo del score de la posición #2.

**Encendida desde el ítem 4.3**, que recalibró las bandas sobre el score estable con un conjunto
etiquetado (`docs/eval/gate-bandas.labeled-set.json`) y midió que `LowConfidenceThreshold` = 0,05 y
`HighConfidenceThreshold` = 0,60 siguen siendo los mejores valores del barrido con ese score. Para
apagarla, quitar la clave del `appsettings.json`: el default del tipo es `false` y no hay que
recompilar. Medición completa en
[gate-de-confianza-score-inestable-y-fuga-de-prompt.md](analisis-futuro/gate-de-confianza-score-inestable-y-fuga-de-prompt.md).

## Rutas y portabilidad

`appsettings.json` está versionado, así que no puede llevar rutas absolutas de una máquina
concreta. Las rutas de modelo admiten `~` y tokens `${VARIABLE}`, y las resuelve
`RagEngine.Core.Utilities.RagEnginePaths`:

| Variable | Default | Qué controla |
|---|---|---|
| `RAG_MODELS_DIR` | `~/models` | Raíz donde viven los modelos ONNX descargados. Una ruta relativa en `ModelPath`/`VocabPath` se ancla aquí, **nunca al directorio de trabajo**. |
| `RAG_LOGS_DIR` | `<raíz del repo>/logs` | Dónde escriben los sinks de Serilog. La raíz se localiza buscando `RagEngine.slnx` hacia arriba desde el binario; si no aparece (publish fuera del repo), cae a `logs/` junto al ejecutable. |
| `RAG_SHARED_CONFIG_DIR` | `<raíz del repo>/config` | Dónde vive `shared.appsettings.json` (`RetrievalFusion` y el gate — ítem 8.g). Si `RagEngine.slnx` no aparece y la variable no está fijada, el host arranca con los defaults de código. |

Un token sin definir se deja literal a propósito: así el error de "modelo no encontrado" muestra
`${RAG_MODELS_DIR}/...` tal cual, en vez de una ruta a medio construir.

Cuidado con `infra/download-model.sh`: descarga a `models/` **relativo al directorio desde el que
lo corres**, que no es necesariamente `$RAG_MODELS_DIR`. Córrelo desde la raíz de modelos, o
apunta `RAG_MODELS_DIR` a donde haya dejado los archivos.

## Modelos disponibles

`infra/download-model.sh` descarga a `models/` (o donde apunte tu config):

| Variante | Comando | Uso |
|---|---|---|
| **Multilingüe** (default) | `bash infra/download-model.sh` | `paraphrase-multilingual-MiniLM-L12-v2` — ES/EN y 50+ idiomas. Incluye `model.onnx` (fp32) y `model_qint8_arm64.onnx` (int8, **recomendado en Apple Silicon**: 2.3× más rápido, calidad casi idéntica) |
| Inglés (legacy) | `bash infra/download-model.sh english` | `all-MiniLM-L6-v2` — ~2× más rápido que el multilingüe, **sin soporte real de español** |
| Re-ranker (opcional) | `bash infra/download-model.sh reranker` | `mmarco-mMiniLMv2-L12-H384-v1` — Cross-Encoder multilingüe para `--rerank`. **No requiere re-ingesta**: solo actúa en tiempo de consulta. Detalle: [busqueda-hibrida.md](busqueda-hibrida.md#re-ranking-cross-encoder--onnxcrossencoderreranker) |

Para cambiar de modelo denso: descarga → apunta `ModelPath`/`VocabPath`/`TokenizerType` → **re-ingesta todas las colecciones**. El re-ranker es independiente de este ciclo — se puede activar/desactivar o cambiar de modelo sin tocar las colecciones ya ingestadas.

## Cuándo re-ingestar

Los vectores y términos almacenados quedan desalineados con las consultas cuando cambia cualquier pieza de la vectorización. Regla práctica:

| Cambio | ¿Re-ingestar? |
|---|---|
| Modelo denso (`ModelPath`, variante, dimensiones) | ✅ Sí, siempre |
| `TokenizerType` / archivo de tokenizador | ✅ Sí |
| Lógica del `SparseTokenizer` (stemming, folding, pesos, stop words) | ✅ Sí |
| Estrategias de chunking | ✅ Sí |
| `MaxSequenceLength`, `BatchSize` (`OnnxBrain`) | ⚠️ Recomendado solo si bajó el primero |
| `min-score`, TopK, opciones de búsqueda | ❌ No — son parámetros de consulta |
| Modelo o configuración de `CrossEncoder` (`--rerank`) | ❌ No — re-puntúa en tiempo de consulta, no toca vectores almacenados |
| Prompt del LLM, Ollama, contexto | ❌ No |
| Contenido editado de un archivo ya ingestado (código o docs) | ⚠️ Basta `ingest` incremental — ver nota abajo |
| Reglas nuevas en `.ragignore` / `.gitignore` sobre rutas YA indexadas | ✅ Sí, con `--force` — ver nota abajo |

```bash
# Re-ingesta incremental: reindexa lo que cambió y borra lo que quedó obsoleto
rag ingest /ruta/al/repo -c mi-coleccion

# Reconstrucción completa (borra y recrea la colección); --yes la hace no interactiva
rag ingest /ruta/al/repo -c mi-coleccion --force --yes
```

> **Editar archivos ya indexados ya no exige `--force`.** El Id de chunk es
> `UUIDv5(rutaAbsoluta:startLine:hashContenido)`, así que una edición que desplaza líneas produce
> Ids nuevos y dejaba los viejos indexados para siempre. Desde `e559d80` la ingesta incremental
> **borra esos puntos obsoletos** al cerrar la Fase 1 (`DeleteSupersededPointsAsync`), y la línea
> de cierre del log reporta cuántos: `Obsoletos borrados: N`.
>
> Dos casos que la limpieza **no** cubre, y que sí necesitan `--force`:
>
> 1. **Archivos borrados del repo.** La limpieza sólo toca archivos que la Fase 1 procesó; uno que
>    ya no existe no se procesa, así que sus puntos sobreviven. Es deliberado: si un archivo falló
>    al leerse o al chunkearse, un fallo transitorio nunca debe borrar datos buenos.
> 2. **Rutas recién excluidas** por `.ragignore` o `.gitignore`. Excluir es dejar de procesar, así
>    que sus puntos quedan igualmente huérfanos.

## Calibración de `min-score`

El default (`0.10`) está calibrado para el modelo multilingüe (relevante ≈ 0.12–0.25, ruido < 0.08 en pares pregunta↔código). Si cambias de modelo denso, **re-calibra**: mide el coseno entre una pregunta representativa y un chunk que sabes relevante vs. uno irrelevante, y fija el umbral entre ambas nubes. La escala completa está en [busqueda-hibrida.md](busqueda-hibrida.md#escala-de-min-score-denso-coseno).
