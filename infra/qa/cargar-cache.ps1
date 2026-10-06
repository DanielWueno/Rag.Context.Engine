<#
.SYNOPSIS
    Carga una COPIA CONSISTENTE de la caché SQLite de resúmenes de negocio
    (summary-cache.sqlite3) en el volumen nombrado que usa rag-api
    (infra/docker-compose.yml), sin montar jamás el WAL que escribe la CLI
    del host.

.DESCRIPTION
    Ítem 10.6.3 del plan (porte del 21.1 local, punto 4 de su _alcance): "la
    caché que lee la API vive en un volumen nombrado y se carga como copia
    consistente (sqlite3 .backup o VACUUM INTO). NUNCA se monta el WAL que
    escribe la CLI."

    Mecanismo: un contenedor efímero (alpine + sqlite3, instalado al vuelo)
    monta el DIRECTORIO de origen de SOLO LECTURA y usa 'VACUUM INTO' para
    producir un archivo nuevo, autocontenido y consistente — sin más
    conexiones concurrentes que esta lectura puntual. Ese archivo nuevo (no
    el original, nunca sus -wal/-shm) se copia al volumen nombrado que monta
    rag-api en /data/rag-engine.

    Precaución explícita: no corras esto mientras una ingesta
    'rag ingest --con-resumen' esté escribiendo activamente en -SourcePath.
    VACUUM INTO abre el origen solo lectura; si eso falla porque el WAL tiene
    frames pendientes y el contenedor no puede crear/leer *-shm en un
    directorio read-only, el script lo reporta como fallo — no lo convierte
    en éxito silencioso.

.PARAMETER SourcePath
    Ruta en el HOST del summary-cache.sqlite3 a copiar. Default: la ruta que
    usa RagEngine.Core.Domain.IngestionOptions.ResumenCachePath en Windows
    (%LOCALAPPDATA%\rag-engine\summary-cache.sqlite3).

.PARAMETER ComposeFile
    Ruta al docker-compose.yml cuyo volumen 'rag-api-cache' se va a poblar.

.PARAMETER VolumeLabel
    Nombre del volumen DECLARADO en el compose (no el nombre completo que le
    da Docker Compose al proyecto — ese se resuelve por el label
    com.docker.compose.volume que Compose ya le pone).

.EXAMPLE
    pwsh infra/qa/cargar-cache.ps1 -SourcePath "C:\Users\dsoft03.corp\AppData\Local\rag-engine\summary-cache.sqlite3"
#>
[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $env:LOCALAPPDATA "rag-engine\summary-cache.sqlite3"),
    [string]$ComposeFile = (Join-Path $PSScriptRoot "..\docker-compose.yml"),
    [string]$VolumeLabel = "rag-api-cache"
)

$ErrorActionPreference = "Stop"

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

if (-not (Test-Path $SourcePath)) {
    Fail ("No existe '$SourcePath'. No hay nada que cargar todavía (rag-api arranca " +
        "igual sin caché: Resumen=null por chunk). Corre una ingesta con --con-resumen " +
        "en el host primero si quieres poblarla.")
}

try {
    docker version --format '{{.Server.Os}}' | Out-Null
} catch {
    Fail "Docker no responde. No se puede cargar la caché sin el motor de Docker en marcha."
}

$ComposeFile = (Resolve-Path $ComposeFile).Path
Write-Host "Compose file: $ComposeFile"
Write-Host "Origen (host): $SourcePath"

# Resuelve el nombre REAL del volumen por el label que Compose ya aplica — no
# asume el prefijo de proyecto (cambia según desde dónde se invoque compose).
$volumeName = docker volume ls --filter "label=com.docker.compose.volume=$VolumeLabel" --format "{{.Name}}" | Select-Object -First 1
if (-not $volumeName) {
    Fail ("No se encontro un volumen Compose con label com.docker.compose.volume=$VolumeLabel. " +
        "Corre primero: docker compose -f `"$ComposeFile`" up -d (crea los volúmenes declarados).")
}
Write-Host "Volumen destino: $volumeName"

$sourceDir = Split-Path -Parent $SourcePath
$sourceFile = Split-Path -Leaf $SourcePath
$tempName = "cache-copy-$(Get-Date -Format yyyyMMddHHmmss).sqlite3"

# '?immutable=1' en la URI le dice a SQLite que el archivo NO va a cambiar
# durante la conexión: evita el camino normal de WAL, que para abrir —incluso
# solo lectura— necesita poder CREAR el -shm en el mismo directorio. Sin esto,
# VACUUM INTO contra un .sqlite3 en journal_mode=WAL montado :ro falla con
# "unable to open database file" aunque el contenido esté íntegro (confirmado
# al construir este script). Con immutable=1 el mount read-only se respeta de
# verdad: el contenedor jamás intenta escribir nada bajo /src.
# OJO: ${sourceFile} y ${tempName} llevan llaves explícitas a propósito — sin
# ellas, PowerShell 7 lee "$sourceFile?immutable=1" como el operador ternario
# ?: (nuevo desde 7.0) y se come el resto del literal (confirmado al construir
# este script: "$x?world=1" interpola a "=1", no al texto esperado).
$shellScript = "set -e; apk add --no-cache sqlite >/dev/null 2>&1; " +
    "sqlite3 `"file:/src/${sourceFile}?immutable=1`" `"VACUUM INTO '/tmp/${tempName}'`"; " +
    "cp /tmp/${tempName} /dest/summary-cache.sqlite3; " +
    "rm -f /dest/summary-cache.sqlite3-wal /dest/summary-cache.sqlite3-shm; " +
    "chown 1654:1654 /dest/summary-cache.sqlite3; " +
    "echo COPIA_OK"

# Docker en Windows (Docker Desktop) traduce rutas de host con letra de unidad
# a las que entiende el motor Linux de adentro al usar -v; no hace falta
# reescribirlas a mano.
$dockerArgs = @(
    "run", "--rm",
    "-v", "${sourceDir}:/src:ro",
    "-v", "${volumeName}:/dest",
    "alpine:3.20",
    "sh", "-c", $shellScript
)

Write-Host "Ejecutando VACUUM INTO en un contenedor efímero (sin escribir nunca en el origen)..."
$output = & docker @dockerArgs 2>&1
$exitCode = $LASTEXITCODE
$outputText = ($output | Out-String)

Write-Host $outputText

if ($exitCode -ne 0 -or ($outputText -notmatch "COPIA_OK")) {
    Fail ("La copia consistente falló (exit $exitCode). Causas típicas: el origen tiene un " +
        "-wal con frames pendientes y el contenedor no pudo leerlo en modo solo-lectura " +
        "(cierra cualquier 'rag ingest' que lo tenga abierto e intenta de nuevo), o la " +
        "ruta de origen no es accesible para Docker Desktop (compártela en Settings > " +
        "Resources > File sharing).")
}

Write-Host "`nCopia consistente cargada en el volumen '$volumeName' como summary-cache.sqlite3."
Write-Host "Reinicia rag-api para que abra la copia nueva: docker compose -f `"$ComposeFile`" restart rag-api"
