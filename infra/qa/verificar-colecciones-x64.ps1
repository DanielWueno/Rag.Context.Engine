<#
.SYNOPSIS
    Verificacion mecanica del item de plan 18.2 (poblar colecciones rag-engine
    y micro-repo en el ambiente dev/QA x64 de DSOFT03).

.DESCRIPTION
    Falla (exit distinto de 0) si:
      - rag-engine o micro-repo no existen en /api/collections, o
      - alguna de esas dos colecciones tiene 0 puntos en Qdrant, o
      - no se puede correr el eval de recall de micro-repo, parsear su JSON,
        o leer el baseline de referencia, o
      - el recall@10 (hit_any) de la corrida actual tiene MAS DE 1 hit menos
        que el baseline (docs/eval/baselines/tickets-microservice.norerank.baseline.json).

    No oculta ninguna comparacion que no se pudo ejecutar: una infraestructura
    faltante, un JSON que no parsea o un baseline ausente se reportan como FALLA,
    nunca como exito silencioso.

.NOTES
    Pensado para correr de forma independiente (el orquestador lo vuelve a
    ejecutar el solo, sin depender de los archivos de evidencia de docs/eval/18.2/).
#>

param(
    [string]$ApiBaseUrl = "http://127.0.0.1:5080",
    [string]$QdrantBaseUrl = "http://127.0.0.1:6333",
    [string]$EvalSet = "docs/eval/tickets-microservice.eval-set.json",
    [string]$BaselinePath = "docs/eval/baselines/tickets-microservice.norerank.baseline.json",
    [string]$ModelsDir = "$env:USERPROFILE\models",
    [string[]]$RequiredCollections = @("rag-engine", "micro-repo"),
    [string]$RecallCollection = "micro-repo",
    [int]$TopK = 10
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$failures = [System.Collections.Generic.List[string]]::new()
$pointsByCollection = @{}

Write-Host "=== Verificacion mecanica 18.2: colecciones x64 + recall micro-repo ===" -ForegroundColor Cyan
Write-Host "Repo root: $repoRoot"
Write-Host "API:       $ApiBaseUrl"
Write-Host "Qdrant:    $QdrantBaseUrl"
Write-Host ""

# ---------------------------------------------------------------------------
# 1) /api/collections — existencia de ambas colecciones
# ---------------------------------------------------------------------------
Write-Host "--- 1) Colecciones vía rag-api ---"
$apiCollections = @()
try {
    $resp = Invoke-RestMethod -Uri "$ApiBaseUrl/api/collections" -Method Get -TimeoutSec 15
    $apiCollections = @($resp.collections)
    Write-Host "Colecciones reportadas por rag-api: $($apiCollections -join ', ')"
}
catch {
    $failures.Add("No se pudo consultar '$ApiBaseUrl/api/collections': $($_.Exception.Message)")
    Write-Host "ERROR consultando /api/collections: $($_.Exception.Message)" -ForegroundColor Red
}

foreach ($name in $RequiredCollections) {
    if ($apiCollections -notcontains $name) {
        $failures.Add("La coleccion '$name' no aparece en /api/collections")
        Write-Host "FALTA: $name no esta en /api/collections" -ForegroundColor Red
    }
}

# ---------------------------------------------------------------------------
# 2) Conteo de puntos por coleccion vía Qdrant REST directo
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "--- 2) Conteo de puntos vía Qdrant ($QdrantBaseUrl) ---"
foreach ($name in $RequiredCollections) {
    try {
        $info = Invoke-RestMethod -Uri "$QdrantBaseUrl/collections/$name" -Method Get -TimeoutSec 15
        $count = $info.result.points_count
        $pointsByCollection[$name] = $count
        if ($null -eq $count -or $count -le 0) {
            $failures.Add("La coleccion '$name' existe pero tiene 0 puntos")
            Write-Host "$name -> $count puntos (FALLA: debe ser > 0)" -ForegroundColor Red
        }
        else {
            Write-Host "$name -> $count puntos" -ForegroundColor Green
        }
    }
    catch {
        $pointsByCollection[$name] = $null
        $failures.Add("No se pudo leer el conteo de puntos de '$name' en Qdrant: $($_.Exception.Message)")
        Write-Host "$name -> ERROR consultando Qdrant: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# ---------------------------------------------------------------------------
# 3) Baseline de recall de micro-repo
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "--- 3) Baseline de recall ($BaselinePath) ---"
$baselineFullPath = Join-Path $repoRoot $BaselinePath
$baselineHits = $null
if (-not (Test-Path $baselineFullPath)) {
    $failures.Add("No existe el archivo de baseline: $BaselinePath")
    Write-Host "FALTA el archivo de baseline: $baselineFullPath" -ForegroundColor Red
}
else {
    try {
        $baselineJson = Get-Content -Raw -Path $baselineFullPath | ConvertFrom-Json
        $baselineHits = @($baselineJson.results | Where-Object { $_.hit_any_at_k.'10' -eq $true }).Count
        $baselineTotal = @($baselineJson.results).Count
        Write-Host "Baseline: $baselineHits / $baselineTotal hits (hit_any_at_k.$TopK)"
    }
    catch {
        $failures.Add("No se pudo parsear el baseline '$BaselinePath': $($_.Exception.Message)")
        Write-Host "ERROR parseando el baseline: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# ---------------------------------------------------------------------------
# 4) Correr el eval actual de micro-repo y contar hits
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "--- 4) Eval actual de '$RecallCollection' (k=$TopK) ---"
$currentHits = $null
$currentTotal = $null
try {
    $env:RAG_MODELS_DIR = $ModelsDir
    Push-Location $repoRoot
    try {
        $rawOutput = & dotnet run --project src/RagEngine.Cli -- eval `
            --eval-set $EvalSet `
            -c $RecallCollection `
            -k $TopK `
            --baseline $BaselinePath `
            --json 2>&1 | Out-String
        $evalExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($evalExitCode -ne 0) {
        $failures.Add("El comando 'rag eval' salio con codigo $evalExitCode")
        Write-Host "ERROR: 'rag eval' salio con codigo $evalExitCode" -ForegroundColor Red
        Write-Host $rawOutput
    }
    else {
        $jsonStart = $rawOutput.IndexOf('{')
        if ($jsonStart -lt 0) {
            $failures.Add("La salida de 'rag eval --json' no contiene un objeto JSON reconocible")
            Write-Host "ERROR: no se encontro '{' en la salida de eval" -ForegroundColor Red
            Write-Host $rawOutput
        }
        else {
            $jsonText = $rawOutput.Substring($jsonStart)
            try {
                $evalJson = $jsonText | ConvertFrom-Json
                $currentHits = @($evalJson.results | Where-Object { $_.hit_any_at_k.'10' -eq $true }).Count
                $currentTotal = @($evalJson.results).Count
                Write-Host "Corrida actual: $currentHits / $currentTotal hits (hit_any_at_k.$TopK)"
                Write-Host "Modelo de embedding usado: $($evalJson.provenance.embedding_model)"
            }
            catch {
                $failures.Add("No se pudo parsear el JSON de salida de 'rag eval': $($_.Exception.Message)")
                Write-Host "ERROR parseando JSON de eval: $($_.Exception.Message)" -ForegroundColor Red
            }
        }
    }
}
catch {
    $failures.Add("Excepcion corriendo 'rag eval': $($_.Exception.Message)")
    Write-Host "EXCEPCION corriendo 'rag eval': $($_.Exception.Message)" -ForegroundColor Red
}

# ---------------------------------------------------------------------------
# 5) Comparacion de recall: no peor fuera de 1 acierto neto por set
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "--- 5) Comparacion de recall ---"
$delta = $null
if ($null -ne $baselineHits -and $null -ne $currentHits) {
    $delta = $currentHits - $baselineHits
    Write-Host ("Hits baseline: {0}   Hits actual: {1}   Delta: {2}" -f $baselineHits, $currentHits, $delta)
    if (($baselineHits - $currentHits) -gt 1) {
        $failures.Add("Recall degradado: $currentHits hits actuales vs. $baselineHits del baseline (cae mas de 1 acierto neto)")
        Write-Host "FALLA: la corrida actual tiene mas de 1 acierto menos que el baseline" -ForegroundColor Red
    }
    else {
        Write-Host "OK: la corrida actual no cae mas de 1 acierto por debajo del baseline" -ForegroundColor Green
    }
}
else {
    $failures.Add("No se pudo calcular el delta de recall (falta hits de baseline y/o de la corrida actual)")
    Write-Host "FALLA: no hay datos suficientes para comparar baseline vs. actual" -ForegroundColor Red
}

# ---------------------------------------------------------------------------
# Resumen final
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Resumen ==="
foreach ($name in $RequiredCollections) {
    Write-Host ("Puntos {0}: {1}" -f $name, $pointsByCollection[$name])
}
Write-Host ("Hits baseline (hit_any_at_k.{0}): {1}" -f $TopK, $baselineHits)
Write-Host ("Hits actual   (hit_any_at_k.{0}): {1}" -f $TopK, $currentHits)
Write-Host ("Delta (actual - baseline): {0}" -f $delta)

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Fallas detectadas:" -ForegroundColor Red
    foreach ($f in $failures) {
        Write-Host "  - $f" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "RESULTADO FINAL: FALLA" -ForegroundColor Red
    exit 1
}
else {
    Write-Host ""
    Write-Host "RESULTADO FINAL: PASA" -ForegroundColor Green
    exit 0
}
