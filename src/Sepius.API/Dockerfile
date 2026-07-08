# ── ETAPA 1: BUILD ──────────────────────────────────────────────────────────
# Usar la imagen del SDK de .NET (compilador + herramientas)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# OPTIMIZACIÓN DE CACHÉ: Copiar primero solo los .csproj y restaurar dependencias.
# Si el código cambia pero los .csproj no, Docker reutiliza esta capa cacheada.
# Equivale a copiar package.json antes que el resto del código en Node.
COPY ["src/Sepius.API/Sepius.API.csproj", "src/Sepius.API/"]
COPY ["src/Sepius.Application/Sepius.Application.csproj", "src/Sepius.Application/"]
COPY ["src/Sepius.Domain/Sepius.Domain.csproj", "src/Sepius.Domain/"]
COPY ["src/Sepius.Infrastructure/Sepius.Infrastructure.csproj", "src/Sepius.Infrastructure/"]
RUN dotnet restore "src/Sepius.API/Sepius.API.csproj"

# Copiar el resto del código fuente y compilar
COPY . .
RUN dotnet publish "src/Sepius.API/Sepius.API.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore

# ── ETAPA 2: RUNTIME ─────────────────────────────────────────────────────────
# Imagen mucho más pequeña: solo el runtime de ASP.NET, sin el SDK completo.
# Equivale a una imagen de producción de Node sin devDependencies.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Instalar streamlink y ffmpeg (dependencia de streamlink para muxing a .mp4)
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 \
    python3-pip \
    ffmpeg \
    curl \
    && pip3 install streamlink yt-dlp --break-system-packages \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Crear directorio de grabaciones (será sobreescrito por el volumen Docker)
RUN mkdir -p /recordings

# Copiar el artefacto publicado desde la etapa de build
COPY --from=build /app/publish .

# Puerto que expone la API (configurado vía ASPNETCORE_URLS)
EXPOSE 8080

ENTRYPOINT ["dotnet", "Sepius.API.dll"]
