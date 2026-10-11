import os
import contextvars
import json
import urllib.request
import urllib.parse
from typing import Optional, Dict, Any, List, Union
from .types import (
    ExecutionMode,
    SideEffectType,
    StreamChunk,
    ExecutionFrame,
    ExecutionSession,
    BranchDiff,
    StepDiff,
    TenantUsageMetrics,
)

_current_session = contextvars.ContextVar("current_steplock_session", default=None)


class StepLockSession:
    """
    Context manager representing a deterministic agent execution session in StepLock.
    Supports both synchronous (`with session:`) and asynchronous (`async with session:`) context management.
    Tracks step index and produces canonical proxy headers.
    """

    def __init__(
        self,
        session_id: str,
        branch_id: str = "main",
        execution_mode: Union[str, ExecutionMode] = ExecutionMode.AUTO,
        speed_multiplier: float = 0.0,
        proxy_url: Optional[str] = None,
        auto_advance: bool = False
    ):
        self.session_id = session_id
        self.branch_id = branch_id
        self.execution_mode = execution_mode.value if isinstance(execution_mode, ExecutionMode) else execution_mode
        self.speed_multiplier = speed_multiplier
        self.proxy_url = proxy_url or os.getenv("STEPLOCK_PROXY_URL", os.getenv("DETERMINISTIC_PROXY_URL", "http://localhost:5000"))
        self.auto_advance = auto_advance
        self.step_index = 0
        self._token = None

    def __enter__(self) -> "StepLockSession":
        self._token = _current_session.set(self)
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self._token:
            _current_session.reset(self._token)

    async def __aenter__(self) -> "StepLockSession":
        return self.__enter__()

    async def __aexit__(self, exc_type, exc_val, exc_tb):
        self.__exit__(exc_type, exc_val, exc_tb)

    @classmethod
    def get_current(cls) -> Optional["StepLockSession"]:
        """Retrieve active StepLock session from current task/thread context."""
        return _current_session.get()

    def advance_step(self) -> int:
        """Increment and return the previous step index."""
        current = self.step_index
        self.step_index += 1
        return current

    def get_headers(self, target_url: str) -> Dict[str, str]:
        """
        Generate StepLock proxy control headers for the given upstream target.
        If auto_advance is enabled on the session, automatically advances the step index.
        """
        idx = self.advance_step() if self.auto_advance else self.step_index
        return {
            "X-Agent-Session-ID": self.session_id,
            "X-Branch-Id": self.branch_id,
            "X-Step-Index": str(idx),
            "X-Execution-Mode": str(self.execution_mode),
            "X-Speed-Multiplier": str(self.speed_multiplier),
            "X-Target-Url": target_url,
        }


DeterministicSession = StepLockSession


