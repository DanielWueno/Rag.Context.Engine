# Guía de Desarrollo con Claude

@AGENTS.md

El protocolo compartido vive en `AGENTS.md`; este archivo conserva el contexto
técnico y la entrada nativa de Claude. No mantiene una segunda copia del flujo.

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
- **infra/** — Configuración de infraestructura (Docker, modelos, descargas)

## Plan de Ingeniería

El proyecto sigue un plan estructurado en olas (Ola 1: red de seguridad, Ola 2: deuda estructural, Ola 3: usabilidad, etc.).

El estado del plan vive en **docs/analisis-futuro/ejecucion-plan.estado.json** (ledger).

Ejecutar un ítem en Claude: `/arnes-plan:plan-siguiente [id-ítem]`

Revisar estado: `/arnes-plan:plan-estado` → consulta el ledger actual.

## Verificación

Comandos disponibles; elegir los necesarios según el criterio del ítem. Los
conteos de tests y colecciones se consultan, no se asumen a partir de esta guía.

```bash
dotnet build           # Debe compilar sin errores ni advertencias
dotnet test
dotnet run --project src/RagEngine.Cli -- doctor           # Diagnóstico local
dotnet run --project src/RagEngine.Cli -- status           # Estado real de colecciones
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
4. **Caché de resúmenes:** clave `(content_hash, prompt_version)`; contar misses antes de re-ingestar. Cambio de prompt_version puede exigir regeneración completa (~19 h históricas).
5. **Multilingüismo:** consulta en ES/EN contra cualquier corpus, soporte simétrico

## Flujo y selección de modelos

Seguir `AGENTS.md` y, dentro del plugin de Claude, su
`commands/plan-siguiente.md`. Los valores `haiku`, `sonnet` y `opus` del ledger
siguen siendo los que usa el plugin. La tabla de candidatos Copilot de `AGENTS.md`
no cambia la selección nativa de Claude.

## Contacto y Problemas

Ver [docs/README.md](docs/README.md) para troubleshooting.
