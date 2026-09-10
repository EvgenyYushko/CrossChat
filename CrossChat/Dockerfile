FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# --- УСТАНОВКА FFMPEG ---
USER root
RUN apt-get update && \
    apt-get install -y --no-install-recommends ca-certificates ffmpeg && \
    rm -rf /var/lib/apt/lists/*
USER $APP_UID
# ------------------------

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

ARG GITHUB_USER
ARG GITHUB_TOKEN

# 1. Копируем ВСЕ csproj файлы (включая Infrastructure!)
COPY ["CrossChat/CrossChat.csproj", "CrossChat/"]
COPY ["CrossChat.Worker/CrossChat.Worker.csproj", "CrossChat.Worker/"]
COPY ["CrossChat.Data/CrossChat.Data.csproj", "CrossChat.Data/"]
COPY ["CrossChat.Integrations/CrossChat.Integrations.csproj", "CrossChat.Integrations/"]
COPY ["CrossChat.Infrastructure/CrossChat.Infrastructure.csproj", "CrossChat.Infrastructure/"]

RUN dotnet nuget add source "https://nuget.pkg.github.com/EvgenyYushko/index.json" --name "github" --username "${GITHUB_USER}" --password "${GITHUB_TOKEN}" --store-password-in-clear-text

# 2. Восстанавливаем зависимости
RUN dotnet restore "CrossChat/CrossChat.csproj"

# 3. Копируем всё остальное
COPY . .

# 4. Собираем
WORKDIR "/src/CrossChat"
RUN dotnet build "CrossChat.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "CrossChat.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# --- ВЫДАЁМ ПРАВА ДОСТУПА ПОЛЬЗОВАТЕЛЮ APP НА ВСЮ ПАПКУ /app ---
USER root
RUN mkdir -p /app/wwwroot/temp_audio /app/wwwroot/temp_media && \
    chown -R app:app /app && \
    chmod -R 775 /app
USER $APP_UID
# -------------------------------------------------------------

ENTRYPOINT ["dotnet", "CrossChat.dll"]