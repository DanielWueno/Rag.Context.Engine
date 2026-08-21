# Re-ingesta manual — comandos y qué esperar

Escrito el 2026-08-21, después de los cambios de chunking de esa sesión.

## Antes de correr nada: probablemente no hace falta

Se midió el corpus y **la re-ingesta de `bsuite-repo` no es necesaria** por los
cambios de esta sesión:

| Fuente | Archivos | Con CRLF | `.ts`/`.tsx` |
|---|---|---|---|
| `BusinessSuite.Xaf` → `bsuite-repo` | 2.097 | **0** | **0** |
| `docs-bsute-innovapp-plan` → `innovapp-docs` | 15 | 0 | 0 |
| `Business-Suite` → `wiki-solis` | 46 | 0 | 0 |
| `Reyma.TI.Tickets.Microservice` → `micro-repo` | 279 | 1 | 0 |

Los dos cambios de comportamiento del chunker fueron la normalización de finales
de línea y el arreglo del lexer de TypeScript. Sin archivos CRLF y sin archivos
TypeScript, **ninguno mueve un solo chunk** de esas colecciones. La extracción de
`ChunkBuilder` y `ParagraphBudget` quedó verificada byte-idéntica por el golden
master (`tests/RagEngine.Core.Tests/GoldenMaster/chunking.json`).

`micro-repo` era la única con un archivo CRLF y **ya se re-ingestó**.

## Si aun así quieres re-ingestar: no cuesta 19 horas

La estimación de ~19 h venía de suponer que se regeneran los resúmenes vía Ollama,
que es el paso lento. Pero la caché acierta cuando el contenido del chunk y el
`prompt_version` no cambian, y aquí **no cambian**:

- Caché en `~/Library/Application Support/rag-engine/summary-cache.sqlite3`:
  **21.082 entradas**, todas con `prompt_version = 210d3e35`.
- `prompt_version` sigue siendo ese: lo fija `PromptHashesTests`, que falla si el
  prompt de resumen cambia.

Con la caché acertando queda solo embedding ONNX + upsert. Referencia medida:
`micro-repo` son 953 chunks en **8,45 s**, así que los ~23.000 de `bsuite-repo`
deberían rondar **10-20 minutos**.

**Cómo confirmarlo en el primer minuto:** la tabla en vivo ya funciona (antes
mostraba `0/0` todo el tiempo). Mira la fila **Resúmenes**: si el contador de
generados sube despacio, la caché está fallando y sí vas a las horas — corta con
Ctrl+C y averigua por qué antes de seguir.

## Comandos

Todos desde la raíz del repo. Compila primero:

```bash
dotnet build RagEngine.slnx --configuration Release
```

### Respaldo antes de tocar una colección

`--force` borra y reconstruye. Los snapshots de Qdrant viven **dentro** del
contenedor y mueren con él, así que hay que sacarlos al host:

```bash
COL=bsuite-repo
curl -s -X POST "http://localhost:6333/collections/$COL/snapshots"
SNAP=$(docker exec qdrant-local sh -c "ls -t /qdrant/snapshots/$COL/*.snapshot" | head -1)
mkdir -p ~/qdrant_snapshots
docker cp "qdrant-local:$SNAP" ~/qdrant_snapshots/
docker cp "qdrant-local:${SNAP}.checksum" ~/qdrant_snapshots/
# verificar que el respaldo sirve
F=$(ls -t ~/qdrant_snapshots/*.snapshot | head -1)
[ "$(cat "$F.checksum")" = "$(shasum -a 256 "$F" | cut -d' ' -f1)" ] && echo OK || echo "CHECKSUM NO COINCIDE"
```

### Las ingestas

`--yes` confirma el `--force` sin preguntar. Es obligatorio si corres sin terminal
interactiva (nohup, cron, script): sin él Spectre aborta con exit 255.

