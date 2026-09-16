#!/usr/bin/env sh
# capacidad-ia.sh -- Capacidad de inferencia LLM. macOS y Linux.
# Espejo exacto de capacidad-ia.ps1: mismo esquema JSON, mismas metricas.
#
#   ./capacidad-ia.sh                      informe legible
#   ./capacidad-ia.sh --json equipo.json   ademas escribe el registro comparable
#   ./capacidad-ia.sh --out reporte.txt    guarda el informe legible en un archivo
#   ./capacidad-ia.sh --no-bench           omite toda medicion (solo lee specs)
#
# No instala nada. Compatible con sh/bash 3.2.

SCHEMA="capacidad-ia/1"
JSON_OUT=""
TXT_OUT=""
DO_BENCH=1
BLOCK_MB=128

while [ $# -gt 0 ]; do
  case "$1" in
    --json) JSON_OUT="$2"; shift 2 ;;
    --out)  TXT_OUT="$2";  shift 2 ;;
    --no-bench) DO_BENCH=0; shift ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "Opcion desconocida: $1" >&2; exit 2 ;;
  esac
done

# Duplica toda la salida al archivo sin quitarla de la consola. Se reinvoca una
# sola vez; la variable de entorno corta la recursion.
if [ -n "$TXT_OUT" ] && [ -z "$CAPIA_TEED" ]; then
  CAPIA_TEED=1
  export CAPIA_TEED
  sh "$0" "$@" | tee "$TXT_OUT"
  st=$?
  printf '\nInforme legible guardado en: %s\n' "$TXT_OUT"
  exit $st
fi

case "$(uname -s)" in
  Darwin) OSKIND="macos" ;;
  Linux)  OSKIND="linux" ;;
  *)      OSKIND="desconocido" ;;
esac

sec() { printf '\n== %s ==\n' "$1"; }
n0()  { [ -n "$1" ] && echo "$1" || echo "0"; }
jstr(){ printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

# ---------------------------------------------------------------- identidad
HOSTN=$(hostname 2>/dev/null)
ARCH=$(uname -m)
if [ "$OSKIND" = "macos" ]; then
  OSVER="macOS $(sw_vers -productVersion 2>/dev/null)"
else
  OSVER=$(. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME")
  [ -z "$OSVER" ] && OSVER="$(uname -sr)"
fi

printf 'REPORTE DE CAPACIDAD IA  -  %s\n' "$(date '+%Y-%m-%d %H:%M')"
printf 'Equipo: %s   |   %s   |   %s\n' "$HOSTN" "$OSVER" "$ARCH"

# --------------------------------------------------------------------- CPU
sec "1. CPU"
if [ "$OSKIND" = "macos" ]; then
  CPU_MODEL=$(sysctl -n machdep.cpu.brand_string)
  CPU_LOG=$(sysctl -n hw.logicalcpu)
  CPU_PHY=$(sysctl -n hw.physicalcpu)
else
  # Cadena de respaldo: x86 trae "model name"; ARM a menudo no trae nada util.
  CPU_MODEL=$(awk -F: '/model name/{gsub(/^ /,"",$2);print $2;exit}' /proc/cpuinfo 2>/dev/null)
  ok_model() { case "$1" in ""|"-"|"unknown"|"Unknown"|"n/a") return 1 ;; *) return 0 ;; esac; }
  ok_model "$CPU_MODEL" || CPU_MODEL=$(lscpu 2>/dev/null | awk -F: '/Model name/{gsub(/^ +/,"",$2);print $2;exit}')
  ok_model "$CPU_MODEL" || CPU_MODEL=""
  [ -z "$CPU_MODEL" ] && CPU_MODEL=$(awk -F: '/^(Model|Hardware)[ \t]*:/{gsub(/^ /,"",$2);print $2;exit}' /proc/cpuinfo 2>/dev/null)
  if [ -z "$CPU_MODEL" ] && [ -r /sys/firmware/devicetree/base/model ]; then
    CPU_MODEL=$(tr -d '\0' < /sys/firmware/devicetree/base/model 2>/dev/null)
  fi
  [ -z "$CPU_MODEL" ] && CPU_MODEL="no reportado por el kernel ($ARCH)"
  CPU_LOG=$(nproc 2>/dev/null || grep -c ^processor /proc/cpuinfo)
  CPU_PHY=$(awk -F: '/cpu cores/{gsub(/ /,"",$2);print $2;exit}' /proc/cpuinfo)
  [ -z "$CPU_PHY" ] && CPU_PHY="$CPU_LOG"
