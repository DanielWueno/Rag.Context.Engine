# Arnés de capacidad de inferencia

Mide qué tan rápido puede generar tokens una máquina cualquiera y produce un
registro comparable con las demás. Sirve para decidir dónde conviene alojar la
inferencia del motor RAG antes de mover nada.

## Piezas

| Archivo | Dónde corre | Qué hace |
|---|---|---|
| `capacidad-ia.sh` | macOS y Linux | Informe legible + registro JSON |
| `capacidad-ia.ps1` | Windows | Espejo exacto del anterior |
| `comparar-capacidad.py` | Donde sea que juntes los JSON | Ordena N máquinas |

Ninguno instala nada ni descarga modelos. Sólo leen y miden.

## Uso

```bash
# En cada máquina: JSON para comparar + informe legible para mandar
./capacidad-ia.sh --json maquinas/equipo.json --out equipo.txt
powershell -ExecutionPolicy Bypass -File .\capacidad-ia.ps1 -Json equipo.json -Out equipo.txt

# Juntando los JSON en un solo lugar
python3 comparar-capacidad.py maquinas/*.json --html informe.html
```

| Opción | Efecto |
|---|---|
| `--json` / `-Json` | Registro comparable, el que consume el comparador |
| `--out` / `-Out` | Informe legible a archivo, **sin perderlo de la consola** |
| `--no-bench` / `-NoBench` | Omite toda medición, sólo lee specs |
| `--html` (comparador) | Informe autocontenido para abrir con doble clic o mandar |

Nada de copiar y pegar del scrollback: `--out` guarda el mismo texto que ves, y
`--html` produce una página sin recursos externos, con tema claro y oscuro, que
se imprime bien a PDF. En Windows `-Out` usa `Start-Transcript`, que captura
también lo que sale por `Write-Host`.

## La métrica

El número que ordena las máquinas es el **ancho de banda efectivo**:

```
GB/s efectivo = tamaño_del_modelo_GB  x  tokens/s de generación
```

La generación de tokens es *memory-bound*: cada token recorre los pesos del
modelo completo. Por eso los tok/s crudos no se pueden comparar entre máquinas
que corrieron modelos distintos, pero los GB/s sí.

Verificado en el Mac mini M4 Pro (273 GB/s de spec): 4,68 GB × 49,1 tok/s =
229,7 GB/s, o sea 84 % del teórico. Con el modelo de 14B daba 230 GB/s. Dos
modelos distintos, la misma constante.

Por eso el script **no fija un modelo**: usa el más pequeño que ya esté
instalado y normaliza. Máquina nueva, cero configuración.

La **eficiencia** (efectivo ÷ teórico) es el segundo dato útil: delata cuando
Ollama se cayó a CPU o cuando la GPU no se está usando, sin que tengas que
saber nada del hardware de antemano.

## Cuando no hay Ollama

Cae a una **prueba sintética**: copia de memoria de un solo hilo, con el mismo
tamaño de bloque en todas las plataformas. `comparar-capacidad.py` proyecta ese
número a GB/s efectivos usando la razón observada en las máquinas que sí tienen
ambas medidas, y marca el resultado con `~`.

Límite honesto: es de un solo hilo, así que queda entre un tercio y la mitad del
pico real de la máquina. Y entre la implementación de Python y la de .NET hay
hasta 15 % de diferencia medida sobre el mismo equipo. Sirve para **ordenar por
categoría**, no para separar dos máquinas parecidas. Cuando hay Ollama, manda el
ancho de banda efectivo, que sí concuerda entre ambas implementaciones (0,04 %
de diferencia medida).

## Lo que el arnés deja claro a propósito

El NPU de una laptop Copilot+ y el Neural Engine de Apple **no participan**.
Ollama y llama.cpp no tienen backend de NPU: sus backends son Metal, CUDA, ROCm,
Vulkan y CPU. Los TOPS del folleto no se traducen en tokens/s. La sección 4 de
cada script lo reporta y el comparador lo levanta como advertencia.

## Contrato JSON

Esquema `capacidad-ia/1`. Bloques: `host`, `cpu`, `memory`, `gpu`,
`accelerator`, `inference`, `synthetic`, `power`. El comparador rechaza
cualquier archivo con otro `schema`, así que si se cambia la forma hay que
subir la versión.

## Reglas al editar `capacidad-ia.ps1`

Dos que ya costaron un bug cada una:

1. **ASCII puro y CRLF.** Windows PowerShell 5.1 lee los `.ps1` como ANSI, no
   UTF-8. Una raya larga `—` se decodifica como `â€"`, y ese último byte es una
   comilla tipográfica que PowerShell toma como delimitador de cadena: el parser
   se desincroniza y reporta errores en líneas que no tienen nada malo.
2. **Nada de funciones de una letra.** Los alias ganan a las funciones en la
   resolución de comandos: `H` es alias de `Get-History`, así que `function H`
   nunca se llama. Con `$ErrorActionPreference = "SilentlyContinue"` falla en
   silencio. Verificar con `Get-Command <nombre>`.

Se puede validar sin Windows: `dotnet tool install --global PowerShell` deja
`pwsh`, y `[Parser]::ParseFile(...)` detecta los errores de sintaxis.

## Verificación hecha

- `capacidad-ia.sh`: ejecutado en macOS 26.5.1 (M4 Pro) y en Linux dentro de
  contenedor, tanto mínimo (sin `lspci`/`dmidecode`/`bc`) como completo.
- `capacidad-ia.ps1`: parseo sin errores y ejecución completa bajo pwsh 7.6.5.
  Los cmdlets `Win32_*` y `Get-Net*` no existen en macOS, así que esa parte sólo
  se ejerció en flujo de control, no con datos reales.
- Concordancia entre ambas implementaciones sobre la misma máquina: mismo modelo
  elegido, misma generación (49,1 tok/s), ancho de banda efectivo 229,7 vs 229,8.
