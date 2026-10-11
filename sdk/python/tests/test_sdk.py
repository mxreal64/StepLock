import unittest
from steplock.types import (
    ExecutionMode,
    SideEffectType,
    ExecutionFrame,
    ExecutionSession,
    StepDiff,
    BranchDiff,
    TenantUsageMetrics
)
from steplock.client import StepLockClient, StepLockSession

class TestStepLockPythonSDK(unittest.TestCase):
    def test_types_and_defaults(self):
        self.assertEqual(ExecutionMode.RECORD.value, "Record")
        self.assertEqual(SideEffectType.MUTATING.value, 1)
        self.assertEqual(SideEffectType.SAFE_READ_ONLY.value, 0)
        
        session_info = ExecutionSession(
            session_id="sess_1",
            root_branch_id="main",
            active_branch_id="main",
            created_at_utc="2026-10-11T00:00:00Z"
        )
        self.assertEqual(session_info.session_id, "sess_1")

    def test_session_lifecycle_and_headers(self):
        session = StepLockSession(
            session_id="test_sess",
            branch_id="feature",
            execution_mode=ExecutionMode.REPLAY,
            speed_multiplier=2.5,
            auto_advance=True
        )
        
        self.assertEqual(session.session_id, "test_sess")
        self.assertEqual(session.branch_id, "feature")
        self.assertEqual(session.execution_mode, "Replay")
        self.assertEqual(session.step_index, 0)
        
        headers_0 = session.get_headers("https://api.openai.com/v1/chat/completions")
        self.assertEqual(headers_0["X-Agent-Session-ID"], "test_sess")
        self.assertEqual(headers_0["X-Branch-Id"], "feature")
        self.assertEqual(headers_0["X-Step-Index"], "0")
        self.assertEqual(headers_0["X-Execution-Mode"], "Replay")
        self.assertEqual(headers_0["X-Speed-Multiplier"], "2.5")
        self.assertEqual(headers_0["X-Target-Url"], "https://api.openai.com/v1/chat/completions")
        
        # Check auto_advance
        self.assertEqual(session.step_index, 1)
        headers_1 = session.get_headers("https://api.openai.com/v1/chat/completions")
        self.assertEqual(headers_1["X-Step-Index"], "1")
        self.assertEqual(session.step_index, 2)

    def test_context_manager(self):
        with StepLockSession("ctx_sess") as sess:
            self.assertIs(StepLockSession.get_current(), sess)
        self.assertIsNone(StepLockSession.get_current())

    def test_client_initialization(self):
        client = StepLockClient(base_url="http://localhost:5000", timeout=15.0)
        self.assertEqual(client.base_url, "http://localhost:5000")
        self.assertEqual(client.timeout, 15.0)

if __name__ == "__main__":
    unittest.main()
