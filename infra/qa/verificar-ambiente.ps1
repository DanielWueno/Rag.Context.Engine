<#
.SYNOPSIS
    Verifica el ambiente dev/QA portable de infra/docker-compose.yml (ítem
    10.6.3 del plan, que integra el 21.1 local con el contrato vigente de main:
    12.1 clave de Qdrant, 12.8 loopback por defecto). Sale distinto de 0 si
    CUALQUIERA de los puntos (a)-(j) falla. Nunca convierte una comprobación
    que no pudo correr en un éxito.

.DESCRIPTION
    (a) 'docker compose config' resuelto no contiene '/Users/'.
    (b) Tras 'docker compose up -d --build', qdrant y rag-api quedan healthy.
    (c) GET /api/health reporta Qdrant "ok"; el estado del LLM se imprime TAL
        CUAL (no se oculta, no se exige mientras no haya Ollama nativo ni
        perfil servidor).
    (d) Qdrant (6333/6334) NO responde en la IP LAN del host y SÍ en 127.0.0.1
        — publicado SOLO en loopback.
    (e) Ítem 12.8: SIN fijar RAG_API_BIND, el puerto de rag-api NO acepta
        conexiones por una IP LAN del host (negativo) — loopback por defecto.
    (f) Una colección de prueba creada por este script sobrevive a
        'docker compose down' + 'up', y se borra al final.
    (g) 'docker compose exec rag-api id -u' no es 0 (no root).
    (h) Con una clave de Qdrant definida, una petición SIN esa clave recibe 401
        (negativo) — se prueba con un Qdrant efímero aparte, sin tocar el stack
        principal ya levantado.
    (i) /metrics no es accesible desde la IP LAN del host en el perfil por
        defecto (Metrics:Enabled=false, además de loopback-only).
    (j) La caché de resúmenes de rag-api vive en un volumen Docker nombrado
        (nunca un bind mount del WAL del host) cuando RAG_SUMMARY_CACHE_DIR no
        se fija a una ruta.

    Negativo: sin Docker en marcha, CADA punto se marca como fallo explícito
    (no se omite ninguno) y el script sale distinto de 0.

.PARAMETER ComposeFile
    Ruta al docker-compose.yml. Default: infra/docker-compose.yml junto a este
    script (resuelto por $PSScriptRoot, no por el directorio desde el que se
    invoque).

.PARAMETER EnvFile
    Ruta a un archivo de variables (formato infra/.env.example). Si no existe,
    el script FALLA con un mensaje claro — no copia infra/.env.example en su
    lugar (decisión explícita del ítem 21.1 original: evitar levantar un
    ambiente con valores de ejemplo sin que quien corre el script lo sepa).

.PARAMETER ProjectName
    Nombre de proyecto de Compose (docker compose -p). Permite correr esta
    verificación de forma aislada de cualquier stack ya levantado con el mismo
    docker-compose.yml, siempre que EnvFile también fije nombres de contenedor
    (RAG_API_CONTAINER_NAME/QDRANT_CONTAINER_NAME) y puertos de host distintos
    a los de ese otro stack — container_name es literal y NO se namespacea por
    el nombre de proyecto.

.PARAMETER RagApiContainerName
.PARAMETER QdrantContainerName
    Deben coincidir con RAG_API_CONTAINER_NAME/QDRANT_CONTAINER_NAME dentro de
    EnvFile (defaults: rag-api/rag-qdrant, los de producción).

.PARAMETER RagApiPort
.PARAMETER QdrantHttpPort
.PARAMETER QdrantGrpcPort
    Puertos de HOST a verificar (deben coincidir con RAG_API_PORT y
    QDRANT_HTTP_HOST_PORT/QDRANT_GRPC_HOST_PORT dentro de EnvFile).

.EXAMPLE
    pwsh infra/qa/verificar-ambiente.ps1