fi
CPU_MODEL=${CPU_MODEL:-desconocido}
echo "$CPU_MODEL"
printf 'Nucleos: %s fisicos / %s logicos   Arquitectura: %s\n' "$(n0 "$CPU_PHY")" "$(n0 "$CPU_LOG")" "$ARCH"

# ------------------------------------------------------------------ MEMORIA
sec "2. MEMORIA"
MEM_SPEED=0; MEM_BUS=0; BW_THEO=0
if [ "$OSKIND" = "macos" ]; then
  RAM_GB=$(echo "scale=1; $(sysctl -n hw.memsize)/1073741824" | bc)
  RAM_FREE=""
  case "$CPU_MODEL" in
    *"M4 Max"*) BW_THEO=546 ;;  *"M4 Pro"*) BW_THEO=273 ;;  *"M4"*) BW_THEO=120 ;;
    *"M3 Max"*) BW_THEO=400 ;;  *"M3 Pro"*) BW_THEO=150 ;;  *"M3"*) BW_THEO=100 ;;
    *"M2 Max"*) BW_THEO=400 ;;  *"M2 Pro"*) BW_THEO=200 ;;  *"M2"*) BW_THEO=100 ;;
    *"M1 Max"*) BW_THEO=400 ;;  *"M1 Pro"*) BW_THEO=200 ;;  *"M1"*) BW_THEO=68  ;;
  esac
  echo "RAM total : ${RAM_GB} GB (unificada CPU+GPU)"
  [ "$BW_THEO" != "0" ] && echo "ANCHO DE BANDA TEORICO : ${BW_THEO} GB/s (spec del chip)"
else
  RAM_GB=$(awk '/MemTotal/{printf "%.1f",$2/1048576}' /proc/meminfo)
  RAM_FREE=$(awk '/MemAvailable/{printf "%.1f",$2/1048576}' /proc/meminfo)
  echo "RAM total : ${RAM_GB} GB    disponible: ${RAM_FREE:-?} GB"
  DMI=$(dmidecode -t memory 2>/dev/null || sudo -n dmidecode -t memory 2>/dev/null)
  if [ -n "$DMI" ]; then
    MEM_SPEED=$(echo "$DMI" | awk '/Configured Memory Speed|Speed:/{for(i=1;i<=NF;i++)if($i ~ /^[0-9]+$/ && $i>m)m=$i}END{print m+0}')
    MEM_BUS=$(echo "$DMI" | awk '/Data Width/{for(i=1;i<=NF;i++)if($i ~ /^[0-9]+$/)s+=$i}END{print s+0}')
    if [ "$MEM_SPEED" -gt 0 ] 2>/dev/null && [ "$MEM_BUS" -gt 0 ] 2>/dev/null; then
      BW_THEO=$(echo "scale=0; $MEM_SPEED*$MEM_BUS/8000" | bc)
      echo "Modulos: ${MEM_SPEED} MT/s  |  bus ${MEM_BUS} bits"
      echo "ANCHO DE BANDA TEORICO : ${BW_THEO} GB/s"
    fi
  fi
  [ "$BW_THEO" = "0" ] && echo "ANCHO DE BANDA TEORICO : no legible (dmidecode requiere root)"
fi

# ---------------------------------------------------------------------- GPU
sec "3. GPU"
GPU_NAME=""; GPU_VENDOR="ninguno"; GPU_VRAM=0
if [ "$OSKIND" = "macos" ]; then
  GPU_NAME=$(system_profiler SPDisplaysDataType 2>/dev/null | awk -F': ' '/Chipset Model/{print $2;exit}')
  GCORES=$(system_profiler SPDisplaysDataType 2>/dev/null | awk -F': ' '/Total Number of Cores/{print $2;exit}')
  GPU_VENDOR="apple"
  printf -- '- %s' "$GPU_NAME"; [ -n "$GCORES" ] && printf ' (%s nucleos)' "$GCORES"; echo
  echo "  VEREDICTO: Apple Silicon. Ollama usa Metal siempre."
  echo "  Memoria unificada: sin techo de VRAM separado."
