"""
StepLock Python SDK
Deterministic execution, cryptographic replay, and time-travel infrastructure for AI agents.
"""

from .client import (
    DeterministicSession,
    StepLockSession,
    ProxyClient,
    StepLockClient,
    AsyncStepLockClient,
)
from .patcher import (
    patch_openai,
    patch_environment,
    wrap_openai,
    wrap_anthropic,
    patch_anthropic,
    StepLockHTTPXHook,
    StepLockCallbackHandler,
)
from .types import (
    ExecutionMode,
    SideEffectType,
    StreamChunk,
    ExecutionFrame,
    ExecutionSession,
    StepDiff,
    BranchDiff,
    TenantUsageMetrics,
)

__version__ = "1.1.0"
__all__ = [
    # Client & Session
    "StepLockSession",
    "DeterministicSession",
    "StepLockClient",
    "AsyncStepLockClient",
    "ProxyClient",
    # Patchers & Wrappers
    "patch_openai",
    "patch_environment",
    "wrap_openai",
    "wrap_anthropic",
    "patch_anthropic",
    "StepLockHTTPXHook",
    "StepLockCallbackHandler",
    # Types & Enums
    "ExecutionMode",
    "SideEffectType",
    "StreamChunk",
    "ExecutionFrame",
    "ExecutionSession",
    "StepDiff",
    "BranchDiff",
    "TenantUsageMetrics",
]
