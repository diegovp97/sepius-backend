# ── Sepius Recorder ──────────────────────────────────────────────────────────
# El grabador: ffmpeg + streamlink + yt-dlp y los workers que detectan los directos.
# Se construye desde la raíz del repo:  docker compose build sepius-recorder

# ── ETAPA 1: BUILD ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Caché: primero solo los .csproj para restaurar dependencias
COPY ["src/Sepius.Recorder/Sepius.Recorder.csproj", "src/Sepius.Recorder/"]
COPY ["src/Sepius.Application/Sepius.Application.csproj", "src/Sepius.Application/"]
COPY ["src/Sepius.Domain/Sepius.Domain.csproj", "src/Sepius.Domain/"]
COPY ["src/Sepius.Infrastructure/Sepius.Infrastructure.csproj", "src/Sepius.Infrastructure/"]
RUN dotnet restore "src/Sepius.Recorder/Sepius.Recorder.csproj"

COPY . .
RUN dotnet publish "src/Sepius.Recorder/Sepius.Recorder.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore

# ── ETAPA 2: RUNTIME ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# streamlink, yt-dlp y ffmpeg (muxing a HLS y .mp4). curl para el healthcheck.
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 \
    python3-pip \
    ffmpeg \
    curl \
    && pip3 install streamlink yt-dlp --break-system-packages \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /recordings

COPY --from=build /app/publish .

EXPOSE 9090

ENTRYPOINT ["dotnet", "Sepius.Recorder.dll"]
