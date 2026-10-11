#!/usr/bin/env bash
set -e
echo "Building StepLock Community Core (Open Source)..."
dotnet build src/DeterministicProxy.Gateway/DeterministicProxy.Gateway.csproj -c Release
echo "Community Core built successfully!"
