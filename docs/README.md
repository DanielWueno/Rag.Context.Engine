# Documentación de RagEngine

Documentación formal del sistema, organizada por audiencia y profundidad.
El plan de proyecto interno (diseño histórico, roadmap y riesgos) vive en la raíz como `Fase 1..5 - *.md`; estos documentos describen **el sistema tal como es hoy**.

## Índice

| Documento | Audiencia | Contenido |
|---|---|---|
| [arquitectura.md](arquitectura.md) | Ingeniería | Componentes del núcleo, capas, contratos, mapa de dependencias y decisiones de diseño |
| [busqueda-hibrida.md](busqueda-hibrida.md) | Ingeniería / IA | Rama densa multilingüe (ONNX/SentencePiece), rama dispersa (normalización ES/EN, TF saturado), fusión RRF y semántica de `min-score` |
| [pipeline-de-ingesta.md](pipeline-de-ingesta.md) | Ingeniería | Flujo productor/consumidores, estrategias de chunking, filtros de calidad, características de rendimiento medidas |
| [guia-cli.md](guia-cli.md) | Usuarios | Referencia completa de `ingest`, `search`, `ask`, `status`, `doctor` con ejemplos |
| [configuracion.md](configuracion.md) | Usuarios / Ops | `appsettings.json` campo a campo, gestión de modelos ONNX, matriz de "cuándo re-ingestar" |
| [operaciones.md](operaciones.md) | Ops | Qdrant, logs estructurados, métricas, troubleshooting conocido y runbook |

## Convenciones

- Los documentos citan código como `ruta/Archivo.cs` y valores medidos indican el hardware de referencia (Apple Silicon, Qdrant local en Docker).
- Cada documento abre con un resumen de una línea y mantiene una sola responsabilidad; los temas transversales se enlazan en lugar de duplicarse.
