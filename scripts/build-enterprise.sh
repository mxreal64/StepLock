#!/usr/bin/env bash
set -e
echo "Building StepLock Enterprise Edition (Full Suite)..."
dotnet build DeterministicProxy.sln -c Release
echo "StepLock Enterprise built successfully!"
