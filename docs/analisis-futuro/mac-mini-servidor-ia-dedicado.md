# Mac mini como servidor de IA dedicado

Fecha: 2026-09-23 · Estado: decisión tomada. El servidor se organiza en el
proyecto propio `Servidor.IA` (repositorio privado `DanielWueno/Servidor.IA`), con
su diseño y su ledger. Aquí queda la ficha del motor
`18.1-contexto-efectivo-de-los-prompts-del-motor`.

## 1. Decisión

El 2026-09-23 el usuario decidió dedicar el Mac mini M4 Pro a servidor de
inferencia LLM para los proyectos y el equipo, exprimiendo su capacidad, con una
configuración automatizada que no dependa de pasos que alguien tenga que recordar.

| Tema | Decisión |
|---|---|
| Alcance del servidor | Solo Ollama y la máquina que lo sostiene |
| Stack del motor | Docker Desktop, Qdrant y `rag-api` dejan de vivir en esta máquina y pasan a la infraestructura de la empresa (nota en `16.3-topologia-empresarial-diferida`) |
| Instalación de Ollama | Fórmula de Homebrew, sin la app: la app se actualiza sola y al abrirse mata cualquier otro proceso `ollama` (`~/.ollama/logs/app.log`) |
| Proyecto | Repositorio propio: el servidor tiene un ciclo de vida distinto del motor y sirve a varios proyectos; el arnés de plan se instala allí con su propio ledger |
| Acceso por red | Construido y apagado hasta la aprobación de TI: Ollama no autentica |

## 2. Qué implica para el motor

- **El motor será cliente remoto del servidor** cuando viva en la infraestructura
  de la empresa. El acceso por red de `Servidor.IA` (TLS, clave por cliente, rutas
  permitidas, Ollama siempre en loopback) cubre el lado servidor de
  `16.2-ollama-remoto-reversible`; las pruebas del cliente remoto (desconexión,
  timeout, reanudación sin duplicar resúmenes, retorno local) siguen en 16.2.
- **Mientras el motor corra en esta máquina no cambia nada**: `Ollama:Endpoint`
  apunta a `http://localhost:11434/v1` y el servidor mantiene Ollama en loopback.
- **Contexto efectivo**: el motor llama al endpoint compatible con OpenAI (`/v1`)
  sin fijar `num_ctx` (no hay coincidencias en `src/`), así que gobierna el valor
  del servidor. El log de Ollama del 2026-09-23 muestra `n_ctx = 4096`. No está
  verificado que los prompts del motor superen ese valor: lo mide la ficha
  `18.1-contexto-efectivo-de-los-prompts-del-motor`, y su resultado es una entrada
  del contexto que calibra `Servidor.IA`.

## 3. Evidencia de partida (2026-09-23)

| Aspecto | Valor | Fuente |
|---|---|---|
| Equipo | Mac mini M4 Pro, 24 GB unificados | `system_profiler SPHardwareDataType` |
| Presupuesto de GPU | 17,8 GiB (`iogpu.wired_limit_mb=0`, por defecto) | log de Ollama, `inference compute` |
| Parámetros efectivos de Ollama | `127.0.0.1:11434`, `NUM_PARALLEL=1`, `KEEP_ALIVE=5m`, `n_ctx=4096` | log de Ollama, `server config` |
| `qwen2.5-coder:14b` cargado | 9,5 GB, 100 % GPU | `ollama ps` |
| `qwen2.5-coder:32b` cargado | 21 GB, 17 % CPU / 83 % GPU | `ollama ps` |

La tabla completa, el diseño de la automatización y las fichas del servidor están
en `Servidor.IA/docs/diseno.md` y `Servidor.IA/docs/plan/ejecucion-plan.estado.json`.