class StepLockClient:
    """
    Synchronous client for controlling and inspecting the StepLock Execution Gateway.
    Provides session management, DAG integrity verification, and time-travel branch forking.
    Supports httpx connection pooling with urllib fallback.
    """

    def __init__(self, base_url: str = "http://localhost:5000", timeout: float = 30.0):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout
        self._httpx_client = None

    def _get_httpx(self):
        if self._httpx_client is None:
            try:
                import httpx
                self._httpx_client = httpx.Client(base_url=self.base_url, timeout=self.timeout)
            except ImportError:
                pass
        return self._httpx_client

    def _request(self, method: str, path: str, params: Optional[Dict[str, Any]] = None, json_body: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        client = self._get_httpx()
        if client is not None:
            resp = client.request(method, path, params=params, json=json_body)
            resp.raise_for_status()
            return resp.json()
        
        url = f"{self.base_url}{path}"
        if params:
            query = urllib.parse.urlencode({k: v for k, v in params.items() if v is not None})
            url = f"{url}?{query}"
        
        data = json.dumps(json_body).encode("utf-8") if json_body is not None else None
        req = urllib.request.Request(url, data=data, method=method)
        if data:
            req.add_header("Content-Type", "application/json")
        
        with urllib.request.urlopen(req, timeout=self.timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def health(self) -> Dict[str, Any]:
        """Check the health and engine version of the StepLock Gateway."""
        return self._request("GET", "/health")

    def list_sessions(self) -> List[Dict[str, Any]]:
        """List all active agent execution sessions."""
        data = self._request("GET", "/api/sessions")
        return data.get("sessions", [])

    def get_session(self, session_id: str) -> Optional[ExecutionSession]:
        """Retrieve metadata for a specific execution session."""
        try:
            data = self._request("GET", f"/api/sessions/{session_id}")
            return ExecutionSession(
                session_id=data["session_id"],
                root_branch_id=data["root_branch_id"],
                active_branch_id=data["active_branch_id"],
                created_at_utc=data["created_at_utc"],
                metadata=data.get("metadata", {})
            )
        except Exception:
            return None

    def get_history(self, session_id: str, branch_id: str = "main") -> Dict[str, Any]:
        """Retrieve full execution frame DAG history for a session and branch."""
        return self._request("GET", f"/api/sessions/{session_id}/history", params={"branchId": branch_id})

    def verify_dag(self, session_id: str, branch_id: str = "main") -> bool:
        """Verify cryptographic Merkle DAG integrity for the given session and branch."""
        data = self._request("GET", f"/api/sessions/{session_id}/verify-dag", params={"branchId": branch_id})
        return data.get("is_valid_dag", False)

    def fork_branch(
        self,
        session_id: str,
        source_branch: str,
        new_branch: str,
        fork_at_step: int
    ) -> Dict[str, Any]:
        """Fork an execution branch at a specific step index for prompt experimentation."""
        payload = {
            "sessionId": session_id,
            "sourceBranchId": source_branch,
            "newBranchId": new_branch,
            "forkAtStepIndex": fork_at_step
        }
        return self._request("POST", "/api/sessions/fork", json_body=payload)

    def compare_branches(
        self,
        session_id: str,
        base_branch: str,
        target_branch: str
    ) -> BranchDiff:
        """Compare two execution branches to pinpoint where and why agent execution diverged."""
        params = {
            "sessionId": session_id,
            "baseBranch": base_branch,
            "targetBranch": target_branch
        }
        data = self._request("GET", f"/api/sessions/{session_id}/compare-branches", params=params)
        diffs = [
            StepDiff(
                step_index=d["step_index"],
                base_frame_hash=d.get("base_frame_hash"),
                target_frame_hash=d.get("target_frame_hash"),
                is_match=d.get("is_match", False),
                base_target_uri=d.get("base_target_uri"),
                target_target_uri=d.get("target_target_uri"),
                status_code_changed=d.get("status_code_changed", False)
            )
            for d in data.get("step_diffs", [])
        ]
        return BranchDiff(
            session_id=data["session_id"],
            base_branch_id=data["base_branch_id"],
            target_branch_id=data["target_branch_id"],
            divergence_step_index=data.get("divergence_step_index"),
            identical_steps_count=data.get("identical_steps_count", 0),
            step_diffs=diffs
        )

    def get_frame(self, frame_hash: str) -> Optional[Dict[str, Any]]:
        """Look up a specific execution frame by its cryptographic hash."""
        try:
            return self._request("GET", f"/api/frames/{frame_hash}")
        except Exception:
            return None

    def get_tenant_usage(self, tenant_id: str) -> TenantUsageMetrics:
        """Retrieve usage metering metrics for a tenant."""
        data = self._request("GET", f"/api/tenants/{tenant_id}/usage")
        return TenantUsageMetrics(
            total_recorded_steps=data.get("total_recorded_steps", 0),
            total_replayed_steps=data.get("total_replayed_steps", 0),
            total_proxied_bytes=data.get("total_proxied_bytes", 0),
            estimated_llm_cost_saved_cents=data.get("estimated_llm_cost_saved_cents", 0)
        )

    def close(self):
        if self._httpx_client:
            self._httpx_client.close()

    def __enter__(self) -> "StepLockClient":
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.close()


class AsyncStepLockClient:
    """
    Asynchronous client for controlling and inspecting the StepLock Execution Gateway.
    Provides non-blocking session management, DAG integrity verification, and branch forking.
    """

    def __init__(self, base_url: str = "http://localhost:5000", timeout: float = 30.0):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout
        self._client = None

    async def _get_client(self):
        if self._client is None:
            import httpx
            self._client = httpx.AsyncClient(base_url=self.base_url, timeout=self.timeout)
        return self._client

    async def _request(self, method: str, path: str, params: Optional[Dict[str, Any]] = None, json_body: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        client = await self._get_client()
        resp = await client.request(method, path, params=params, json=json_body)
        resp.raise_for_status()
        return resp.json()

    async def health(self) -> Dict[str, Any]:
        return await self._request("GET", "/health")

    async def list_sessions(self) -> List[Dict[str, Any]]:
        data = await self._request("GET", "/api/sessions")
        return data.get("sessions", [])

    async def get_history(self, session_id: str, branch_id: str = "main") -> Dict[str, Any]:
        return await self._request("GET", f"/api/sessions/{session_id}/history", params={"branchId": branch_id})

    async def verify_dag(self, session_id: str, branch_id: str = "main") -> bool:
        data = await self._request("GET", f"/api/sessions/{session_id}/verify-dag", params={"branchId": branch_id})
        return data.get("is_valid_dag", False)

    async def fork_branch(
        self,
        session_id: str,
        source_branch: str,
        new_branch: str,
        fork_at_step: int
    ) -> Dict[str, Any]:
        payload = {
            "sessionId": session_id,
            "sourceBranchId": source_branch,
            "newBranchId": new_branch,
            "forkAtStepIndex": fork_at_step
        }
        return await self._request("POST", "/api/sessions/fork", json_body=payload)

    async def compare_branches(
        self,
        session_id: str,
        base_branch: str,
        target_branch: str
    ) -> BranchDiff:
        params = {
            "sessionId": session_id,
            "baseBranch": base_branch,
            "targetBranch": target_branch
        }
        data = await self._request("GET", f"/api/sessions/{session_id}/compare-branches", params=params)
        diffs = [
            StepDiff(
                step_index=d["step_index"],
                base_frame_hash=d.get("base_frame_hash"),
                target_frame_hash=d.get("target_frame_hash"),
                is_match=d.get("is_match", False),
                base_target_uri=d.get("base_target_uri"),
                target_target_uri=d.get("target_target_uri"),
                status_code_changed=d.get("status_code_changed", False)
            )
            for d in data.get("step_diffs", [])
        ]
        return BranchDiff(
            session_id=data["session_id"],
            base_branch_id=data["base_branch_id"],
            target_branch_id=data["target_branch_id"],
            divergence_step_index=data.get("divergence_step_index"),
            identical_steps_count=data.get("identical_steps_count", 0),
            step_diffs=diffs
        )

    async def get_frame(self, frame_hash: str) -> Optional[Dict[str, Any]]:
        try:
            return await self._request("GET", f"/api/frames/{frame_hash}")
        except Exception:
            return None

    async def get_tenant_usage(self, tenant_id: str) -> TenantUsageMetrics:
        data = await self._request("GET", f"/api/tenants/{tenant_id}/usage")
        return TenantUsageMetrics(
            total_recorded_steps=data.get("total_recorded_steps", 0),
            total_replayed_steps=data.get("total_replayed_steps", 0),
            total_proxied_bytes=data.get("total_proxied_bytes", 0),
            estimated_llm_cost_saved_cents=data.get("estimated_llm_cost_saved_cents", 0)
        )

    async def close(self):
        if self._client:
            await self._client.aclose()

    async def __aenter__(self) -> "AsyncStepLockClient":
        return self

    async def __aexit__(self, exc_type, exc_val, exc_tb):
        await self.close()


ProxyClient = StepLockClient