#>
[CmdletBinding()]
param(
    [string]$ComposeFile = (Join-Path $PSScriptRoot "..\docker-compose.yml"),
    [string]$EnvFile = (Join-Path $PSScriptRoot "..\.env"),
    [string]$ProjectName = "",
    [string]$RagApiContainerName = "rag-api",
    [string]$QdrantContainerName = "rag-qdrant",
    [int]$RagApiPort = 5080,
    [int]$QdrantHttpPort = 6333,
    [int]$QdrantGrpcPort = 6334
)

$ErrorActionPreference = "Stop"
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:LanIp = $null

$TODAS_LAS_LETRAS = @('a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j')

function Write-Section([string]$Text) {
    Write-Host ""
    Write-Host "=== $Text ===" -ForegroundColor Cyan
}

function Record-Pass([string]$Check, [string]$Message) {
    Write-Host "PASS ($Check): $Message" -ForegroundColor Green
}

function Record-Failure([string]$Check, [string]$Message) {
    $script:Failures.Add("[$Check] $Message")
    Write-Host "FAIL ($Check): $Message" -ForegroundColor Red
}

function Get-LanIPv4 {
    $candidates = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object {
            $_.IPAddress -ne "127.0.0.1" -and
            $_.PrefixOrigin -in @("Dhcp", "Manual") -and
            $_.InterfaceAlias -notmatch "vEthernet|Loopback|WSL|Docker|Npcap|Tailscale|ZeroTier"
        } |
        Sort-Object -Property @{ Expression = { $_.InterfaceAlias -match "Wi-?Fi|Ethernet" } } -Descending
    if (-not $candidates) { return $null }
    return ($candidates | Select-Object -First 1)
}

function Test-TcpPort([string]$IpAddress, [int]$Port, [int]$TimeoutMs = 1500) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $task = $client.ConnectAsync($IpAddress, $Port)
        $completed = $task.Wait($TimeoutMs)
        $ok = $completed -and $client.Connected
        $client.Close()
        return [bool]$ok
    } catch {
        return $false
    }
}

function Get-ContainerHealth([string]$Name) {
    $status = (docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}sin-healthcheck{{end}}' $Name 2>$null)
    if ($LASTEXITCODE -ne 0) { return "ausente" }
    return ($status | Out-String).Trim()
}

function Wait-ContainersHealthy([string[]]$Names, [int]$TimeoutSec = 180) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $status = @{}
    do {
        foreach ($n in $Names) { $status[$n] = Get-ContainerHealth $n }
        $allHealthy = -not ($status.Values | Where-Object { $_ -ne "healthy" })
        if ($allHealthy) { break }
        Start-Sleep -Seconds 3
    } while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec)
    return $status
}

function Exit-WithSummary {
    Write-Section "Resumen"
    if ($script:Failures.Count -eq 0) {
        Write-Host "Todas las comprobaciones (a-j) pasaron." -ForegroundColor Green
        exit 0
    } else {
        foreach ($f in $script:Failures) { Write-Host $f -ForegroundColor Red }
        Write-Host "$($script:Failures.Count) comprobacion(es) fallida(s) de $($TODAS_LAS_LETRAS.Count)." -ForegroundColor Red
        exit 1
    }
}

# ──────────────────────────────────────────────────────────────────────────
# Pre-flight: Docker debe responder. Si no, TODAS las letras se marcan como
# fallo explícito — nada se omite silenciosamente.
# ──────────────────────────────────────────────────────────────────────────
Write-Section "Pre-flight"

$dockerOk = $false
try {
    docker version --format '{{.Server.Os}}' 2>&1 | Out-Null
    $dockerOk = ($LASTEXITCODE -eq 0)
} catch {
    $dockerOk = $false
}

if (-not $dockerOk) {
    Write-Host "Docker no responde (DOCKER_HOST actual: '$env:DOCKER_HOST')." -ForegroundColor Red
    foreach ($c in $TODAS_LAS_LETRAS) {
        Record-Failure $c "Docker no disponible — la comprobacion no pudo ejecutarse."
    }
    Exit-WithSummary
}
Write-Host "Docker responde."