else
  if command -v nvidia-smi >/dev/null 2>&1; then
    GPU_NAME=$(nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null | head -1)
    GPU_VRAM=$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | head -1)
    GPU_VRAM=$(echo "scale=1; $(n0 "$GPU_VRAM")/1024" | bc)
    GPU_VENDOR="nvidia"
  else
    GPU_NAME=$(lspci 2>/dev/null | grep -iE 'vga|3d controller' | head -1 | sed 's/.*: //')
  fi
  [ -n "$GPU_NAME" ] && echo "- $GPU_NAME" || echo "- sin GPU detectada"
  case "$GPU_NAME" in
    *NVIDIA*|*GeForce*|*RTX*|*Quadro*|*Tesla*) GPU_VENDOR="nvidia" ;;
    *AMD*|*Radeon*|*ATI*) GPU_VENDOR="amd" ;;
    *Intel*|*Arc*) GPU_VENDOR="intel" ;;
  esac
  case "$GPU_VENDOR" in
    nvidia) echo "  VEREDICTO: NVIDIA discreta. Ollama la usa via CUDA. Caso bueno." ;;
    amd)    echo "  VEREDICTO: AMD. Solo si ROCm soporta ese modelo. Verificar." ;;
    intel)  echo "  VEREDICTO: iGPU Intel. Ollama NO la usa por defecto (Vulkan/IPEX)." ;;
    *)      echo "  VEREDICTO: sin GPU util. La inferencia caeria 100% en CPU." ;;
  esac
fi

# -------------------------------------------------------------- ACELERADOR
sec "4. ACELERADOR DEDICADO (NPU / ANE)"
NPU_NAME=""
if [ "$OSKIND" = "macos" ]; then
  case "$CPU_MODEL" in *Apple*) NPU_NAME="Apple Neural Engine (16 nucleos)";; esac
else
  NPU_NAME=$(lspci 2>/dev/null | grep -iE 'neural|npu|ai accel' | head -1 | sed 's/.*: //')
fi
[ -n "$NPU_NAME" ] && echo "- $NPU_NAME" || echo "No se detecta acelerador dedicado."
echo "  NOTA: Ollama y llama.cpp NO tienen backend de NPU/ANE. Sus backends son"
echo "        Metal, CUDA, ROCm, Vulkan y CPU. Los TOPS NO se traducen en tokens/s."

