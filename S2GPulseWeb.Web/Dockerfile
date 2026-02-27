FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy project files
COPY ["S2GPulseWeb.Web/S2GPulseWeb.Web.csproj", "S2GPulseWeb.Web/"]
COPY ["S2GPulseWeb.ServiceDefaults/S2GPulseWeb.ServiceDefaults.csproj", "S2GPulseWeb.ServiceDefaults/"]

# Restore dependencies
RUN dotnet restore "S2GPulseWeb.Web/S2GPulseWeb.Web.csproj"

# Copy everything else
COPY . .

# Build
WORKDIR "/src/S2GPulseWeb.Web"
RUN dotnet build "S2GPulseWeb.Web.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "S2GPulseWeb.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app

# Use Docker environment profile (loads appsettings.Docker.json automatically)
ENV ASPNETCORE_ENVIRONMENT=Docker
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=publish /app/publish .
COPY --from=build /src/workflow-samples /app/workflow-samples
COPY --from=build /src/custom-nodes /app/custom-nodes

HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
  CMD wget -qO- http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "S2GPulseWeb.Web.dll"]