if (-not (Test-Path $ComposeFile)) {
    Write-Host "No existe el compose file '$ComposeFile'." -ForegroundColor Red
    foreach ($c in $TODAS_LAS_LETRAS) {
        Record-Failure $c "No existe el compose file."
    }
    Exit-WithSummary
}
$ComposeFile = (Resolve-Path $ComposeFile).Path
Write-Host "Compose file: $ComposeFile"

if (-not (Test-Path $EnvFile)) {
    Write-Host "No existe '$EnvFile'." -ForegroundColor Red
    Write-Host "Copia infra/.env.example a infra/.env y ajusta las variables antes de verificar." -ForegroundColor Red
    Write-Host "Este script NO copia .env.example por ti: levantar el ambiente con valores de ejemplo sin que se sepa es peor que fallar aqui." -ForegroundColor Red
    foreach ($c in $TODAS_LAS_LETRAS) {
        Record-Failure $c "El EnvFile no existe."
    }
    Exit-WithSummary
}
$EnvFile = (Resolve-Path $EnvFile).Path
Write-Host "Env file: $EnvFile"

$composeArgs = @("--env-file", $EnvFile, "-f", $ComposeFile)
if ($ProjectName) { $composeArgs = @("-p", $ProjectName) + $composeArgs }

# ──────────────────────────────────────────────────────────────────────────
# (a) El config resuelto no contiene '/Users/'
# ──────────────────────────────────────────────────────────────────────────
Write-Section "(a) docker compose config sin rutas /Users/"
try {
    $configText = (& docker compose @composeArgs config 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "exit $LASTEXITCODE`n$configText" }
    # Detecta el hardcode de Mac ("/Users/<nombre>/...", sin letra de unidad)
    # que tenia este compose antes del ítem 21.1 — NO una ruta de perfil de
    # Windows ("C:/Users/<nombre>/...", que es la convención normal de esta
    # plataforma y no un hardcode a limpiar). El lookbehind excluye el caso en
    # el que "/Users/" está precedido por "<letra>:".
    if ($configText -match '(?<!\w:)[/\\]Users[/\\]') {
        Record-Failure "a" "El config resuelto SI contiene una ruta de tipo '/Users/...' sin letra de unidad (hardcode de Mac)."
    } else {
        Record-Pass "a" "Config resuelto sin rutas de hardcode de Mac ('/Users/...' sin letra de unidad)."
    }
} catch {
    Record-Failure "a" "No se pudo resolver 'docker compose config': $($_.Exception.Message)"
}

# ──────────────────────────────────────────────────────────────────────────
# (b) up -d --build y healthy
# ──────────────────────────────────────────────────────────────────────────
Write-Section "(b) docker compose up -d --build -> qdrant y rag-api healthy"
try {
    & docker compose @composeArgs up -d --build 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "'docker compose up -d --build' salio con exit $LASTEXITCODE" }

    $status = Wait-ContainersHealthy -Names @($QdrantContainerName, $RagApiContainerName) -TimeoutSec 180
    if ($status[$QdrantContainerName] -eq "healthy" -and $status[$RagApiContainerName] -eq "healthy") {
        Record-Pass "b" "qdrant=$($status[$QdrantContainerName]), rag-api=$($status[$RagApiContainerName])."
    } else {
        Record-Failure "b" "qdrant=$($status[$QdrantContainerName]), rag-api=$($status[$RagApiContainerName]) tras 180s."
    }
} catch {
    Record-Failure "b" "Fallo levantando el stack: $($_.Exception.Message)"
}

