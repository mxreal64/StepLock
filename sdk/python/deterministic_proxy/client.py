import os
import contextvars
import json
import urllib.request
import urllib.parse
from typing import Optional, Dict, Any, List

_current_session = contextvars.ContextVar("current_steplock_session", default=None)


class StepLockSession:
    """
    Context manager representing a deterministic agent execution session in StepLock.
    Tracks step index and produces canonical proxy headers.
    """

    def __init__(
        self,
        session_id: str,
        branch_id: str = "main",
        execution_mode: str = "Auto",
        speed_multiplier: float = 0.0,
        proxy_url: Optional[str] = None
    ):
        self.session_id = session_id
        self.branch_id = branch_id
        self.execution_mode = execution_mode
        self.speed_multiplier = speed_multiplier
        self.proxy_url = proxy_url or os.getenv("STEPLOCK_PROXY_URL", os.getenv("DETERMINISTIC_PROXY_URL", "http://localhost:5000"))
        self.step_index = 0
        self._token = None

    def __enter__(self) -> "StepLockSession":
        self._token = _current_session.set(self)
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self._token:
            _current_session.reset(self._token)

    @classmethod
    def get_current(cls) -> Optional["StepLockSession"]:
        """Retrieve active StepLock session from current context."""
        return _current_session.get()

    def advance_step(self) -> int:
        """Increment and return the previous step index."""
        current = self.step_index
        self.step_index += 1
        return current

    def get_headers(self, target_url: str) -> Dict[str, str]:
        """Generate StepLock proxy control headers for the given upstream target."""
        return {
            "X-Agent-Session-ID": self.session_id,
            "X-Branch-Id": self.branch_id,
            "X-Step-Index": str(self.step_index),
            "X-Execution-Mode": self.execution_mode,
            "X-Speed-Multiplier": str(self.speed_multiplier),
            "X-Target-Url": target_url,
        }


DeterministicSession = StepLockSession


class StepLockClient:
    """
    Client for controlling and inspecting the StepLock Execution Gateway.
    Provides session management, DAG integrity verification, and time-travel branch forking.
    Supports httpx when available with automatic urllib standard-library fallback.
    """

    def __init__(self, base_url: str = "http://localhost:5000", timeout: float = 30.0):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    def _request(self, method: str, path: str, params: Optional[Dict[str, Any]] = None, json_body: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        try:
            import httpx
            with httpx.Client(timeout=self.timeout) as client:
                url = f"{self.base_url}{path}"
                resp = client.request(method, url, params=params, json=json_body)
                resp.raise_for_status()
                return resp.json()
        except ImportError:
            url = f"{self.base_url}{path}"
            if params:
                query = urllib.parse.urlencode(params)
                url = f"{url}?{query}"
            
            data = json.dumps(json_body).encode("utf-8") if json_body is not None else None
            req = urllib.request.Request(url, data=data, method=method)
            if data:
                req.add_header("Content-Type", "application/json")
            
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                return json.loads(resp.read().decode("utf-8"))

    def list_sessions(self) -> List[Dict[str, Any]]:
        """List all active agent execution sessions."""
        data = self._request("GET", "/api/sessions")
        return data.get("sessions", [])

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
    ) -> Dict[str, Any]:
        """Compare two execution branches to pinpoint where and why agent execution diverged."""
        params = {
            "sessionId": session_id,
            "baseBranch": base_branch,
            "targetBranch": target_branch
        }
        return self._request("GET", f"/api/sessions/{session_id}/compare-branches", params=params)

    def get_frame(self, frame_hash: str) -> Optional[Dict[str, Any]]:
        """Look up a specific execution frame by its cryptographic hash."""
        try:
            return self._request("GET", f"/api/frames/{frame_hash}")
        except Exception:
            return None

    def get_tenant_usage(self, tenant_id: str) -> Dict[str, Any]:
        """Retrieve usage metering metrics for a tenant."""
        return self._request("GET", f"/api/tenants/{tenant_id}/usage")


ProxyClient = StepLockClient
