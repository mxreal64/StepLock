# ==========================================
# Multi-Stage Production Dockerfile (.NET 10/11)
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# Copy project manifests and restore dependencies
COPY ["src/DeterministicProxy.Core/DeterministicProxy.Core.csproj", "src/DeterministicProxy.Core/"]
COPY ["src/DeterministicProxy.Engine/DeterministicProxy.Engine.csproj", "src/DeterministicProxy.Engine/"]
COPY ["src/DeterministicProxy.Storage/DeterministicProxy.Storage.csproj", "src/DeterministicProxy.Storage/"]
COPY ["src/DeterministicProxy.Gateway/DeterministicProxy.Gateway.csproj", "src/DeterministicProxy.Gateway/"]
RUN dotnet restore "src/DeterministicProxy.Gateway/DeterministicProxy.Gateway.csproj"

# Copy source and publish optimized release build
COPY . .
WORKDIR "/src/src/DeterministicProxy.Gateway"
RUN dotnet publish "DeterministicProxy.Gateway.csproj" -c Release -o /app/publish \
    --no-restore \
    -p:PublishReadyToRun=true

# ==========================================
# Runtime Stage (Distroless/Alpine Minimal)
# ==========================================
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS final
WORKDIR /app
EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DatabasePath=/data/execution_traces.db

# Persistent volume for SQLite traces
VOLUME /data

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DeterministicProxy.Gateway.dll"]
