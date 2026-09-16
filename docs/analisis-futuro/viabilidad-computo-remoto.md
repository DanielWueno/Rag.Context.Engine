# Viabilidad de cómputo remoto para inferencia (Dell Pro 16 vs. Mac mini M4 Pro)

Fecha: 2026-09-03 · Estado: análisis previo, sin cambios en código

> **Estado reconciliado, 2026-09-12:** propuesta pendiente/diferida en los ítems
> `16.1-comparar-capacidad-remota` y `16.2-ollama-remoto-reversible` del ledger;
> la topología empresarial se registra en `16.3-topologia-empresarial-diferida`.
> Ninguna bloquea el trabajo local. Las recomendaciones históricas de abrir Ollama a
> la LAN no son autorización de ejecución: cualquier prueba futura exige aprobación,
> transporte protegido y retorno local. `infra/bench/` se conserva sin modificaciones.
> **Relevo del 2026-09-14:** ese directorio era, hasta entonces, un prototipo local
> sin versionar, excluido de los commits del motor y del ledger.
> **Reconciliación del 2026-09-15:** a pedido explícito del usuario, los 5 archivos
> de código/doc (`README.md`, `capacidad-ia.sh`, `capacidad-ia.ps1`,
> `comparar-capacidad.py`, `maquinas/.gitignore`) se versionaron sin modificarlos;
> los datos por máquina (`maquinas/*.json`, `*.txt`, `*.html`) siguen fuera del
> repo. Versionarlo NO lo convierte en una herramienta de aceptación ya aprobada
> ni avanza `16.1`, que sigue bloqueado por sus propias condiciones de reentrada
> (decisión explícita, permisos, candidatos autorizados, `14.1-eval-nocturno`
> hecho). Detalle completo en la nota `_artefactos_previos_sin_versionar` de
> `16.1`.

## 1. Pregunta

¿Es viable tomar prestado el cómputo de una Dell Pro 16 con "hardware dedicado
de IA" (NPU) para descargar la inferencia del motor RAG, hoy 100 % local en el
Mac mini M4 Pro?

## 2. Hallazgo estructural: el pipeline no es una sola cosa

El proyecto tiene **dos** consumidores de cómputo con portabilidad opuesta:

| Componente | Backend | ¿Remotable? | Evidencia |
|---|---|---|---|
| Generación de respuestas | Ollama vía HTTP | **Sí**, un renglón | `Ollama:Endpoint` en `appsettings.json` |
| Resúmenes de negocio (~19 h) | Ollama vía HTTP | **Sí**, el mismo renglón | `OllamaBusinessSummaryGenerator.cs` |
| Embeddings (`OnnxBrain`) | ONNX Runtime en proceso | **No** | `Microsoft.ML.OnnxRuntime` en `RagEngine.Core.csproj` |
| Rerank (`CrossEncoder`) | ONNX Runtime en proceso | **No** | ídem |

Consecuencia: "prestar potencia" sólo puede significar **descargar la generación
LLM**. Los embeddings y el rerank viven dentro del proceso .NET y además usan
modelos `model_qint8_arm64.onnx`, cuantizados para ARM64. Se quedan en el Mac.

No es un premio menor: el trabajo de 19 h de regeneración de la caché de
resúmenes es precisamente la mitad remotable.

## 3. El NPU no sirve para este proyecto

Ollama y llama.cpp no tienen backend de NPU. Sus backends son Metal, CUDA, ROCm,
Vulkan y CPU. El NPU de Intel sólo es accesible por OpenVINO / DirectML / Windows ML.

Verificado en el lado Mac: con el modelo cargado, `ollama ps` reporta
`100% GPU`. El Neural Engine de 16 núcleos del M4 Pro está sin usar. El NPU de
la Dell quedaría igual de ocioso.

**Los TOPS del NPU no se traducen en tokens/s aquí.** Es el dato de marketing
que motivó la pregunta y es el que no aplica.

## 4. El predictor real es el ancho de banda de memoria

La generación de tokens es *memory-bound*: cada token exige recorrer los pesos
del modelo. Por eso `tok/s ≈ ancho_de_banda / tamaño_del_modelo`.

Medición en el Mac mini M4 Pro (273 GB/s de spec):

| Modelo | Tamaño | Medido | BW efectivo | % del teórico |
|---|---|---|---|---|
| qwen2.5-coder 7B q4 | 4,7 GB | 47,8 tok/s | ~225 GB/s | 82 % |
| qwen2.5-coder 14B q4 | 9,0 GB | 25,6 tok/s | ~230 GB/s | 84 % |

