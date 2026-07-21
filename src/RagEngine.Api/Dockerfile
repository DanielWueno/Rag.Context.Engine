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
COPY --from=build /app .

# Kept in sync with the 0.0.0.0:5080 bound in Program.cs (native `dotnet run`
# and the container listen on the same port so docker-compose can map 1:1).
EXPOSE 5080

ENTRYPOINT ["dotnet", "RagEngine.Api.dll"]
