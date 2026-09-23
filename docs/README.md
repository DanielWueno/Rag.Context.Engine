# Documentación de RagEngine

Documentación formal del sistema, organizada por audiencia y profundidad.
El plan de proyecto interno (diseño histórico, roadmap y riesgos) vive en la raíz como `Fase 1..5 - *.md`; estos documentos describen **el sistema tal como es hoy**.

## Índice

| Documento | Audiencia | Contenido |
|---|---|---|
| [como-funciona.md](como-funciona.md) | Ingeniería (incorporación) | Recorrido explicado del sistema con diagramas: vocabulario, los tres proyectos, qué pasa en una ingesta y en una pregunta, y dónde tocar según lo que quieras cambiar |
| [arquitectura.md](arquitectura.md) | Ingeniería | Componentes del núcleo, capas, contratos, mapa de dependencias y decisiones de diseño |
| [busqueda-hibrida.md](busqueda-hibrida.md) | Ingeniería / IA | Rama densa multilingüe (ONNX/SentencePiece), rama dispersa (normalización ES/EN, TF saturado), fusión RRF y semántica de `min-score` |
| [pipeline-de-ingesta.md](pipeline-de-ingesta.md) | Ingeniería | Flujo productor/consumidores, estrategias de chunking, filtros de calidad, características de rendimiento medidas |
| [guia-cli.md](guia-cli.md) | Usuarios | Referencia completa de `ingest`, `search`, `ask`, `status`, `doctor` y `eval` con ejemplos |
| [configuracion.md](configuracion.md) | Usuarios / Ops | `appsettings.json` campo a campo, gestión de modelos ONNX, matriz de "cuándo re-ingestar" |
| [operaciones.md](operaciones.md) | Ops | Qdrant, caché SQLite de resúmenes, logs estructurados, métricas, troubleshooting conocido y runbook |
| [eval/README.md](eval/README.md) | Ingeniería / IA | Eval-sets de ground-truth, baselines con procedencia, qué invalida una comparación y estado medido de cada colección |
| [reingesta-manual.md](reingesta-manual.md) | Ops | Comandos de re-ingesta por colección, respaldo de Qdrant, cómo verificar que salió bien y cómo medir si movió algo |

## Convenciones

- Los documentos citan código como `ruta/Archivo.cs` y valores medidos indican el hardware de referencia (Apple Silicon, Qdrant local en Docker).
- Cada documento abre con un resumen de una línea y mantiene una sola responsabilidad; los temas transversales se enlazan en lugar de duplicarse.
