# Deterministic Proxy: Open-Core & Commercial Licensing Guide

```
                  ┌───────────────────────────────────────────────┐
                  │          DETERMINISTIC PROXY SOLUTION         │
                  └───────────────────────┬───────────────────────┘
                                          │
                  ┌───────────────────────┴───────────────────────┐
                  ▼                                               ▼
     [ OPEN SOURCE COMMUNITY CORE ]                 [ PROPRIETARY ENTERPRISE MODULES ]
  Distributed under Apache 2.0 / MIT              Kept private / Commercial License
  
  • System.IO.Pipelines SSE Zero-Copy Tap         • Branch Regression Diffing Engine
  • Basic SQLite WAL Persistence Engine           • L3 Cloud Archive Sync (S3/Blob format)
  • Python Integration SDK                        • Virtual Timing Replay (1x / 10x / Instant)
  • Reverse / Forward Proxy MITM Gateway          • Side-Effect Mutation Safety Barrier
  • WebSocket CDP Duplex Interceptor              • Multi-Tenancy & Usage Metering
  • Merkle DAG Cryptographic Integrity            • Cryptographic License Key Manager
```

## Feature Matrix Comparison

| Feature | Open Source Community | Enterprise Edition |
|---|:---:|:---:|
| **Zero-Copy Streaming Tap (`System.IO.Pipelines`)** | ✅ Included | ✅ Included |
| **SQLite WAL Mode Storage Engine** | ✅ Included | ✅ Included |
| **In-Memory Ring Buffer / Store** | ✅ Included | ✅ Included |
| **Python SDK (`deterministic_proxy`)** | ✅ Included | ✅ Included |
| **Dynamic TLS MITM Forward Proxy (CONNECT)** | ✅ Included | ✅ Included |
| **CDP & WebSocket Duplex Interception** | ✅ Included | ✅ Included |
| **Merkle DAG Verification (`VerifyDagIntegrity`)** | ✅ Included | ✅ Included |
| **Branch Regression Diffing Engine** | ❌ | ✅ Included |
| **L3 Cloud Archive Sync (`.dpz` S3/Blob CAS)** | ❌ | ✅ Included |
| **Virtual Timing SSE Token Replay (`1x / 10x`)** | ❌ (0x Instant only) | ✅ Included (with Jitter Sim) |
| **Side-Effect Mutation Short-Circuiting** | ❌ (Pass-through) | ✅ Included (Virtual Mocking) |
| **Multi-Tenancy & Enterprise Metering** | ❌ | ✅ Included |
| **Signed License Key Enforcement** | ❌ | ✅ Included |
| **Commercial SLA & Dedicated Support** | ❌ | ✅ Included |
