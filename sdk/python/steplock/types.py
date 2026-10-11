from dataclasses import dataclass, field
from enum import Enum
from typing import Optional, Dict, Any, List
from datetime import datetime


class ExecutionMode(str, Enum):
    AUTO = "Auto"
    RECORD = "Record"
    REPLAY = "Replay"
    FORK = "Fork"


class SideEffectType(int, Enum):
    SAFE_READ_ONLY = 0
    MUTATING = 1
    IDEMPOTENT_WRITE = 2


@dataclass
class StreamChunk:
    seq: int
    delta_ms: int
    data: bytes

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "StreamChunk":
        payload = data.get("data", "")
        if isinstance(payload, str):
            import base64
            try:
                payload = base64.b64decode(payload)
            except Exception:
                payload = payload.encode("utf-8")
        return cls(
            seq=data.get("seq", 0),
            delta_ms=data.get("delta_ms", 0),
            data=payload
        )


@dataclass
class WebSocketCapturedFrame:
    seq: int
    timestamp_ms: int
    is_client_to_server: bool
    message_type: str
    payload: str


@dataclass
class ExecutionFrame:
    session_id: str
    branch_id: str
    step_index: int
    step_id: str
    frame_hash: str
    http_method: str
    target_uri: str
    request_headers: Dict[str, str]
    request_body_hash: str
    response_status: int
    response_headers: Dict[str, str]
    chunks: List[StreamChunk]
    side_effect_type: SideEffectType
    duration_ms: int
    parent_step_hash: Optional[str] = None
    request_body_canonical: Optional[bytes] = None
    ws_frames: Optional[List[WebSocketCapturedFrame]] = None
    ttft_ms: Optional[int] = None
    created_at_utc: Optional[str] = None


@dataclass
class ExecutionSession:
    session_id: str
    root_branch_id: str
    active_branch_id: str
    created_at_utc: str
    metadata: Dict[str, str] = field(default_factory=dict)


@dataclass
class StepDiff:
    step_index: int
    base_frame_hash: Optional[str]
    target_frame_hash: Optional[str]
    is_match: bool
    base_target_uri: Optional[str]
    target_target_uri: Optional[str]
    status_code_changed: bool


@dataclass
class BranchDiff:
    session_id: str
    base_branch_id: str
    target_branch_id: str
    divergence_step_index: Optional[int]
    identical_steps_count: int
    step_diffs: List[StepDiff] = field(default_factory=list)


@dataclass
class TenantUsageMetrics:
    total_recorded_steps: int
    total_replayed_steps: int
    total_proxied_bytes: int
    estimated_llm_cost_saved_cents: int
