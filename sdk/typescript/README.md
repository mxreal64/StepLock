# StepLock TypeScript / JavaScript SDK

Client library for integrating AI agents with the **StepLock** deterministic state proxy and content-addressable execution store.

## Features

- **Deterministic Step Locking:** Guarantees repeatable multi-step execution traces with monotonic step index progression.
- **Dynamic Context Management:** Ambient session support via `StepLockSession.run()` and `StepLockSession.getCurrent()`.
- **Automatic Interception:** Seamlessly redirect OpenAI, Anthropic, or standard `fetch` calls through the StepLock gateway.
- **Cryptographic DAG Verification:** Remotely verify Merkle DAG integrity.
- **Time-Travel & Branch Comparison:** Fork execution traces at arbitrary steps and compare divergence points.

---

## Installation

```bash
npm install @steplock/sdk
# or
pnpm add @steplock/sdk
# or
bun add @steplock/sdk
```

---

## Quickstart

### 1. Wrapping Fetch

```typescript
import { StepLockSession, createStepLockFetch, ExecutionMode } from "@steplock/sdk";

const session = new StepLockSession({
  sessionId: "agent-run-101",
  branchId: "main",
  executionMode: ExecutionMode.AUTO,
  autoAdvance: true,
});

const proxyFetch = createStepLockFetch({ session });

const response = await proxyFetch("https://api.openai.com/v1/chat/completions", {
  method: "POST",
  headers: {
    "Authorization": `Bearer ${process.env.OPENAI_API_KEY}`,
    "Content-Type": "application/json",
  },
  body: JSON.stringify({
    model: "gpt-4o",
    messages: [{ role: "user", content: "Plan a flight" }],
  }),
});
```

### 2. Wrapping OpenAI SDK

```typescript
import OpenAI from "openai";
import { StepLockSession, wrapOpenAI, ExecutionMode } from "@steplock/sdk";

const session = new StepLockSession({
  sessionId: "agent-eval-001",
  executionMode: ExecutionMode.REPLAY,
  speedMultiplier: 5.0, // 5x playback speed
});

const client = wrapOpenAI(new OpenAI(), session);

// All completions are now routed through StepLock
const result = await client.chat.completions.create({
  model: "gpt-4o",
  messages: [{ role: "user", content: "Hello!" }],
});
```

### 3. Gateway Client Operations

```typescript
import { StepLockClient } from "@steplock/sdk";

const client = new StepLockClient({ baseUrl: "http://localhost:5000" });

// Verify cryptographic DAG
const isValid = await client.verifyDag("agent-eval-001", "main");
console.log(`Merkle DAG integrity: ${isValid}`);

// Fork branch for prompt experimentation
await client.forkBranch("agent-eval-001", "main", "experimental-prompt", 3);

// Compare branch divergence
const diff = await client.compareBranches("agent-eval-001", "main", "experimental-prompt");
console.log(`Diverged at step: ${diff.divergence_step_index}`);
```

---

## License

Apache-2.0
