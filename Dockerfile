# Correos Velsat: build desde la raíz del repositorio
#   docker build -t correos-velsat .

# ---------- Etapa 1: compilar ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restaurar primero (capa cacheada mientras no cambie el .csproj)
COPY correos-velsat/correos-velsat.csproj correos-velsat/
RUN dotnet restore correos-velsat/correos-velsat.csproj

COPY correos-velsat/ correos-velsat/
RUN dotnet publish correos-velsat/correos-velsat.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---------- Etapa 2: ejecutar ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# tzdata para TZ=America/Lima y curl para el HEALTHCHECK
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata curl \
    && rm -rf /var/lib/apt/lists/*

ENV TZ=America/Lima \
    # Escucha en 0.0.0.0:8080 dentro del contenedor
    ASPNETCORE_HTTP_PORTS=8080 \
    Logging__LogLevel__Default=Information \
    Logging__LogLevel__Microsoft.AspNetCore=Warning

WORKDIR /app
COPY --from=build /app/publish .

# Usuario sin privilegios que trae la imagen oficial (UID 1654)
USER $APP_UID

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "correos-velsat.dll"]
