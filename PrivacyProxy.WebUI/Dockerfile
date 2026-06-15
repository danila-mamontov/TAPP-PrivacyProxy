FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# Create the shared config mount point owned by the non-root app user so the WebUI can
# write to the named volume (Docker seeds a new volume's ownership from this directory).
RUN mkdir -p /config && chown $APP_UID /config
USER $APP_UID
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["PrivacyProxy.WebUI/PrivacyProxy.WebUI.csproj", "PrivacyProxy.WebUI/"]
COPY ["PrivacyProxy.Core/PrivacyProxy.Core.csproj", "PrivacyProxy.Core/"]
RUN dotnet restore "PrivacyProxy.WebUI/PrivacyProxy.WebUI.csproj"
COPY . .
WORKDIR "/src/PrivacyProxy.WebUI"
RUN dotnet build "./PrivacyProxy.WebUI.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./PrivacyProxy.WebUI.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PrivacyProxy.WebUI.dll"]
