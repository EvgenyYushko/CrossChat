FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

ARG GITHUB_USER
ARG GITHUB_TOKEN

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# 1. Копируем csproj файлы, сохраняя структуру папок
# Контекст сборки будет в КОРНЕ репозитория (снаружи папки CrossChat)

COPY ["CrossChat/CrossChat.csproj", "CrossChat/"]
COPY ["CrossChat.Worker/CrossChat.Worker.csproj", "CrossChat.Worker/"]
COPY ["CrossChat.Data/CrossChat.Data.csproj", "CrossChat.Data/"]
COPY ["CrossChat.Integrations/CrossChat.Integrations.csproj", "CrossChat.Integrations/"]

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
ENTRYPOINT ["dotnet", "CrossChat.dll"]