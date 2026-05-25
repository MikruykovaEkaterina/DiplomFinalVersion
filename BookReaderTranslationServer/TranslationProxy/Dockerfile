# Сборка и запуск TranslationProxy в Docker (рядом с LibreTranslate в docker-compose).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY TranslationProxy.csproj .
RUN dotnet restore TranslationProxy.csproj
COPY . .
RUN dotnet publish TranslationProxy.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TranslationProxy.dll"]
