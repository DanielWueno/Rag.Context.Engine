<#
  capacidad-ia.ps1 -- Capacidad de inferencia LLM. Windows.
  Espejo exacto de capacidad-ia.sh: mismo esquema JSON, mismas metricas.

    .\capacidad-ia.ps1                       informe legible
    .\capacidad-ia.ps1 -Json equipo.json     ademas escribe el registro comparable
    .\capacidad-ia.ps1 -Out reporte.txt      guarda el informe legible en un archivo
    .\capacidad-ia.ps1 -NoBench              omite toda medicion (solo lee specs)

  No instala nada. No descarga modelos.

  DOS REGLAS QUE ESTE ARCHIVO DEBE RESPETAR (ambas costaron un bug):
   1. ASCII PURO + CRLF. Windows PowerShell 5.1 lee los .ps1 como ANSI, y un
      caracter no-ASCII (raya larga, acento) desincroniza el parser y produce
      una cascada de errores en lineas que no tienen nada malo.
   2. Nada de funciones con nombre de una letra: los ALIAS ganan a las
      funciones. 'H' es alias de Get-History. Verificar con Get-Command.
#>

param(
    [string]$Json,
    [string]$Out,
    [switch]$NoBench
)

$ErrorActionPreference = "SilentlyContinue"
$SCHEMA   = "capacidad-ia/1"
$transcribiendo = $false
if ($Out) {
    try { Start-Transcript -Path $Out -Force | Out-Null; $transcribiendo = $true }
    catch { Write-Warning ("No se pudo abrir el archivo de salida: " + $Out) }
}
$BLOCK_MB = 128

function Seccion($t) { Write-Host ""; Write-Host ("== " + $t + " ==") -ForegroundColor Cyan }
function Num($v) { if ($null -eq $v -or $v -eq "") { return 0 } return $v }

# ------------------------------------------------------------------ identidad
$osInfo = Get-CimInstance Win32_OperatingSystem
$arch   = $env:PROCESSOR_ARCHITECTURE
Write-Host ("REPORTE DE CAPACIDAD IA  -  " + (Get-Date -Format 'yyyy-MM-dd HH:mm')) -ForegroundColor Yellow
Write-Host ("Equipo: " + $env:COMPUTERNAME + "   |   " + $osInfo.Caption + "   |   " + $arch)
Write-Host ("PowerShell: " + $PSVersionTable.PSVersion)

# ----------------------------------------------------------------------- CPU
Seccion "1. CPU"
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$cpuModel = $cpu.Name
if (-not $cpuModel) { $cpuModel = "no reportado" }
Write-Host $cpuModel.Trim()
Write-Host ("Nucleos: " + (Num $cpu.NumberOfCores) + " fisicos / " + (Num $cpu.NumberOfLogicalProcessors) + " logicos   Reloj max: " + (Num $cpu.MaxClockSpeed) + " MHz")

# ------------------------------------------------------------------- MEMORIA
Seccion "2. MEMORIA"
$ramGb  = [math]::Round($osInfo.TotalVisibleMemorySize/1MB, 1)
$freeGb = [math]::Round($osInfo.FreePhysicalMemory/1MB, 1)
Write-Host ("RAM total : " + $ramGb + " GB    disponible: " + $freeGb + " GB")
$mods    = @(Get-CimInstance Win32_PhysicalMemory)
$memSpeed = 0; $memBus = 0; $bwTheo = 0
if ($mods.Count -gt 0) {
    $memSpeed = ($mods | Measure-Object -Property Speed -Maximum).Maximum
    $memBus   = ($mods | Measure-Object -Property DataWidth -Sum).Sum
    if (-not $memBus) { $memBus = ($mods | Measure-Object -Property TotalWidth -Sum).Sum }
}
if ($memSpeed -and $memBus) {
    $bwTheo = [math]::Round($memSpeed * $memBus / 8 / 1000, 0)
    Write-Host ("Modulos: " + $mods.Count + " x " + $memSpeed + " MT/s  |  bus total " + $memBus + " bits")
    Write-Host ("ANCHO DE BANDA TEORICO : " + $bwTheo + " GB/s") -ForegroundColor Green
} else {
    Write-Host "ANCHO DE BANDA TEORICO : no legible (la LPDDR soldada suele ocultarse)" -ForegroundColor Yellow
}

