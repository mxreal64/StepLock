# StepLock Architecture & Internals Deep Dive

## 1. The Core Problem: Agent Non-Determinism

Multi-step AI agents (e.g. LangGraph state machines, Temporal durable workflows, AutoGen, and browser automation agents) are inherently non-deterministic. When an agent fails on Step 8 of a 10-step trajectory:
1. **Costly Re-Execution**: Re-running the agent changes the reasoning path, generating new tool inputs and consuming duplicate LLM API tokens.
2. **Duplicate Side Effects**: Blindly retrying can fire live real-world mutations twice (duplicate credit card charges, email notifications, database records).
3. **No Root-Cause Pinpointing**: Engineers cannot pause, inspect, or time-travel back to step 7 to evaluate a prompt fix without re-running steps 0 through 6.

**StepLock acts as *"Git for Live Agent Execution RAM & Network Traffic"*** to make multi-step autonomous agents fully reproducible, testable, and debuggable.

---

## 2. Merkle DAG Cryptographic Lineage

Each step snapshot is recorded as an immutable `ExecutionFrame` linked to the cryptographic hash of its predecessor:

$$\text{FrameHash}_0 = \text{SHA256}(\text{"ROOT"} \mathbin{\Vert} 0 \mathbin{\Vert} \text{METHOD} \mathbin{\Vert} \text{URI} \mathbin{\Vert} \text{CanonicalRequestBodyHash})$$
$$\text{FrameHash}_n = \text{SHA256}(\text{FrameHash}_{n-1} \mathbin{\Vert} n \mathbin{\Vert} \text{METHOD} \mathbin{\Vert} \text{URI} \mathbin{\Vert} \text{CanonicalRequestBodyHash})$$

```
[ Step 0: Plan ] ──(H0)──► [ Step 1: SQL Query ] ──(H1)──► [ Step 2: Stripe Refund ] (H2)
                                                                 │
                                                                 ├──[ Branch: Experiment ]
                                                                 └──► [ Step 3: Tweaked Prompt ] (H3_new)
```

### Why Merkle DAG?
* **Proof of Execution Lineage**: Guarantees that step $n$ was strictly preceded by the exact sequence of historical states $0 \dots n-1$.
* **Zero-Cost Branching**: When creating a new branch at step $k$, historical frames $0 \dots k$ share the exact same Merkle hashes, allowing instant cache hits while diverging paths receive unique hashes.
* **Tamper-Proof Audit Trail**: Any payload tampering immediately invalidates the Merkle chain.

---

## 3. High-Performance Zero-Copy Streaming Tap (`System.IO.Pipelines`)

Modern LLMs stream tokens via Server-Sent Events (`text/event-stream`). Naive proxy implementations buffer the full HTTP body before processing, introducing hundreds of milliseconds of artificial latency and breaking partial token parsers.

StepLock's `DuplexStreamingTap` uses .NET `System.IO.Pipelines` and `ArrayPool<byte>`:
1. Bytes read from the upstream LLM are immediately forwarded into the client's `PipeWriter`.
2. As bytes are tapped, each chunk is recorded as `StreamChunk(sequence, delta_ms, payload)`.
3. TTFT (Time-To-First-Token) is captured from `chunk[0].DeltaMs`.
4. Zero heap allocations occur on the hot forward path, sustaining over **171,000 op/s**.

```
Upstream HTTP Stream ──► [ ArrayPool Buffer ] ┬──► Client PipeWriter (Zero added latency)
                                              │
                                              └──► StreamChunk Collector ──► Background WAL Channel
```

---

## 4. Semantic Request Canonicalization

Naive string hashing fails because client frameworks inject dynamic entropy into requests (e.g. `User-Agent`, `Date`, trace IDs, client timestamps, or varying JSON key ordering).

StepLock's `SemanticRequestCanonicalizer` normalizes requests prior to computing Merkle hashes:
1. **Volatile Header Stripping**: Drops dynamic headers (`Date`, `Traceparent`, `User-Agent`, `X-Request-Id`, etc.).
2. **Deterministic JSON Key Sorting**: Parses payloads using high-speed UTF-8 JSON spans and sorts object keys alphabetically.
3. **Dynamic Field Masking**: Masks ephemeral nonce keys (`"nonce"`, `"client_timestamp"`, `"uuid"`) with `[VIRTUAL_DETERMINISTIC_MASKED]`.

---

## 5. Side-Effect Safety Barrier

StepLock classifies every outbound request into three safety tiers:

| Side Effect Type | Behavior in Record/Live Mode | Behavior in Replay Mode |
| :--- | :--- | :--- |
| **`SafeReadOnly`** (LLMs, GET requests) | Executed and recorded | Served from Content-Addressable Storage (CAS) cache |
| **`Mutating`** (POST to Stripe, Email, DB writes) | Executed live with audit logging | **Short-circuited**: Returns synthetic virtual response with `X-Deterministic-Virtualized: true` |
| **`IdempotentWrite`** (Requests with Idempotency Key) | Executed with idempotency key | Served from cache if identical key is detected |

---

## 6. Advanced OSS Engine Modules

### A. Branch Regression Diffing (`BranchRegressionDiffEngine`)
Compares two branches (e.g. `main` vs. `experiment-prompt`) step by step. Pinpoints exact divergence points, semantic payload changes, and whether divergence occurred due to prompt drift, tool schema changes, or HTTP status changes.

### B. Virtual Timing Engine (`VirtualTimingEngine`)
Replays token streams with configurable speed pacing:
* **$0.0\times$ (Instant)**: Zero delay, maximum test throughput.
* **$1.0\times$ (Real-Time)**: Emulates original production token arrival cadence.
* **$>1.0\times$ (Accelerated)**: Paced replay with micro-jitter simulation for realistic UI testing.

### C. L3 Cloud Content-Addressable Storage (`.dpz` Bundles)
Enables cross-machine sharing of execution traces:
* Compresses complete trace DAGs into `.dpz` packages.
* Generates SHA-256 CAS manifests.
* Works with local or remote object stores (S3, Azure Blob, GCS).

---

## 7. Non-Blocking Write-Ahead Log (WAL)

Database persistence is completely decoupled from request forwarding via bounded channels:

```
Request Pipeline ──(TryWrite)──► [ Bounded Channel (10,000 capacity) ]
                                             │
                                             ▼
                                [ Background WAL Service Worker ]
                                             │
                                             ▼
                                [ SQLite (WAL Mode) / Store ]
```

* **Overhead**: $<0.05\text{ ms}$ to enqueue in-memory.
* **Storage Performance**: Sustains over **18,500 frames/s** in batch SQLite transactions with full crash durability.
