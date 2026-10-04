"""
Deterministic State Proxy - Python / LangGraph Integration Example
Demonstrates how multi-step agents hook into the Deterministic State Proxy.
"""

import os
import httpx
from typing import Dict, Any

PROXY_BASE_URL = os.getenv("DETERMINISTIC_PROXY_URL", "http://localhost:5000")

class DeterministicAgentSession:
    def __init__(self, session_id: str, branch_id: str = "main", execution_mode: str = "Auto"):
        self.session_id = session_id
        self.branch_id = branch_id
        self.execution_mode = execution_mode
        self.step_index = 0

    def get_headers(self, target_url: str) -> Dict[str, str]:
        """Headers required by the C# Deterministic Proxy."""
        headers = {
            "X-Agent-Session-ID": self.session_id,
            "X-Branch-Id": self.branch_id,
            "X-Step-Index": str(self.step_index),
            "X-Execution-Mode": self.execution_mode,
            "X-Target-Url": target_url,
        }
        return headers

    def call_tool(self, target_url: str, method: str = "POST", json_body: Dict[str, Any] = None):
        headers = self.get_headers(target_url)
        with httpx.Client() as client:
            response = client.request(
                method=method,
                url=f"{PROXY_BASE_URL}",
                headers=headers,
                json=json_body
            )
            # Advance step counter upon completion
            self.step_index += 1
            return response.json(), response.headers.get("X-Deterministic-Replay") == "true"


if __name__ == "__main__":
    print("Deterministic Proxy Client initialized.")
