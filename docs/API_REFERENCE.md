# StepLock API & Protocol Reference

## 1. Proxy Ingress Headers

When routing requests through the StepLock Gateway, supply these headers to control session lineage and replay behavior:

| Header | Type | Required | Description |
| :--- | :--- | :--- | :--- |
| `X-Agent-Session-ID` | `string` | **Yes** | Unique identifier for the agent conversation / thread. |
| `X-Step-Index` | `integer` | **Yes** | 0-indexed integer representing the current execution step. |
| `X-Branch-Id` | `string` | No | Branch lineage identifier (default: `"main"`). |
| `X-Execution-Mode` | `enum` | No | `Auto` (default), `Record`, `Replay`, or `Fork`. |
| `X-Target-Url` | `string` | No | Upstream target URL (e.g. `https://api.openai.com/v1/chat/completions`). |
| `X-Speed-Multiplier` | `float` | No | Replay speed: `0.0` (instant), `1.0` (real-time), or `>1.0` (accelerated). |

---

## 2. Proxy Egress Diagnostic Headers

StepLock attaches the following diagnostic headers to all outgoing responses:

| Header | Description |
| :--- | :--- |
| `X-Deterministic-Replay` | `"true"` if the response was served from cache. |
| `X-Deterministic-Recorded` | `"true"` if the response was captured and recorded live. |
| `X-Deterministic-Virtualized` | `"true"` if a mutating call was short-circuited and mocked safely. |
| `X-Frame-Hash` | The SHA-256 Merkle hash of this step in the execution DAG. |

---

## 3. Control Plane REST API

### Health & Runtime Check
`GET /health`
* **Response (200 OK):**
```json
{
  "status": "healthy",
  "runtime": ".NET 11.0",
  "engine": "StepLock Enterprise",
  "timestamp": "2026-10-11T11:45:00Z"
}
```

---

### List Sessions
`GET /api/sessions`
* **Response (200 OK):**
```json
{
  "total": 3,
  "sessions": [
    {
      "session_id": "customer-support-agent-01",
      "root_branch_id": "main",
      "active_branch_id": "experiment-tweaked-prompt",
      "created_at_utc": "2026-10-11T10:00:00Z"
    }
  ]
}
```

---

### Get Execution History
`GET /api/sessions/{sessionId}/history?branchId=main`
* **Response (200 OK):**
```json
{
  "session_id": "customer-support-agent-01",
  "branch_id": "main",
  "total_steps": 2,
  "frames": [
    {
      "step_index": 0,
      "step_id": "step_0",
      "parent_step_hash": null,
      "frame_hash": "a4b1c8...",
      "http_method": "POST",
      "target_uri": "https://api.openai.com/v1/chat/completions",
      "response_status": 200,
      "duration_ms": 340,
      "ttft_ms": 85,
      "side_effect_type": 0
    }
  ]
}
```

---

### Verify Merkle DAG Integrity
`GET /api/sessions/{sessionId}/verify-dag?branchId=main`
* **Response (200 OK):**
```json
{
  "session_id": "customer-support-agent-01",
  "branch_id": "main",
  "is_valid_dag": true
}
```

---

### Compare Branches (Semantic Diffing)
`GET /api/sessions/{sessionId}/compare-branches?baseBranch=main&targetBranch=experiment-tweaked-prompt`
* **Response (200 OK):**
```json
{
  "session_id": "customer-support-agent-01",
  "base_branch_id": "main",
  "target_branch_id": "experiment-tweaked-prompt",
  "divergence_step_index": 2,
  "identical_steps_count": 2,
  "step_diffs": [
    {
      "step_index": 0,
      "is_match": true,
      "base_frame_hash": "a4b1c8...",
      "target_frame_hash": "a4b1c8..."
    },
    {
      "step_index": 2,
      "is_match": false,
      "base_target_uri": "https://api.stripe.com/v1/charges",
      "target_target_uri": "https://api.stripe.com/v1/invoices",
      "status_code_changed": false
    }
  ]
}
```

---

### Fork Branch (Time-Travel)
`POST /api/sessions/fork`
* **Request Body:**
```json
{
  "sessionId": "customer-support-agent-01",
  "sourceBranchId": "main",
  "newBranchId": "experiment-tweaked-prompt",
  "forkAtStepIndex": 2
}
```
* **Response (200 OK):**
```json
{
  "message": "BranchCreated",
  "session_id": "customer-support-agent-01",
  "new_branch_id": "experiment-tweaked-prompt",
  "copied_steps": 3
}
```

---

### L3 Cloud CAS Archive Export (`.dpz` Bundles)
`POST /api/sessions/{sessionId}/archive/export?branchId=main`
* Compresses and exports full session frame history to cloud storage as a content-addressable `.dpz` package.
* **Response (200 OK):**
```json
{
  "sessionId": "customer-support-agent-01",
  "branchId": "main",
  "totalFrames": 12,
  "totalBytes": 48210,
  "rootMerkleHash": "a4b1c8...",
  "archiveSha256": "9f71da...",
  "createdAtUtc": "2026-10-11T11:45:00Z"
}
```

---

### L3 Cloud CAS Archive Import
`POST /api/sessions/{sessionId}/archive/import?branchId=main`
* Fetches and unpacks a `.dpz` package from cloud storage into the active store.
* **Response (200 OK):**
```json
{
  "session_id": "customer-support-agent-01",
  "branch_id": "main",
  "imported_frames": 12
}
```

---

## 4. WebSocket & Browser CDP Endpoint

`GET /ws/cdp?target=ws://localhost:9222/devtools/browser`
* Upgrades incoming client connection to WebSocket (`101 Switching Protocols`).
* Connects to upstream headless Chromium CDP endpoint.
* Captures all JSON-RPC frames bidirectionally and records DOM trees, network requests, and user input events.
