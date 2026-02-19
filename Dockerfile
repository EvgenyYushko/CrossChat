FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# 1. Копируем основной проект (он в корне)
COPY ["CrossChat.csproj", "."]

# 2. Копируем проект Воркера
COPY ["CrossChat.Worker/CrossChat.Worker.csproj", "CrossChat.Worker/"]

COPY ["CrossChat.Data/CrossChat.Data.csproj", "CrossChat.Data/"]

# 4. Восстанавливаем зависимости
# Теперь dotnet найдет все три проекта
RUN dotnet restore "./CrossChat.csproj"

# 5. Копируем все остальные файлы исходного кода
COPY . .

# 6. Сборка
WORKDIR "/src/."
RUN dotnet build "CrossChat.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "CrossChat.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CrossChat.dll"]