# -------------------------------------------------------------- INFERENCIA
sec "5. INFERENCIA (medicion real)"
OL_PRESENT=false; OL_VER=""; OL_MODEL=""; OL_SIZE=0
GEN_TPS=0; PP_TPS=0; EFF_BW=0; EFF_PCT=0; PROCESSOR=""
if command -v ollama >/dev/null 2>&1; then
  OL_PRESENT=true
  OL_VER=$(ollama --version 2>/dev/null | head -1)
  echo "Ollama: $OL_VER"
  if [ "$DO_BENCH" = "1" ]; then
    # Elige el modelo mas pequeno instalado: mas rapido y suficiente para normalizar.
    # /api/tags da bytes exactos y es estable entre versiones. Se normaliza a GB
    # DECIMALES (10^9) para poder dividir contra el ancho de banda teorico.
    PICK=""
    if command -v python3 >/dev/null 2>&1; then
      PICK=$(curl -s --max-time 20 http://localhost:11434/api/tags 2>/dev/null | python3 -c '
import sys, json
try:
    m = json.load(sys.stdin).get("models", [])
except Exception:
    m = []
if m:
    c = min(m, key=lambda x: x.get("size", 1<<62))
    print("%s %.2f" % (c["name"], c.get("size",0)/1e9))
' 2>/dev/null)
    fi
    if [ -z "$PICK" ]; then
      # Respaldo: texto de 'ollama list', ya viene en GB decimales.
      PICK=$(ollama list 2>/dev/null | tail -n +2 | awk '{
        s=$3; u=$4; g=(u ~ /^MB/) ? s/1024 : s;
        if (best=="" || g<best) { best=g; name=$1; sz=g }
      } END { if (name!="") printf "%s %.2f", name, sz }')
    fi
    if [ -n "$PICK" ]; then
      OL_MODEL=$(echo "$PICK" | awk '{print $1}')
      OL_SIZE=$(echo "$PICK" | awk '{print $2}')
      echo "Modelo elegido (el mas pequeno instalado): $OL_MODEL  (${OL_SIZE} GB)"
      echo "Midiendo..."
      REQ='{"model":"'"$OL_MODEL"'","prompt":"Explica en 100 palabras que es una base de datos relacional.","stream":false,"options":{"num_predict":150,"temperature":0}}'
      RES=$(curl -s --max-time 300 http://localhost:11434/api/generate -d "$REQ")
      if [ -n "$RES" ]; then
        GEN_TPS=$(echo "$RES" | tr ',' '\n' | awk -F: '/"eval_count"/{c=$2} /"eval_duration"/{d=$2} END{if(d>0) printf "%.1f", c/(d/1e9); else print 0}')
        PP_TPS=$(echo "$RES" | tr ',' '\n' | awk -F: '/"prompt_eval_count"/{c=$2} /"prompt_eval_duration"/{d=$2} END{if(d>0) printf "%.1f", c/(d/1e9); else print 0}')
        EFF_BW=$(echo "scale=1; $OL_SIZE*$GEN_TPS/1" | bc 2>/dev/null || echo 0)
        echo ""
        echo "  prompt eval        : ${PP_TPS} tok/s"
        echo "  GENERACION         : ${GEN_TPS} tok/s"
        echo "  ANCHO BANDA EFECTIVO: ${EFF_BW} GB/s   <== metrica comparable entre maquinas"
        if [ "$BW_THEO" != "0" ]; then
          EFF_PCT=$(echo "scale=0; $EFF_BW*100/$BW_THEO" | bc 2>/dev/null || echo 0)
          echo "  EFICIENCIA         : ${EFF_PCT}% del teorico (${BW_THEO} GB/s)"
        fi
        PROCESSOR=$(ollama ps 2>/dev/null | tail -n +2 | grep -oE '[0-9]+%[ \t]*(GPU|CPU)' | head -1)
        [ -n "$PROCESSOR" ] && echo "  Corrio en          : $PROCESSOR"
      else
        echo "  El servidor de Ollama no respondio (arrancalo con: ollama serve)"
      fi
    else
      echo "Sin modelos instalados: no hay medicion de inferencia posible."
    fi
  fi
else
  echo "Ollama NO instalado. Se usa la prueba sintetica como sustituto."
fi

# ---------------------------------------------------------- PRUEBA SINTETICA
sec "6. PRUEBA SINTETICA DE MEMORIA"
SYN_GBS=0; SYN_METHOD="omitida"
if [ "$DO_BENCH" = "1" ]; then
  if command -v python3 >/dev/null 2>&1; then
    SYN_METHOD="memcpy 1 hilo (python3)"
    SYN_GBS=$(python3 - "$BLOCK_MB" <<'PY'
import sys, time
mb = int(sys.argv[1]); n = mb*1024*1024
try:
    src = bytearray(n); dst = bytearray(n)
except MemoryError:
    print("0"); sys.exit()
dst[:] = src
best = 0.0
for _ in range(5):
    t0 = time.perf_counter(); dst[:] = src; t1 = time.perf_counter()
    dt = t1 - t0
    if dt > 0: best = max(best, (2*n)/dt/1e9)
print("%.1f" % best)
PY
)
  else
    SYN_METHOD="no disponible (falta python3)"
  fi
  echo "Metodo : $SYN_METHOD  (bloques de ${BLOCK_MB} MB, trafico = lectura + escritura)"
  echo "INDICE DE MEMORIA : ${SYN_GBS} GB/s"
  echo "  NOTA: copia de UN solo hilo. No es el pico de la maquina (queda entre"
  echo "        un tercio y la mitad), pero es el MISMO test en todas, asi que"
  echo "        sirve para ordenarlas cuando no hay Ollama para medir de verdad."
else
  echo "omitida (--no-bench)"
fi

# ---------------------------------------------------------------------- RED
sec "7. RED"
if [ "$OSKIND" = "macos" ]; then
  for i in $(networksetup -listallhardwareports 2>/dev/null | awk '/Device/{print $2}'); do
    ip=$(ipconfig getifaddr "$i" 2>/dev/null); [ -n "$ip" ] && echo "- $i : $ip"
  done
else
  ip -4 -o addr show 2>/dev/null | awk '$4 !~ /^127/{print "- "$2" : "$4}'
fi
LISTEN=""
if command -v lsof >/dev/null 2>&1; then
  LISTEN=$(lsof -nP -iTCP:11434 -sTCP:LISTEN 2>/dev/null | tail -n +2 | awk '{print $9}' | head -1)
elif command -v ss >/dev/null 2>&1; then
  LISTEN=$(ss -ltnH 2>/dev/null | awk '$4 ~ /:11434$/{print $4}' | head -1)
fi
echo "Puerto 11434: ${LISTEN:-no escucha}"

# ------------------------------------------------------------------ ENERGIA
sec "8. ENERGIA Y TERMICA"
BATT=false
if [ "$OSKIND" = "macos" ]; then
  pmset -g batt 2>/dev/null | grep -q InternalBattery && BATT=true
else
  [ -d /sys/class/power_supply/BAT0 ] && BATT=true
fi
if [ "$BATT" = "true" ]; then
  echo "Laptop (bateria presente)."
  echo "  NOTA: el throttling termico recorta 30-50% del rendimiento sostenido"
  echo "        en cargas largas. Mala anfitriona para trabajos de horas."
else
  echo "Equipo de escritorio o servidor: sin bateria, sin techo termico por energia."
fi

# ----------------------------------------------------------------- JSON OUT
if [ -n "$JSON_OUT" ]; then
  cat > "$JSON_OUT" <<JSON
{
  "schema": "$SCHEMA",
  "timestamp": "$(date -u '+%Y-%m-%dT%H:%M:%SZ')",
  "host": { "name": "$(jstr "$HOSTN")", "os": "$OSKIND", "os_version": "$(jstr "$OSVER")", "arch": "$ARCH" },
  "cpu": { "model": "$(jstr "$CPU_MODEL")", "cores_physical": $(n0 "$CPU_PHY"), "cores_logical": $(n0 "$CPU_LOG") },
  "memory": { "total_gb": $(n0 "$RAM_GB"), "speed_mts": $(n0 "$MEM_SPEED"), "bus_bits": $(n0 "$MEM_BUS"), "bandwidth_theoretical_gbs": $(n0 "$BW_THEO") },
  "gpu": { "name": "$(jstr "$GPU_NAME")", "vendor": "$GPU_VENDOR", "vram_gb": $(n0 "$GPU_VRAM") },
  "accelerator": { "name": "$(jstr "$NPU_NAME")", "usable_by_ollama": false },
  "inference": {
    "ollama_present": $OL_PRESENT,
    "model": "$(jstr "$OL_MODEL")",
    "model_size_gb": $(n0 "$OL_SIZE"),
    "processor": "$(jstr "$PROCESSOR")",
    "prompt_eval_tps": $(n0 "$PP_TPS"),
    "generation_tps": $(n0 "$GEN_TPS"),
    "effective_bandwidth_gbs": $(n0 "$EFF_BW"),
    "efficiency_pct": $(n0 "$EFF_PCT")
  },
  "synthetic": { "memcpy_gbs": $(n0 "$SYN_GBS"), "method": "$(jstr "$SYN_METHOD")" },
  "power": { "battery_present": $BATT }
}
JSON
  printf '\nRegistro comparable escrito en: %s\n' "$JSON_OUT"
fi

printf '\n== FIN ==\n'