# ----------------------------------------------------------------------- GPU
Seccion "3. GPU"
$gpus = @(Get-CimInstance Win32_VideoController)
$gpuName = ""; $gpuVendor = "ninguno"; $gpuVram = 0
foreach ($g in $gpus) {
    Write-Host ("- " + $g.Name)
    $vram = 0
    $key = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\*" |
           Where-Object { $_.DriverDesc -eq $g.Name } | Select-Object -First 1
    if ($key -and $key.'HardwareInformation.qwMemorySize') {
        $vram = [math]::Round($key.'HardwareInformation.qwMemorySize'/1GB, 1)
        Write-Host ("    VRAM real : " + $vram + " GB   (driver " + $g.DriverVersion + ")")
    } elseif ($g.AdapterRAM) {
        $vram = [math]::Round($g.AdapterRAM/1GB, 1)
        Write-Host ("    VRAM (dato poco fiable, WMI se satura en 4 GB) : " + $vram + " GB")
    }
    # Se queda con la GPU mas capaz encontrada.
    $v = "ninguno"
    if ($g.Name -match "NVIDIA|GeForce|RTX|Quadro|Tesla") { $v = "nvidia" }
    elseif ($g.Name -match "Radeon|AMD|ATI")              { $v = "amd" }
    elseif ($g.Name -match "Intel|Arc")                   { $v = "intel" }
    $rank = @{ "nvidia" = 3; "amd" = 2; "intel" = 1; "ninguno" = 0 }
    if ($rank[$v] -gt $rank[$gpuVendor]) { $gpuVendor = $v; $gpuName = $g.Name; $gpuVram = $vram }
}
Write-Host ""
switch ($gpuVendor) {
    "nvidia" {
        Write-Host "  VEREDICTO: NVIDIA discreta. Ollama la usa via CUDA. Caso bueno." -ForegroundColor Green
        if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
            nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader
        }
    }
    "amd"   { Write-Host "  VEREDICTO: AMD. Solo si ROCm soporta ese modelo. Verificar." -ForegroundColor Yellow }
    "intel" { Write-Host "  VEREDICTO: iGPU Intel. Ollama NO la usa por defecto (Vulkan/IPEX)." -ForegroundColor Yellow }
    default { Write-Host "  VEREDICTO: sin GPU util. La inferencia caeria 100 por ciento en CPU." -ForegroundColor Red }
}

# ---------------------------------------------------------------- ACELERADOR
Seccion "4. ACELERADOR DEDICADO (NPU)"
$npu = Get-CimInstance Win32_PnPEntity | Where-Object { $_.Name -match "NPU|Neural|AI Boost|VPU" } | Select-Object -First 1
$npuName = ""
if ($npu) { $npuName = $npu.Name; Write-Host ("- " + $npuName) } else { Write-Host "No se detecta acelerador dedicado." }
Write-Host "  NOTA: Ollama y llama.cpp NO tienen backend de NPU. Sus backends son" -ForegroundColor Yellow
Write-Host "        Metal, CUDA, ROCm, Vulkan y CPU. Los TOPS NO se traducen en tokens/s." -ForegroundColor Yellow

