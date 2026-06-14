FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["PrivacyProxy.Api/PrivacyProxy.Api.csproj", "PrivacyProxy.Api/"]
RUN dotnet restore "PrivacyProxy.Api/PrivacyProxy.Api.csproj"
COPY . .
WORKDIR "/src/PrivacyProxy.Api"
RUN dotnet build "./PrivacyProxy.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./PrivacyProxy.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PrivacyProxy.Api.dll"]
