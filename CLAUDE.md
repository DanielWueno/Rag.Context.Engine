# Guía de Desarrollo con Claude

Este archivo documenta cómo colaborar con este proyecto usando Claude como asistente.

## Setup Local

Sigue [README.md](README.md) para el Quick Start:
- .NET 10 SDK, Docker, Ollama (opcional)
- `docker compose -f infra/docker-compose.yml up -d`
- `bash infra/download-model.sh`
- `dotnet build`

## Estructura del Proyecto

- **docs/** — Documentación formal (arquitectura, configuración, CLI)
- **docs/analisis-futuro/** — Análisis de decisiones y registros
- **src/** — Código de producción (.Core, .Api, .Cli)
- **tests/** — Suite de tests unitarios
- **poc/** — Proof-of-concepts experimentales
- **infra/arnes/** — Arnés de ejecución de planes (protocolo de trabajo)

## Plan de Ingeniería

El proyecto sigue un plan estructurado en olas (Ola 1: red de seguridad, Ola 2: deuda estructural, Ola 3: usabilidad, etc.).

El estado del plan vive en **docs/analisis-futuro/ejecucion-plan.estado.json** (ledger).

Ejecutar un ítem: `/plan-siguiente [id-ítem]`  
Revisar estado: `python3 infra/arnes/ledger_path.py` → lee el JSON actual.

## Verificación

Antes de cambios importantes:

```bash
dotnet build           # Debe compilar sin errores ni advertencias
dotnet test            # 39 tests verdes
rag doctor             # Diagnóstico completo
rag status             # 9 colecciones operativas
```

## Commits y PRs

- **Commits:** Sin `Co-Authored-By` (la herramienta se ve en el mensaje del PR, no en el commit)
- **Mensaje:** una línea que resuma qué y por qué
- **Alcance:** un ítem del plan por commit (se cierra al terminar)
- **Rollback:** documentado en el ítem; preserva el estado anterior

## Decisiones de Diseño Importantes

1. **Reproducibilidad:** baselines con procedencia (commit, config, hash de dataset)
2. **Determinismo:** IDs de chunk basados en contenido; idempotencia en re-ingestas
3. **Tests:** criterio de aceptación mecánico, verificable sin agentes (salvo regresión silenciosa)
4. **Caché de resúmenes:** SQLite versionada; cambio de prompt_version = regeneración completa (~19 h)
5. **Multilingüismo:** consulta en ES/EN contra cualquier corpus, soporte simétrico

## Recorrido Típico de Cambio

1. Entender el alcance (leer el ítem del plan, los docs en `docs/analisis-futuro/`)
2. Compilar y verificar el estado actual (`dotnet build`, `rag doctor`)
3. Implementar y probar localmente (si requiere ingesta, usar micro-repo ~1 min)
4. Ejecutar suite de tests (`dotnet test`)
5. Cerrar el ítem: actualizar ledger, commit
6. Si es Ola 1, verificación es binaria (sí/no); Ola 2+, A/B si hay regresión silenciosa

## Contacto y Problemas

Ver [docs/README.md](docs/README.md) para troubleshooting.
