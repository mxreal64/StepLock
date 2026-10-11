# StepLock SDK & Integration Guide

StepLock provides official SDKs for both **Python** (`steplock`) and **TypeScript / JavaScript** (`@steplock/sdk`).

---

## Part 1: Python SDK (`steplock`)

### 1. Installation

```bash
pip install steplock

# Optional extras:
pip install "steplock[openai]"
pip install "steplock[anthropic]"
pip install "steplock[langchain]"
pip install "steplock[all]"
```

### 2. Context Manager (`StepLockSession`)

```python
from steplock import StepLockSession, ExecutionMode
import httpx

# Wrap your agent loop in an ambient session
with StepLockSession(
    session_id="research-agent-401",
    branch_id="main",
    execution_mode=ExecutionMode.AUTO,  # Replay if cached, else record live
    speed_multiplier=0.0,               # 0.0 = instant replay, 1.0 = real-time
    auto_advance=True                   # Automatically increments step_index
) as session:

    # Step 0: Search Web Tool
    headers_0 = session.get_headers("https://api.tavily.com/search")
    res0 = httpx.post("http://localhost:5000", headers=headers_0, json={"query": "Latest AI news"})
    print("Step 0 Result:", res0.json())

    # Step 1: LLM Synthesis (headers automatically advanced)
    headers_1 = session.get_headers("https://api.openai.com/v1/chat/completions")
    res1 = httpx.post(
        "http://localhost:5000",
        headers=headers_1,
        json={
            "model": "gpt-4o",
            "messages": [{"role": "user", "content": f"Summarize: {res0.text}"}]
        }
    )
    print("Step 1 Summary:", res1.json())
```

### 3. Automatic Framework Patching

#### OpenAI SDK:
```python
from openai import OpenAI
from steplock import wrap_openai, StepLockSession

client = wrap_openai(OpenAI(), proxy_url="http://localhost:5000")

with StepLockSession("agent-run-101"):
    response = client.chat.completions.create(
        model="gpt-4o",
        messages=[{"role": "user", "content": "Hello!"}]
    )
```

#### Anthropic SDK:
```python
import anthropic
from steplock import wrap_anthropic, StepLockSession

client = wrap_anthropic(anthropic.Anthropic(), proxy_url="http://localhost:5000")

with StepLockSession("claude-run-101"):
    response = client.messages.create(
        model="claude-3-5-sonnet-20241022",
        max_tokens=1024,
        messages=[{"role": "user", "content": "Analyze this code"}]
    )
```

#### LangChain Callback Handler:
```python
from steplock import StepLockCallbackHandler

handler = StepLockCallbackHandler(session_id="langchain-session-01")
# Pass `callbacks=[handler]` to any LangChain run
```

### 4. Gateway Client Operations

```python
from steplock import StepLockClient, AsyncStepLockClient

client = StepLockClient("http://localhost:5000")

# Verify DAG cryptographic integrity
is_valid = client.verify_dag("research-agent-401", "main")
print("DAG Valid:", is_valid)

# Fork branch at step 2 for prompt experimentation
client.fork_branch("research-agent-401", "main", "experiment-prompt", fork_at_step=2)

# Compare branch divergence
diff = client.compare_branches("research-agent-401", "main", "experiment-prompt")
print(f"Divergence detected at step: {diff.divergence_step_index}")
```

---

## Part 2: TypeScript & JavaScript SDK (`@steplock/sdk`)

### 1. Installation

```bash
npm install @steplock/sdk
# or
pnpm add @steplock/sdk
# or
bun add @steplock/sdk
```

### 2. Ambient Session Scoping

```typescript
import { StepLockSession, ExecutionMode } from "@steplock/sdk";

const session = new StepLockSession({
  sessionId: "agent-eval-001",
  branchId: "main",
  executionMode: ExecutionMode.REPLAY,
  speedMultiplier: 5.0, // 5x accelerated replay
  autoAdvance: true,
});

await session.run(async (active) => {
  console.log(`Active session: ${active.sessionId}`);
  // Downstream wrappers detect active session automatically
});
```

### 3. Fetch Interceptor

```typescript
import { StepLockSession, createStepLockFetch, ExecutionMode } from "@steplock/sdk";

const session = new StepLockSession({
  sessionId: "fetch-agent-01",
  executionMode: ExecutionMode.AUTO,
  autoAdvance: true
});

const proxyFetch = createStepLockFetch({ session });

const res = await proxyFetch("https://api.openai.com/v1/chat/completions", {
  method: "POST",
  headers: {
    "Authorization": `Bearer ${process.env.OPENAI_API_KEY}`,
    "Content-Type": "application/json"
  },
  body: JSON.stringify({
    model: "gpt-4o",
    messages: [{ role: "user", content: "Analyze sentiment" }]
  })
});
```

### 4. OpenAI & Anthropic Wrappers

```typescript
import OpenAI from "openai";
import { StepLockSession, wrapOpenAI, ExecutionMode } from "@steplock/sdk";

const session = new StepLockSession({
  sessionId: "ts-agent-run-01",
  executionMode: ExecutionMode.RECORD,
});

const client = wrapOpenAI(new OpenAI(), session);

const completion = await client.chat.completions.create({
  model: "gpt-4o",
  messages: [{ role: "user", content: "Plan architecture" }]
});
```

### 5. StepLockClient (Gateway Operations)

```typescript
import { StepLockClient } from "@steplock/sdk";

const client = new StepLockClient({ baseUrl: "http://localhost:5000" });

// 1. Verify Merkle DAG
const isValid = await client.verifyDag("ts-agent-run-01", "main");
console.log(`DAG Intact: ${isValid}`);

// 2. Fork branch
await client.forkBranch("ts-agent-run-01", "main", "experimental-eval", 3);

// 3. Compare branch regression
const report = await client.compareBranches("ts-agent-run-01", "main", "experimental-eval");
console.log(`Diverged at: ${report.divergence_step_index}, Identical steps: ${report.identical_steps_count}`);
```
