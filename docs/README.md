# StepLock Documentation & Architecture Sitemap

Welcome to the **StepLock** technical documentation. StepLock is a deterministic state proxy, cryptographic Merkle DAG execution engine, and content-addressable storage layer designed for multi-step AI agent runtimes (**LangGraph**, **Temporal**, **AutoGen**, **CrewAI**, and **Playwright**).

---

## 🗺️ Documentation Directory

| Document | Description |
| :--- | :--- |
| **[Architecture & Internals](ARCHITECTURE.md)** | Deep dive into the cryptographic Merkle DAG, zero-copy streaming tap (`System.IO.Pipelines`), semantic canonicalization, WAL storage, and performance optimisations. |
| **[API & Protocol Reference](API_REFERENCE.md)** | Ingress headers, diagnostic response headers, Control Plane REST API endpoints, WebSockets/CDP proxy, and L3 CAS endpoints. |
| **[SDK & Integration Guide](SDK_GUIDE.md)** | Dual SDK guide for Python (`steplock`) and TypeScript/JavaScript (`@steplock/sdk`), including client wrappers, ambient session scopes, and framework adapters. |
| **[Contributing Guide](../CONTRIBUTING.md)** | Guidelines for testing, building, benchmarking, and submitting contributions. |

---

## ⚡ Key Concepts

### 1. Deterministic Step Locking
Every outbound HTTP call or browser CDP command executed by an agent is captured, keyed by `(SessionId, BranchId, StepIndex)`, and linked to its predecessor via a cryptographic SHA-256 Merkle chain.

### 2. Time-Travel & Branch Forking
Fork an execution trace at any step index $k$ into a new branch. Steps $0 \dots k$ are served instantly with zero LLM API cost, while subsequent steps can evaluate modified prompts or tool outputs.

### 3. Side-Effect Safety Barrier
During replay, calls to mutating endpoints (e.g. Stripe, SendGrid, database updates) are intercepted and mocked with virtualized synthetic responses, preventing real-world duplicate mutations.

### 4. Zero-Copy Streaming
Outbound Server-Sent Events (`text/event-stream`) token chunks are piped directly to the agent runtime using memory pools with sub-microsecond latency overhead while being simultaneously recorded into the background Write-Ahead Log.

### 5. L3 Cloud Content-Addressable Storage Sync
Export and import full execution DAGs across CI/CD runners or team clusters using compressed `.dpz` packages and content-addressable manifests.