```bash
# bsuite-repo — la grande, con resúmenes de negocio
dotnet run --project src/RagEngine.Cli --configuration Release -- ingest \
  ~/Documents/Projects/BusinessSuite.Xaf \
  --collection bsuite-repo --repo-name BusinessSuite.Xaf --con-resumen --force --yes

# bsuite-auditorias-test — 3 subcarpetas, la primera con --force
dotnet run --project src/RagEngine.Cli --configuration Release -- ingest \
  ~/Documents/Projects/BusinessSuite.Xaf/src/Modules/REYMA.XAFR1PV.Compras/Auditorias \
  --collection bsuite-auditorias-test --repo-name BusinessSuite.Xaf --con-resumen --force --yes
dotnet run --project src/RagEngine.Cli --configuration Release -- ingest \
  ~/Documents/Projects/BusinessSuite.Xaf/src/Modules/REYMA.XAFR1PV.Compras/Compras/Utils \
  --collection bsuite-auditorias-test --repo-name BusinessSuite.Xaf --con-resumen
dotnet run --project src/RagEngine.Cli --configuration Release -- ingest \
  ~/Documents/Projects/BusinessSuite.Xaf/src/Modules/REYMA.XAFR1PV.Base \
  --collection bsuite-auditorias-test --repo-name BusinessSuite.Xaf --con-resumen

# docs (sin resúmenes)
dotnet run --project src/RagEngine.Cli --configuration Release -- ingest \
  ~/Documents/Projects/docs-bsute-innovapp-plan --collection innovapp-docs --force --yes

dotnet run --project src/RagEngine.Cli --configuration Release -- ingest \
  ~/Documents/Projects/Business-Suite --collection wiki-solis --force --yes

# micro-repo (ya hecha el 2026-08-21)
dotnet run --project src/RagEngine.Cli --configuration Release -- ingest \
  ~/Documents/Projects/Reyma.TI.Tickets.Microservice --collection micro-repo --force --yes
```

Para dejarla corriendo sin ocupar la terminal:

```bash
nohup dotnet run --project src/RagEngine.Cli --configuration Release -- ingest \
  ~/Documents/Projects/BusinessSuite.Xaf \
  --collection bsuite-repo --repo-name BusinessSuite.Xaf --con-resumen --force --yes \
  > ~/ingest-bsuite-repo.log 2>&1 &
tail -f ~/ingest-bsuite-repo.log
```

### Cómo saber si salió bien

El exit code ya no miente: **0** = completa, **3** = terminó pero perdió chunks,
1 = cancelada o error, 2 = faltan modelos.

```bash
echo "exit=$?"   # justo después de la ingesta
curl -s http://localhost:6333/collections/bsuite-repo | python3 -m json.tool | grep points_count
```

En la tabla final, si aparece la fila **Chunks perdidos** la ingesta quedó
incompleta: revisa los `ERROR` del log de esa corrida y re-ejecuta.

### Medir si movió algo

```bash
# recall — compara contra el baseline y avisa si la procedencia no es comparable
dotnet run --project src/RagEngine.Cli --configuration Release -- eval \
  --eval-set docs/eval/bsuite-auditorias.eval-set.json -c bsuite-auditorias-test -k 10 \
  --baseline docs/eval/baselines/bsuite-auditorias.norerank.baseline.json

# calidad de respuesta — contra la línea base capturada antes de la re-ingesta
python3 infra/quality-baseline.py --collection bsuite-repo --limit 20 \
  --force-mode Simple --etiqueta post-reingesta
python3 - <<'PY'
import json
a=json.load(open('docs/eval/quality/linea-base-post-ajustes.json'))['resumen']
b=json.load(open('docs/eval/quality/post-reingesta.json'))['resumen']
for k in ['rechazo_pleno_pct','banda_baja_pct','directo_con_score_bajo_pct','palabras_mediana']:
    print(f"{k:<30} antes={a.get(k)} despues={b.get(k)}")
PY
```

## El contenedor de la API

Ya está corriendo con el código de esta sesión. Para volver a desplegar tras un
cambio:

```bash
cd infra
docker compose build rag-api
docker compose up -d --no-deps rag-api
curl -s http://localhost:5080/api/health
```

**`--no-deps` no es opcional.** El compose define un servicio `qdrant`
(`rag-qdrant`) que no es el que usas — el real es el contenedor standalone
`qdrant-local`. Sin `--no-deps`, compose levantaría el otro y chocaría en los
puertos 6333/6334.