Dos puntos independientes caen en la misma constante: el modelo predictivo es
sólido y sirve para estimar la Dell antes de tocarla.

Escenarios para la Dell, según lo que revele el diagnóstico:

| Escenario | Ancho de banda | 7B estimado | Veredicto |
|---|---|---|---|
| NVIDIA RTX discreta (GDDR6) | 256–576 GB/s | 50–110 tok/s | **Gana**, pero limitado por VRAM (8–16 GB) |
| iGPU Intel Arc, LPDDR5x-8533 | ~136 GB/s | ~28 tok/s | Mitad del Mac |
| Sólo CPU (Ollama no toma la iGPU) | ~136 GB/s, mal aprovechado | 8–12 tok/s | Peor que el Mac |

**Toda la decisión se reduce a una pregunta:** ¿esa Dell trae GPU NVIDIA
discreta, o sólo iGPU Intel + NPU? La sección 3 del script lo responde.

## 5. La red no es el cuello de botella

El tráfico LLM es texto. Un prompt de 4 000 tokens son ~16 KB; las respuestas,
menos. Sobre ethernet gigabit (125 MB/s, latencia LAN de 1–5 ms) el sobrecosto
es despreciable frente a generar a 25–50 tok/s. Este punto es favorable.

## 6. Los obstáculos reales no son técnicos

1. **Es la herramienta de trabajo de otra persona.** 19 h de GPU/CPU al 100 %
   dejan la laptop caliente, con ventiladores al máximo y degradada para su
   dueño. Una laptop además hace *throttling* térmico: pierde 30–50 % del
   rendimiento sostenido justo en los trabajos largos.
2. **Una laptop se suspende, se cierra y se va a casa.** El trabajo de 19 h
   necesita disponibilidad continua. La forma del equipo es la equivocada.
3. **Ollama no tiene autenticación.** Exponer el puerto 11434 en la LAN
   corporativa deja un servidor de inferencia abierto a cualquiera en la red.
   Es una conversación con IT, no un cambio de configuración.
4. **Gobernanza del dato.** Los prompts llevan fragmentos del código y de la
   documentación de la empresa, y viajarían en HTTP plano hacia la máquina de
   otro empleado.

## 7. Alternativa más fuerte: invertir la dirección

El Mac mini es un equipo de escritorio: siempre encendido, en ethernet, sin
batería ni techo térmico, 273 GB/s y 24 GB unificados. Es **mejor servidor de
inferencia que cualquier laptop**, incluida la Dell en su mejor escenario
razonable.

El problema declarado —"si salto a mi computadora Win, aún no se prepara"— se
resuelve sirviendo *desde* el Mac hacia la Windows, no tomando prestada la Dell.
Hoy `ollama` escucha sólo en `127.0.0.1:11434`; basta `OLLAMA_HOST=0.0.0.0`
más una regla de firewall.

## 8. Complejidad y tiempos

| Acción | Esfuerzo | Riesgo |
|---|---|---|
| Correr el diagnóstico en ambas máquinas | 15 min | Nulo |
| Apuntar el proyecto a un Ollama remoto | 1 renglón + verificación, ~30 min | Bajo, reversible |
| Exponer el Mac como servidor para la Windows | ~30 min | Bajo |
| Aprobación de IT para usar la Dell | días–semanas | Proceso humano, no ingeniería |
| Portar embeddings/rerank fuera del Mac | Días. Reexportar ONNX a x86 y partir el proceso | Alto, **no recomendado** |

## 9. Recomendación

1. Correr el arnés de `infra/bench/` en cada máquina candidata y comparar con
   `comparar-capacidad.py`. Sin ese dato todo lo demás es especulación. El arnés
   es genérico: no fija modelo ni hardware, así que admite máquinas nuevas sin
   tocarlo. Ver `infra/bench/README.md`.
2. Si la Dell **no** tiene NVIDIA discreta: descartar la idea. Se ganaría menos
   que el Mac actual, a cambio de deuda social y de seguridad.
3. Si **sí** la tiene: sigue siendo mala anfitriona para el trabajo de 19 h por
   térmica y disponibilidad; considerarla sólo para ráfagas cortas.
4. En paralelo y sin depender de nadie: exponer el Mac mini como servidor de
   inferencia de la LAN. Resuelve el caso Windows hoy.
