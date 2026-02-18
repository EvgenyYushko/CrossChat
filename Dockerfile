FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# --- ИСПРАВЛЕНИЕ ЗДЕСЬ ---
# 1. Копируем основной проект.
# Так как он лежит в КОРНЕ, мы копируем его в корень контейнера ("."), а не в папку CrossChat/
COPY ["CrossChat.csproj", "."]

# 2. Копируем проект Воркера.
# Он лежит в папке, поэтому копируем папку в папку.
COPY ["CrossChat.Worker/CrossChat.Worker.csproj", "CrossChat.Worker/"]

# 3. Восстанавливаем зависимости
RUN dotnet restore "./CrossChat.csproj"

# 4. Копируем все остальные файлы
COPY . .

# 5. Сборка (мы уже в корне /src, никуда переходить не надо)
WORKDIR "/src/."
RUN dotnet build "CrossChat.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "CrossChat.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CrossChat.dll"]