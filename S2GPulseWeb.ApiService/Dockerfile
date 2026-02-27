FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy project files
COPY ["S2GPulseWeb.ApiService/S2GPulseWeb.ApiService.csproj", "S2GPulseWeb.ApiService/"]
COPY ["S2GPulseWeb.ServiceDefaults/S2GPulseWeb.ServiceDefaults.csproj", "S2GPulseWeb.ServiceDefaults/"]

# Restore dependencies
RUN dotnet restore "S2GPulseWeb.ApiService/S2GPulseWeb.ApiService.csproj"

# Copy everything else
COPY . .

# Build
WORKDIR "/src/S2GPulseWeb.ApiService"
RUN dotnet build "S2GPulseWeb.ApiService.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "S2GPulseWeb.ApiService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "S2GPulseWeb.ApiService.dll"]