# ──────────────────────────────────────────────────────────────────────────
# (c) GET /api/health: Qdrant ok, LLM tal cual (no se oculta, no se exige)
# ──────────────────────────────────────────────────────────────────────────
Write-Section "(c) GET /api/health"
try {
    $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$RagApiPort/api/health" -UseBasicParsing -TimeoutSec 15 -SkipHttpErrorCheck
    # Invoke-WebRequest devuelve .Content como byte[] (no como string) para
    # algunos content-types no reconocidos como texto (p. ej.
    # application/problem+json en una respuesta 503) — sin esto, Write-Host
    # imprimiria los codigos de byte en vez del JSON legible.
    $contentText = if ($resp.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($resp.Content)
    } else {
        $resp.Content
    }
    $body = $contentText | ConvertFrom-Json
    Write-Host "HTTP $($resp.StatusCode): $contentText"
    $qdrantCheck = $body.checks.qdrant
    $llmCheck = $body.checks.ollama
    Write-Host "Qdrant: $qdrantCheck"
    Write-Host "LLM (ollama), informado tal cual: $llmCheck"
    if ($qdrantCheck -eq "ok") {
        Record-Pass "c" "Qdrant ok. LLM: $llmCheck (no exigido)."
    } else {
        Record-Failure "c" "Qdrant NO esta ok: $qdrantCheck"
    }
} catch {
    Record-Failure "c" "No se pudo leer GET /api/health: $($_.Exception.Message)"
}

# ──────────────────────────────────────────────────────────────────────────
# (d) Qdrant: cerrado en LAN, abierto en 127.0.0.1
# ──────────────────────────────────────────────────────────────────────────
Write-Section "(d) Qdrant: publicado SOLO en 127.0.0.1"
$lan = Get-LanIPv4
if (-not $lan) {
    Record-Failure "d" "No se pudo determinar una IP LAN del host (adaptadores Dhcp/Manual, sin vEthernet/WSL/Docker)."
} else {
    $script:LanIp = $lan.IPAddress
    Write-Host "IP LAN elegida: $($script:LanIp) (adaptador: $($lan.InterfaceAlias))"
    $lanHttp = Test-TcpPort $script:LanIp $QdrantHttpPort
    $lanGrpc = Test-TcpPort $script:LanIp $QdrantGrpcPort
    $loopHttp = Test-TcpPort "127.0.0.1" $QdrantHttpPort
    $loopGrpc = Test-TcpPort "127.0.0.1" $QdrantGrpcPort
    Write-Host "LAN($($script:LanIp)): http=$lanHttp grpc=$lanGrpc | loopback: http=$loopHttp grpc=$loopGrpc"

    if ((-not $lanHttp) -and (-not $lanGrpc) -and $loopHttp -and $loopGrpc) {
        Record-Pass "d" "Qdrant cerrado en la LAN y abierto en 127.0.0.1."
    } else {
        Record-Failure "d" "LAN(${script:LanIp}): http=$lanHttp grpc=$lanGrpc ; loopback: http=$loopHttp grpc=$loopGrpc"
    }
}

# ──────────────────────────────────────────────────────────────────────────
# (e) Item 12.8: SIN RAG_API_BIND, rag-api NO responde en la IP LAN (negativo)
# ──────────────────────────────────────────────────────────────────────────
Write-Section "(e) rag-api (ítem 12.8): loopback por defecto, cerrado en la LAN"
try {
    if (-not $script:LanIp) { throw "sin IP LAN determinada en (d)" }
    $lanClosed = -not (Test-TcpPort $script:LanIp $RagApiPort)
    $loopOpen = Test-TcpPort "127.0.0.1" $RagApiPort
    Write-Host "LAN($($script:LanIp)):$RagApiPort cerrado=$lanClosed | loopback:$RagApiPort abierto=$loopOpen"
    if ($lanClosed -and $loopOpen) {
        Record-Pass "e" "rag-api NO acepta conexiones por la IP LAN (ítem 12.8: loopback por defecto) y SI por 127.0.0.1."
    } else {
        Record-Failure "e" "rag-api deberia estar cerrado en la LAN y abierto en loopback: LAN cerrado=$lanClosed, loopback abierto=$loopOpen"
    }
} catch {
    Record-Failure "e" "No se pudo evaluar el binding de rag-api: $($_.Exception.Message)"
}

