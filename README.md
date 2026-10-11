<div align="center">

# StepLock
### *"Git for Live Agent Execution RAM & Network Traffic"*

A sub-millisecond deterministic state proxy and Merkle DAG execution engine built for multi-step AI agent runtimes (**LangGraph**, **Temporal**, **AutoGen**, **CrewAI**, and **Playwright**).

[![.NET 11](https://img.shields.io/badge/.NET-11.0%20%7C%2010.0-purple.svg)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-49%20passing-brightgreen.svg)]()
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![Python SDK](https://img.shields.io/badge/Python%20SDK-source-green.svg)](sdk/python)
[![TypeScript SDK](https://img.shields.io/badge/TypeScript%20SDK-source-blue.svg)](sdk/typescript)
[![Docker](https://img.shields.io/badge/Docker-ready-2496ED.svg)](Dockerfile)

[**Quickstart**](#-30-second-quickstart) •
[**Why StepLock?**](#-why-steplock) •
[**Architecture**](#-system-architecture) •
[**Benchmarks**](#-benchmarks) •
[**Python SDK**](#-python-sdk) •
[**TypeScript SDK**](#-typescript--javascript-sdk) •
</div>

---

## The Problem StepLock Solves

When an AI agent runs an 8-step workflow (e.g. triaging a user, calling tools, executing database queries, and modifying state):

1. **Expensive & Non-Deterministic Retries**: Step 7 fails. Re-running the agent from scratch burns tokens, hits API rate limits, and LLM jitter prevents you from reproducing the exact bug.
2. **Dangerous Mutation Side-Effects**: Replaying a trace in staging might accidentally fire live Stripe charges, DB writes, or email dispatches.
3. **Zero Lineage Visibility**: Traditional APMs give logs, but cannot **time-travel**, **fork branches**, or **virtually replay** network packets with exact token timing.

**StepLock solves this by recording every execution step into a cryptographic Merkle DAG and virtualizing replays in sub-microseconds.**

---

## 30-Second Quickstart

### 1. Launch the StepLock Proxy Gateway

```bash
# Clone the repository
git clone https://github.com/mxreal64/steplock.git
cd steplock

# Run natively via .NET SDK
dotnet run --project src/DeterministicProxy.Gateway

# Or build & run the container locally
docker build -t steplock:local .
docker run -d -p 5000:5000 -v steplock_data:/data steplock:local
```
*StepLock is now active at `http://localhost:5000` with the visual dashboard at `http://localhost:5000/dashboard`.*

### 2. Install the Python SDK (Local Source)

```bash
pip install -e sdk/python
```

### 3. Wrap Your Agent Calls

```python
from steplock import StepLockSession
import httpx

with StepLockSession(session_id="order-support-agent-01", branch_id="main") as session:
    
    # Step 0: Call LLM
    headers = session.get_headers(target_url="https://api.openai.com/v1/chat/completions")
    session.advance_step()
    
    res = httpx.post(
        "http://localhost:5000",
        headers=headers,
        json={"model": "gpt-4o", "messages": [{"role": "user", "content": "Refund order #452"}]}
    )
    print("Step 0 Result:", res.json())
    print("Served from Cache?", res.headers.get("X-Deterministic-Replay") == "true")
```

---

## ⚖️ Why StepLock?

| Feature | StepLock | LangSmith / Langfuse | VCR.py / Mocking | Charles / MITMProxy |
|---|:---:|:---:|:---:|:---:|
| **Cryptographic Merkle DAG Chaining** | ✅ **Yes** | ❌ No | ❌ No | ❌ No |
| **Sub-Millisecond Zero-Copy Streaming Tap** | ✅ **Yes (337k chunks/s)** | ❌ No (HTTP Hook) | ❌ No | ⚠️ High Overhead |
| **Time-Travel Execution Branching (`fork`)** | ✅ **Yes** | ❌ No | ❌ No | ❌ No |
| **Dynamic TLS MITM & WebSocket CDP Tap** | ✅ **Yes** | ❌ No | ❌ No | ⚠️ Partial |
| **On-Premise / 100% Free & Open Source Core** | ✅ **Yes (Apache 2.0)** | ❌ Cloud / Heavy | ✅ Yes | ✅ Yes |

---

## System Architecture

```
                                  ┌──────────────────────────────────────────────┐
                                  │      Agent Orchestrator (Python / Node)      │
                                  │   LangGraph / Temporal / AutoGen / CrewAI    │
                                  └──────────────────────┬───────────────────────┘
                                                         │
                                    HTTP/HTTPS & CDP     │  Headers: X-Agent-Session-ID, X-Step-Index,
                                    (Reverse / Forward)  │           X-Execution-Mode, X-Branch-Id
                                                         ▼
┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                                 STEPLOCK CORE (.NET 11)                                                │
│                                                                                                                        │
│  ┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐  │
│  │                                      High-Performance Gateway & Dispatcher                                       │  │
│  │   • Kestrel Edge Pipeline     • Dynamic TLS MITM Tunnel (CONNECT)     • WebSocket / CDP Frame Multiplexer        │  │
│  └──────────────────────────────────────────────────────────┬───────────────────────────────────────────────────────┘  │
│                                                             │                                                          │
│                                                             ▼                                                          │
│  ┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐  │
│  │                                         Semantic Request Canonicalizer                                           │  │
│  │   • Strips dynamic jitter (Timestamps, UUIDs)     • Normalizes JSON payloads     • Computes Merkle Hash          │  │
│  └──────────────────────────────────────────────────────────┬───────────────────────────────────────────────────────┘  │
│                                                             │                                                          │
│                                ┌────────────────────────────┴────────────────────────────┐                             │
│                                │                                                         │                             │
│               [Cache Hit / Replay Mode]                                     [Cache Miss / Live Execution]              │
│                                ▼                                                         ▼                             │
│  ┌──────────────────────────────────────────────────────────┐  ┌────────────────────────────────────────────────────┐  │
│  │                  Virtual Replay Engine                   │  │             Duplex Streaming Tap (Pipelines)       │  │
│  │   • Instant 0x deterministic cache replay                │  │   • Zero-copy forward to Agent Client              │  │
│  │   • Cryptographic Merkle parent integrity check          │  │   • Enqueues frame to Background WAL Channel       │  │
│  └──────────────────────────────────────────────────────────┘  └─────────────────────────┬──────────────────────────┘  │
│                                                                                          │                             │
│                                                                                          ▼                             │
│  ┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐  │
│  │                                     High-Throughput State Persistence (CAS)                                      │  │
│  │   • L1: In-Memory Ring Buffer & Hot CAS (1.5M+ ops/s, 0 Alloc)                                                   │  │
│  │   • L2: SQLite (WAL Mode) for Merkle DAG & Chunk BLOBs (18.5k frames/s)                                          │  │
│  └──────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┬──────────────────────────────────────────────────────────┘
                                                              │
                                      ┌───────────────────────┴───────────────────────┐
                                      ▼                                               ▼
                         [ External LLM APIs ]                            [ Third-Party Tools & APIs ]
                     (OpenAI, Anthropic, Gemini)                              (Stripe, Postgres, GitHub)
```

---

## Performance Benchmarks

Run benchmarks locally: `dotnet run --project tests/DeterministicProxy.Benchmarks/DeterministicProxy.Benchmarks.csproj -c Release`

| Benchmark Target | Throughput | p50 Latency | p95 Latency | p99 Latency | Alloc / Op |
|---|:---:|:---:|:---:|:---:|:---:|
| **In-Memory Store (Write + Hash Lookup)** | **1,572,278 op/s** | **0.50 µs** | 1.00 µs | 1.90 µs | **0 B (Zero-Alloc)** |
| **Merkle DAG Hashing (SHA-256 Chaining)** | **300,193 op/s** | **2.30 µs** | 5.10 µs | 14.90 µs | **152 B** |
| **Zero-Copy Streaming Tap (`Pipelines`)** | **171,343 op/s** | **3.50 µs** | 10.60 µs | 25.40 µs | 1.7 KB |
| **PII & Secret Redaction Engine** | **91,001 op/s** | **8.60 µs** | 19.70 µs | 42.60 µs | 1.7 KB |
| **Semantic Request Canonicalizer (JSON)** | **27,432 op/s** | **31.10 µs** | 70.60 µs | 108.30 µs | **1.0 KB** |
| **Dynamic TLS Leaf Cert Gen (ECDsa P-256)** | **407 op/s** | **2.23 ms** | 4.20 ms | 5.62 ms | 17.8 KB |
| **SQLite WAL Batch Persistence (50 Frames/Tx)** | **371 op/s** *(18.5k frames/s)* | **2.58 ms** | 4.20 ms | 5.46 ms | **148.1 KB** |

---

## Python SDK (`steplock`)

### Time-Travel Branching

```python
from steplock import StepLockClient

client = StepLockClient("http://localhost:5000")
session_id = "customer-support-agent-01"

# 1. Fork a failed session from Step 2 into an experiment branch
client.fork_branch(
    session_id=session_id,
    source_branch="main",
    new_branch="experiment-tweaked-prompt",
    fork_at_step=2
)

# 2. Re-run agent on the new branch (Steps 0, 1, 2 served instantly from cache!)

# 3. Verify cryptographic Merkle chain integrity:
is_valid = client.verify_dag(session_id, "main")
print("Merkle DAG Integrity Valid:", is_valid)
```

---

## TypeScript / JavaScript SDK (`sdk/typescript`)

Full client library for Node.js, Bun, Deno, and browser runtimes (import directly or link from `./sdk/typescript`):

```typescript
import { StepLockSession, wrapOpenAI, ExecutionMode } from "@steplock/sdk";
import OpenAI from "openai";

const session = new StepLockSession({
  sessionId: "agent-eval-001",
  branchId: "main",
  executionMode: ExecutionMode.REPLAY,
  speedMultiplier: 5.0, // 5x playback speed
  autoAdvance: true
});

const client = wrapOpenAI(new OpenAI(), session);

// Requests are deterministically proxied with step control headers
const completion = await client.chat.completions.create({
  model: "gpt-4o",
  messages: [{ role: "user", content: "Plan a trip to Tokyo" }]
});
```

---

## Features


```
                  ┌───────────────────────────────────────────────┐
                  │                 STEPLOCK CORE                 │
                  └───────────────────────┬───────────────────────┘
                                          │
                  ┌───────────────────────┴───────────────────────┐
                  ▼                                               ▼
     [ CORE ENGINE & PERSISTENCE ]                  [ ADVANCED AGENT SUITE ]
  • Zero-Copy Pipelines SSE Streaming Tap        • Branch Regression Diffing Engine
  • High-Throughput SQLite WAL Storage           • L3 Cloud CAS Sync (.dpz bundles)
  • In-Memory Zero-Alloc Ring Buffer Store       • Virtual Timing SSE Replay + Jitter
  • Dynamic TLS MITM Forward Proxy (CONNECT)     • Side-Effect Mutation Safety Barrier
  • WebSocket CDP Duplex Interception            • Multi-Tenancy & Usage Quota Management
  • SHA-256 Merkle DAG Cryptographic Integrity   • Automated PII & Secret Redactor
```

### Advanced Built-in Capabilities:
1. **Branch Regression Diffing (`BranchRegressionDiffEngine`)**: Semantic AST and payload comparison across agent branches to detect prompt hallucinations, tool schema drift, and payload mutations.
2. **L3 Cloud CAS Archival (`.dpz` Packages)**: Export and import complete agent execution traces in compressed, tamper-proof packages with SHA-256 CAS manifests.
3. **Virtual Timing SSE Stream Replay (`VirtualTimingEngine`)**: Replay streams at arbitrary speeds ($0\times$ instant, $1\times$ real-time, $10\times$ accelerated) with realistic micro-jitter simulation.
4. **Side-Effect Safety Barrier (`SideEffectBarrier`)**: Intercepts mutating third-party API calls (e.g. Stripe, emails, database mutations) during replays, returning synthetic virtualized responses.
5. **PII & Secret Redaction (`EnterprisePiiRedactor`)**: High-throughput regex pipeline masking OpenAI keys, Anthropic keys, AWS credentials, credit cards, and Bearer tokens in real time.

---

## Contributing

Community contributions are welcome! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for local setup, architecture overview, and PR guidelines.

---

## License

StepLock is free and open-source software licensed under the **[Apache License 2.0](LICENSE)**.
