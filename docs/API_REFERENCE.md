# API & Protocol Reference

## 1. Proxy Ingress Headers

When routing requests through the proxy, include these headers to enable deterministic tracking:

| Header | Type | Required | Description |
| :--- | :--- | :--- | :--- |
| `X-Agent-Session-ID` | `string` | **Yes** | Unique identifier for the agent conversation / thread. |
| `X-Step-Index` | `integer` | **Yes** | 0-indexed integer representing the current execution step. |
| `X-Branch-Id` | `string` | No | Branch lineage identifier (default: `"main"`). |
| `X-Execution-Mode` | `enum` | No | Mode: `Auto` (default), `Record`, `Replay`, or `Fork`. |
| `X-Target-Url` | `string` | No | The ultimate target URL (e.g. `https://api.openai.com/v1/chat/completions`). |
| `X-Speed-Multiplier` | `float` | No | Replay speed: `0.0` (instant, default), `1.0` (real-time), or `>1.0` (accelerated). |

---

## 2. Proxy Egress Response Headers

The proxy attaches the following diagnostic headers to all outgoing responses:

| Header | Description |
| :--- | :--- |
| `X-Deterministic-Replay` | Present and set to `"true"` if the response was served from cache. |
| `X-Deterministic-Recorded` | Present and set to `"true"` if the response was executed live and captured. |
| `X-Frame-Hash` | The SHA256 Merkle hash of this step in the execution DAG. |

---

## 3. Control Plane REST API

### Health Check
`GET /health`
* **Response (200 OK):**
```json
{
  "status": "healthy",
  "runtime": ".NET 9.0",
  "engine": "DeterministicProxy Enterprise",
  "timestamp": "2026-10-02T10:35:00Z"
}
```

---

### Get Execution History
`GET /api/sessions/{sessionId}/history?branchId=main`
* **Response (200 OK):**
```json
{
  "session_id": "session-123",
  "branch_id": "main",
  "total_steps": 2,
  "frames": [
    {
      "step_index": 0,
      "step_id": "9f3d...",
      "parent_step_hash": null,
      "frame_hash": "a4b1...",
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
  "session_id": "session-123",
  "branch_id": "main",
  "is_valid_dag": true
}
```

---

### Compare Execution Branches (Diffing)
`GET /api/sessions/{sessionId}/compare-branches?baseBranch=main&targetBranch=experiment-v1`
* **Response (200 OK):**
```json
{
  "session_id": "session-123",
  "base_branch_id": "main",
  "target_branch_id": "experiment-v1",
  "divergence_step_index": 2,
  "identical_steps_count": 2,
  "step_diffs": [
    {
      "step_index": 0,
      "is_match": true,
      "base_frame_hash": "a4b1...",
      "target_frame_hash": "a4b1..."
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
  "sessionId": "session-123",
  "sourceBranchId": "main",
  "newBranchId": "experiment-v1",
  "forkAtStepIndex": 2
}
```
* **Response (200 OK):**
```json
{
  "message": "BranchCreated",
  "session_id": "session-123",
  "new_branch_id": "experiment-v1",
  "copied_steps": 3
}
```

---

## 4. WebSocket & Browser CDP Endpoint

`GET /ws/cdp?target=ws://localhost:9222/devtools/browser`
* Upgrades incoming client connection to WebSocket (`101 Switching Protocols`).
* Connects to upstream headless Chromium CDP endpoint.
* Captures all JSON-RPC frames bidirectionally and records DOM trees and user input events.