# ---------------------------------------------------------------- INFERENCIA
Seccion "5. INFERENCIA (medicion real)"
$olPresent = $false; $olModel = ""; $olSize = 0
$genTps = 0; $ppTps = 0; $effBw = 0; $effPct = 0; $processor = ""
if (Get-Command ollama -ErrorAction SilentlyContinue) {
    $olPresent = $true
    Write-Host ("Ollama: " + (ollama --version))
    if (-not $NoBench) {
        # /api/tags es estable entre versiones; 'ollama list' es texto y puede cambiar.
        $tags = Invoke-RestMethod -Uri "http://localhost:11434/api/tags" -Method Get
        if ($tags -and $tags.models -and $tags.models.Count -gt 0) {
            $chosen = $tags.models | Sort-Object size | Select-Object -First 1
            $olModel = $chosen.name
            # GB DECIMALES (10^9), no gibibytes: el ancho de banda teorico
            # tambien se expresa en GB decimales. Mezclarlos mete 7% de error.
            $olSize  = [math]::Round($chosen.size/1e9, 2)
            Write-Host ("Modelo elegido (el mas pequeno instalado): " + $olModel + "  (" + $olSize + " GB)")
            Write-Host "Midiendo..."
            $body = @{
                model   = $olModel
                prompt  = "Explica en 100 palabras que es una base de datos relacional."
                stream  = $false
                options = @{ num_predict = 150; temperature = 0 }
            } | ConvertTo-Json -Depth 5
            $r = Invoke-RestMethod -Uri "http://localhost:11434/api/generate" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 300
            if ($r -and $r.eval_duration -gt 0) {
                $genTps = [math]::Round($r.eval_count / ($r.eval_duration/1e9), 1)
                if ($r.prompt_eval_duration -gt 0) {
                    $ppTps = [math]::Round($r.prompt_eval_count / ($r.prompt_eval_duration/1e9), 1)
                }
                $effBw = [math]::Round($olSize * $genTps, 1)
                Write-Host ""
                Write-Host ("  prompt eval         : " + $ppTps + " tok/s")
                Write-Host ("  GENERACION          : " + $genTps + " tok/s")
                Write-Host ("  ANCHO BANDA EFECTIVO: " + $effBw + " GB/s   <== metrica comparable entre maquinas") -ForegroundColor Green
                if ($bwTheo -gt 0) {
                    $effPct = [math]::Round($effBw * 100 / $bwTheo, 0)
                    Write-Host ("  EFICIENCIA          : " + $effPct + " por ciento del teorico (" + $bwTheo + " GB/s)")
                }
                $psLine = (ollama ps | Select-Object -Skip 1 | Select-Object -First 1)
                if ($psLine -match '(\d+%\s*(GPU|CPU))') { $processor = $matches[1]; Write-Host ("  Corrio en           : " + $processor) }
            } else {
                Write-Host "  El servidor de Ollama no respondio (arrancalo con: ollama serve)" -ForegroundColor Yellow
            }
        } else {
            Write-Host "Sin modelos instalados: no hay medicion de inferencia posible." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "Ollama NO instalado. Se usa la prueba sintetica como sustituto."
}

# ---------------------------------------------------------- PRUEBA SINTETICA
Seccion "6. PRUEBA SINTETICA DE MEMORIA"
$synGbs = 0; $synMethod = "omitida"
if (-not $NoBench) {
    $synMethod = "memcpy 1 hilo (Buffer.BlockCopy)"
    $n = $BLOCK_MB * 1MB
    try {
        $src = New-Object byte[] $n
        $dst = New-Object byte[] $n
        [System.Buffer]::BlockCopy($src, 0, $dst, 0, $n)   # calentamiento
        $best = 0.0
        for ($i = 0; $i -lt 5; $i++) {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            [System.Buffer]::BlockCopy($src, 0, $dst, 0, $n)
            $sw.Stop()
            $sec = $sw.Elapsed.TotalSeconds
            if ($sec -gt 0) {
                $g = (2 * $n) / $sec / 1e9    # trafico = lectura + escritura
                if ($g -gt $best) { $best = $g }
            }
        }
        $synGbs = [math]::Round($best, 1)
        $src = $null; $dst = $null; [System.GC]::Collect()
    } catch {
        $synMethod = "fallo (memoria insuficiente)"
    }
    Write-Host ("Metodo : " + $synMethod + "  (bloques de " + $BLOCK_MB + " MB, trafico = lectura + escritura)")
    Write-Host ("INDICE DE MEMORIA : " + $synGbs + " GB/s") -ForegroundColor Green
    Write-Host "  NOTA: copia de UN solo hilo. No es el pico de la maquina (queda entre" -ForegroundColor DarkGray
    Write-Host "        un tercio y la mitad), pero es el MISMO test en todas, asi que" -ForegroundColor DarkGray
    Write-Host "        sirve para ordenarlas cuando no hay Ollama para medir de verdad." -ForegroundColor DarkGray
} else {
    Write-Host "omitida (-NoBench)"
}

# ----------------------------------------------------------------------- RED
Seccion "7. RED"
foreach ($a in (Get-NetAdapter | Where-Object { $_.Status -eq "Up" })) {
    Write-Host ("- " + $a.Name + "  |  " + $a.LinkSpeed + "  |  " + $a.InterfaceDescription)
}
foreach ($ipa in (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike "127.*" })) {
    Write-Host ("  IP: " + $ipa.IPAddress + "  (" + $ipa.InterfaceAlias + ")")
}
$lis = @(Get-NetTCPConnection -LocalPort 11434 -State Listen)
if ($lis.Count -gt 0) { Write-Host ("Puerto 11434: " + $lis[0].LocalAddress + ":" + $lis[0].LocalPort) }
else { Write-Host "Puerto 11434: no escucha" }

# ------------------------------------------------------------------- ENERGIA
Seccion "8. ENERGIA Y TERMICA"
$bat = Get-CimInstance Win32_Battery | Select-Object -First 1
$battPresent = $false
if ($bat) {
    $battPresent = $true
    Write-Host ("Laptop (bateria presente). Carga: " + $bat.EstimatedChargeRemaining + " por ciento")
    Write-Host "  NOTA: el throttling termico recorta 30-50 por ciento del rendimiento" -ForegroundColor Yellow
    Write-Host "        sostenido en cargas largas. Mala anfitriona para trabajos de horas." -ForegroundColor Yellow
} else {
    Write-Host "Equipo de escritorio o servidor: sin bateria, sin techo termico por energia."
}

# --------------------------------------------------------------------- JSON
if ($Json) {
    $rec = [ordered]@{
        schema    = $SCHEMA
        timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        host      = [ordered]@{ name = "$env:COMPUTERNAME"; os = "windows"; os_version = "$($osInfo.Caption)"; arch = "$arch" }
        cpu       = [ordered]@{ model = $cpuModel.Trim(); cores_physical = [int](Num $cpu.NumberOfCores); cores_logical = [int](Num $cpu.NumberOfLogicalProcessors) }
        memory    = [ordered]@{ total_gb = $ramGb; speed_mts = [int](Num $memSpeed); bus_bits = [int](Num $memBus); bandwidth_theoretical_gbs = $bwTheo }
        gpu       = [ordered]@{ name = "$gpuName"; vendor = $gpuVendor; vram_gb = $gpuVram }
        accelerator = [ordered]@{ name = "$npuName"; usable_by_ollama = $false }
        inference = [ordered]@{
            ollama_present = $olPresent
            model = "$olModel"
            model_size_gb = $olSize
            processor = "$processor"
            prompt_eval_tps = $ppTps
            generation_tps = $genTps
            effective_bandwidth_gbs = $effBw
            efficiency_pct = $effPct
        }
        synthetic = [ordered]@{ memcpy_gbs = $synGbs; method = $synMethod }
        power     = [ordered]@{ battery_present = $battPresent }
    }
    $rec | ConvertTo-Json -Depth 6 | Out-File -FilePath $Json -Encoding utf8
    Write-Host ""
    Write-Host ("Registro comparable escrito en: " + $Json) -ForegroundColor Green
}

Write-Host ""
Write-Host "== FIN ==" -ForegroundColor Yellow

if ($transcribiendo) {
    Stop-Transcript | Out-Null
    Write-Host ("Informe legible guardado en: " + $Out) -ForegroundColor Green
}
