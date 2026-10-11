/**
 * StepLock TypeScript / JavaScript SDK
 * Type definitions mirroring StepLock Core & Storage models.
 */

export enum ExecutionMode {
  AUTO = "Auto",
  RECORD = "Record",
  REPLAY = "Replay",
  FORK = "Fork",
}

export enum SideEffectType {
  SAFE_READ_ONLY = 0,
  MUTATING = 1,
  IDEMPOTENT_WRITE = 2,
}

export interface StreamChunk {
  seq: number;
  delta_ms: number;
  data: string | Uint8Array;
}

export interface WebSocketCapturedFrame {
  seq: number;
  timestamp_ms: number;
  is_client_to_server: boolean;
  message_type: string;
  payload: string;
}

export interface ExecutionFrame {
  session_id: string;
  branch_id: string;
  step_index: number;
  step_id: string;
  frame_hash: string;
  http_method: string;
  target_uri: string;
  request_headers: Record<string, string>;
  request_body_hash: string;
  response_status: number;
  response_headers: Record<string, string>;
  chunks: StreamChunk[];
  side_effect_type: SideEffectType;
  duration_ms: number;
  parent_step_hash?: string | null;
  request_body_canonical?: string | null;
  ws_frames?: WebSocketCapturedFrame[] | null;
  ttft_ms?: number | null;
  created_at_utc?: string | null;
}

export interface ExecutionSession {
  session_id: string;
  root_branch_id: string;
  active_branch_id: string;
  created_at_utc: string;
  metadata?: Record<string, string>;
}

export interface StepDiff {
  step_index: number;
  base_frame_hash?: string | null;
  target_frame_hash?: string | null;
  is_match: boolean;
  base_target_uri?: string | null;
  target_target_uri?: string | null;
  status_code_changed: boolean;
}

export interface BranchDiff {
  session_id: string;
  base_branch_id: string;
  target_branch_id: string;
  divergence_step_index?: number | null;
  identical_steps_count: number;
  step_diffs: StepDiff[];
}

export interface TenantUsageMetrics {
  total_recorded_steps: number;
  total_replayed_steps: number;
  total_proxied_bytes: number;
  estimated_llm_cost_saved_cents: number;
}

export interface SessionOptions {
  sessionId: string;
  branchId?: string;
  executionMode?: ExecutionMode | string;
  speedMultiplier?: number;
  proxyUrl?: string;
  autoAdvance?: boolean;
}

export interface ClientOptions {
  baseUrl?: string;
  timeoutMs?: number;
  fetchFn?: typeof fetch;
}
