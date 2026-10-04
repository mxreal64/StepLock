# Contributing to StepLock ⚡

Thank you for your interest in contributing to StepLock! We are building the foundational state infrastructure for autonomous AI agents.

## 🏗 Repository Layout

- `src/DeterministicProxy.Core`: Core abstractions, Merkle hasher, semantic canonicalizer.
- `src/DeterministicProxy.Engine`: Zero-copy pipelines streaming tap, TLS cert engine, WebSocket interceptor.
- `src/DeterministicProxy.Storage`: SQLite WAL mode and in-memory execution store.
- `src/DeterministicProxy.Gateway`: Kestrel edge proxy, control plane API, web dashboard.
- `src/DeterministicProxy.Enterprise`: Proprietary enterprise modules (diffing, cloud archive, virtual timing).
- `sdk/python/steplock`: Official Python SDK.
- `tests/DeterministicProxy.Tests`: Comprehensive xUnit test suite (41+ tests).
- `tests/DeterministicProxy.Benchmarks`: Microbenchmark & load testing suite.

## 🚀 Local Development Setup

### Prerequisites
- [.NET 11.0 or 10.0 SDK](https://dotnet.microsoft.com/)
- Python 3.9+ (for SDK development)

### Build & Test
```bash
# Run test suite
dotnet test

# Run performance benchmarks
dotnet run --project tests/DeterministicProxy.Benchmarks/DeterministicProxy.Benchmarks.csproj -c Release

# Run gateway
dotnet run --project src/DeterministicProxy.Gateway
```

## 📜 Pull Request Guidelines
1. Ensure all existing and new tests pass (`dotnet test`).
2. Adhere to zero-allocation hot paths where applicable (`System.IO.Pipelines`, `Span<T>`, `ArrayPool<byte>`).
