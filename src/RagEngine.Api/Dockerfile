# Build context is the repo ROOT (not this folder) — this Dockerfile needs
# access to src/RagEngine.Core, which RagEngine.Api references as a sibling
# project. See infra/docker-compose.yml's `build.context: ..`.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy just the .csproj files first so `dotnet restore` is cached across
# rebuilds unless a dependency actually changes.
COPY src/RagEngine.Core/RagEngine.Core.csproj src/RagEngine.Core/
COPY src/RagEngine.Api/RagEngine.Api.csproj src/RagEngine.Api/
RUN dotnet restore src/RagEngine.Api/RagEngine.Api.csproj

COPY src/RagEngine.Core/ src/RagEngine.Core/
COPY src/RagEngine.Api/ src/RagEngine.Api/
RUN dotnet publish src/RagEngine.Api/RagEngine.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl: la única dependencia extra que pide el HEALTHCHECK de abajo (la imagen
# base no trae curl ni wget). Se limpian las listas de apt para no dejar esa
# capa más pesada de lo necesario. Ítem 10.6.3 (porte del 21.1 local).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Directorio de la caché SQLite de resúmenes (infra/docker-compose.yml monta aquí
# un volumen nombrado). Se crea y se da dueño ANTES de montar el volumen a
# propósito: Docker siembra un volumen nombrado vacío con el contenido y los
# permisos que ya tenía la ruta en la imagen — sin esto, el volumen nacería
# root:root y el usuario no-root de abajo no podría crear el archivo .sqlite3.
RUN mkdir -p /data/rag-engine && chown $APP_UID:$APP_UID /data/rag-engine

# $APP_UID ya viene definido en la imagen base aspnet junto con el usuario "app"
# — no correr como root (uid 0) dentro del contenedor (ítem 10.6.3/21.1).
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID

# Kept in sync with the 0.0.0.0:5080 bound in Program.cs (native `dotnet run`
# and the container listen on the same port so docker-compose can map 1:1).
EXPOSE 5080

# Sin -f: un 503 (p. ej. Ollama caído, esperado en el ambiente dev/QA mientras no
# haya Ollama nativo ni perfil servidor) cuenta como "el proceso HTTP responde" —
# no certifica que TODOS los componentes estén arriba, solo que el host vive.
# infra/qa/verificar-ambiente.ps1 sí lee el body completo de /api/health.
HEALTHCHECK --interval=5s --timeout=5s --start-period=15s --retries=10 \
  CMD curl -s -o /dev/null http://127.0.0.1:5080/api/health || exit 1

ENTRYPOINT ["dotnet", "RagEngine.Api.dll"]