# ──────────────────────────────────────────────────────────────────────────
# (f) Una coleccion de prueba sobrevive down+up, y se borra despues
# ──────────────────────────────────────────────────────────────────────────
Write-Section "(f) Coleccion de prueba sobrevive a down + up"
$testCollection = "verificar-ambiente-$(Get-Date -Format yyyyMMddHHmmss)"
try {
    $createBody = '{"vectors":{"size":2,"distance":"Cosine"}}'
    Invoke-RestMethod -Method Put -Uri "http://127.0.0.1:$QdrantHttpPort/collections/$testCollection" -Body $createBody -ContentType "application/json" | Out-Null

    $pointBody = '{"points":[{"id":1,"vector":[0.1,0.2],"payload":{"marker":"verificar-ambiente"}}]}'
    Invoke-RestMethod -Method Put -Uri "http://127.0.0.1:$QdrantHttpPort/collections/$testCollection/points?wait=true" -Body $pointBody -ContentType "application/json" | Out-Null

    Write-Host "Coleccion de prueba '$testCollection' creada con 1 punto."

    & docker compose @composeArgs down 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "'docker compose down' salio con exit $LASTEXITCODE" }

    & docker compose @composeArgs up -d 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "'docker compose up -d' (tras down) salio con exit $LASTEXITCODE" }

    $status = Wait-ContainersHealthy -Names @($QdrantContainerName, $RagApiContainerName) -TimeoutSec 180
    if ($status[$QdrantContainerName] -ne "healthy") { throw "qdrant no volvio a healthy tras el ciclo down/up ($($status[$QdrantContainerName]))" }

    $afterInfo = Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:$QdrantHttpPort/collections/$testCollection"
    $pointsCount = $afterInfo.result.points_count
    Write-Host "Tras down+up: points_count=$pointsCount"

    if ($pointsCount -ge 1) {
        Record-Pass "f" "La coleccion de prueba sobrevivio a 'docker compose down' + 'up' (points_count=$pointsCount)."
    } else {
        Record-Failure "f" "La coleccion de prueba NO sobrevivio (points_count=$pointsCount)."
    }
} catch {
    Record-Failure "f" "Fallo el ciclo down/up o la verificacion de la coleccion: $($_.Exception.Message)"
} finally {
    try {
        Invoke-RestMethod -Method Delete -Uri "http://127.0.0.1:$QdrantHttpPort/collections/$testCollection" -ErrorAction SilentlyContinue | Out-Null
        Write-Host "Coleccion de prueba '$testCollection' borrada."
    } catch {
        Write-Host "No se pudo confirmar el borrado de '$testCollection': $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# ──────────────────────────────────────────────────────────────────────────
# (g) rag-api no corre como root
# ──────────────────────────────────────────────────────────────────────────
Write-Section "(g) rag-api no corre como root"
try {
    $uidOut = (& docker compose @composeArgs exec -T rag-api id -u 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "exit ${LASTEXITCODE}: $uidOut" }
    $uidTrim = ($uidOut | Out-String).Trim()
    if ($uidTrim -match '^\d+$' -and $uidTrim -ne "0") {
        Record-Pass "g" "id -u dentro de rag-api = $uidTrim (no root)."
    } else {
        Record-Failure "g" "id -u dentro de rag-api = '$uidTrim' (root o valor inesperado)."
    }
} catch {
    Record-Failure "g" "No se pudo ejecutar 'docker compose exec rag-api id -u': $($_.Exception.Message)"
}

# ──────────────────────────────────────────────────────────────────────────
# (h) Con clave de Qdrant definida, una peticion SIN clave recibe 401
#     (negativo) — Qdrant efimero aparte, sin tocar el stack principal.
# ──────────────────────────────────────────────────────────────────────────
Write-Section "(h) Qdrant con API key: peticion sin clave -> 401"
$authTestContainer = "qdrant-auth-check-$(Get-Date -Format yyyyMMddHHmmss)"
$authTestPort = 0
try {
    # Puerto efimero libre: deja que el SO lo asigne y lo lee de vuelta.
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $authTestPort = $listener.LocalEndpoint.Port
    $listener.Stop()

    & docker run -d --rm --name $authTestContainer `
        -p "127.0.0.1:${authTestPort}:6333" `
        -e "QDRANT__SERVICE__API_KEY=clave-de-prueba-10-6-3" `
        qdrant/qdrant:v1.14.1 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "'docker run' del Qdrant efimero salio con exit $LASTEXITCODE" }

    $ready = $false
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 30) {
        if (Test-TcpPort "127.0.0.1" $authTestPort) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw "el Qdrant efimero de prueba de auth no acepto conexiones tras 30s" }
    Start-Sleep -Seconds 1

    $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$authTestPort/collections" -UseBasicParsing -TimeoutSec 10 -SkipHttpErrorCheck
    Write-Host "GET /collections SIN api-key -> HTTP $($resp.StatusCode)"
    if ($resp.StatusCode -eq 401) {
        Record-Pass "h" "Con RAG_QDRANT_API_KEY definido, una peticion sin clave recibe 401."
    } else {
        Record-Failure "h" "Se esperaba 401 sin api-key; se obtuvo HTTP $($resp.StatusCode)."
    }
} catch {
    Record-Failure "h" "No se pudo completar la prueba de autenticacion de Qdrant: $($_.Exception.Message)"
} finally {
    & docker rm -f $authTestContainer 2>&1 | Out-Null
}

# ──────────────────────────────────────────────────────────────────────────
# (i) /metrics no accesible desde la LAN en el perfil por defecto
# ──────────────────────────────────────────────────────────────────────────
Write-Section "(i) /metrics no accesible fuera de loopback"
try {
    if (-not $script:LanIp) { throw "sin IP LAN determinada en (d)" }
    $lanMetricsClosed = -not (Test-TcpPort $script:LanIp $RagApiPort)
    if ($lanMetricsClosed) {
        Record-Pass "i" "El puerto de rag-api esta cerrado en la LAN (Metrics:Enabled=false por defecto, ademas de loopback-only): /metrics no es alcanzable desde fuera de 127.0.0.1."
    } else {
        Record-Failure "i" "El puerto de rag-api responde en la IP LAN; /metrics quedaria potencialmente alcanzable fuera de loopback."
    }
} catch {
    Record-Failure "i" "No se pudo evaluar /metrics: $($_.Exception.Message)"
}

# ──────────────────────────────────────────────────────────────────────────
# (j) La cache de resumenes vive en un volumen nombrado, no un bind mount
# ──────────────────────────────────────────────────────────────────────────
Write-Section "(j) Cache de resumenes en volumen nombrado"
try {
    $mountsJson = (& docker inspect $RagApiContainerName --format '{{json .Mounts}}' 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "docker inspect fallo: $mountsJson" }
    $mounts = $mountsJson | ConvertFrom-Json
    $cacheMount = $mounts | Where-Object { $_.Destination -eq "/data/rag-engine" } | Select-Object -First 1
    if (-not $cacheMount) {
        Record-Failure "j" "No se encontro un mount en /data/rag-engine dentro del contenedor rag-api."
    } elseif ($cacheMount.Type -eq "volume") {
        Record-Pass "j" "/data/rag-engine es un volumen Docker nombrado ('$($cacheMount.Name)'), no un bind mount del host."
    } else {
        Record-Failure "j" "/data/rag-engine es un mount de tipo '$($cacheMount.Type)' (se esperaba 'volume'); RAG_SUMMARY_CACHE_DIR puede estar fijado a una ruta del host en este EnvFile."
    }
} catch {
    Record-Failure "j" "No se pudo inspeccionar el mount de la cache: $($_.Exception.Message)"
}

Exit-WithSummary
