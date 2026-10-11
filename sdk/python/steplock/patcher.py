import os
from typing import Optional, Any
from .client import StepLockSession


def patch_openai(client_or_class=None, proxy_url: Optional[str] = None):
    """
    Configures OpenAI client or environment to route through the StepLock deterministic proxy.
    """
    proxy_endpoint = proxy_url or os.getenv("STEPLOCK_PROXY_URL", os.getenv("DETERMINISTIC_PROXY_URL", "http://localhost:5000"))
    
    if client_or_class is not None and hasattr(client_or_class, "base_url"):
        client_or_class.base_url = f"{proxy_endpoint.rstrip('/')}/"
    
    os.environ["OPENAI_BASE_URL"] = f"{proxy_endpoint.rstrip('/')}/"
    return client_or_class


def wrap_openai(client: Any) -> Any:
    """
    Wraps an OpenAI client instance to automatically inject StepLock headers
    from active StepLockSession context on every API call.
    """
    original_create = client.chat.completions.create

    def wrapped_create(*args, **kwargs):
        session = StepLockSession.get_current()
        if session:
            extra_headers = kwargs.get("extra_headers") or {}
            control_headers = session.get_headers(target_url="https://api.openai.com/v1/chat/completions")
            extra_headers.update(control_headers)
            kwargs["extra_headers"] = extra_headers
        return original_create(*args, **kwargs)

    client.chat.completions.create = wrapped_create
    return client


def patch_anthropic(client_or_class=None, proxy_url: Optional[str] = None):
    """
    Configures Anthropic client or environment to route through the StepLock deterministic proxy.
    """
    proxy_endpoint = proxy_url or os.getenv("STEPLOCK_PROXY_URL", os.getenv("DETERMINISTIC_PROXY_URL", "http://localhost:5000"))
    
    if client_or_class is not None and hasattr(client_or_class, "base_url"):
        client_or_class.base_url = f"{proxy_endpoint.rstrip('/')}/"
    
    os.environ["ANTHROPIC_BASE_URL"] = f"{proxy_endpoint.rstrip('/')}/"
    return client_or_class


def wrap_anthropic(client: Any) -> Any:
    """
    Wraps an Anthropic client instance to automatically inject StepLock headers
    from active StepLockSession context on every API call.
    """
    original_create = client.messages.create

    def wrapped_create(*args, **kwargs):
        session = StepLockSession.get_current()
        if session:
            extra_headers = kwargs.get("extra_headers") or {}
            control_headers = session.get_headers(target_url="https://api.anthropic.com/v1/messages")
            extra_headers.update(control_headers)
            kwargs["extra_headers"] = extra_headers
        return original_create(*args, **kwargs)

    client.messages.create = wrapped_create
    return client


def patch_environment(proxy_url: Optional[str] = None):
    """
    Enables universal forward proxying (HTTP_PROXY / HTTPS_PROXY) for all networking libraries.
    """
    proxy_endpoint = proxy_url or os.getenv("STEPLOCK_PROXY_URL", os.getenv("DETERMINISTIC_PROXY_URL", "http://localhost:5000"))
    os.environ["HTTP_PROXY"] = proxy_endpoint
    os.environ["HTTPS_PROXY"] = proxy_endpoint
    os.environ["ALL_PROXY"] = proxy_endpoint


class StepLockHTTPXHook:
    """
    HTTPX request event hook that automatically detects active StepLockSession
    and injects deterministic control headers into outgoing requests.
    """

    @staticmethod
    def attach_to_client(client: Any) -> Any:
        session = StepLockSession.get_current()
        if hasattr(client, "event_hooks"):
            async def async_hook(request):
                curr = StepLockSession.get_current()
                if curr:
                    headers = curr.get_headers(str(request.url))
                    for k, v in headers.items():
                        request.headers[k] = v

            def sync_hook(request):
                curr = StepLockSession.get_current()
                if curr:
                    headers = curr.get_headers(str(request.url))
                    for k, v in headers.items():
                        request.headers[k] = v

            if "request" not in client.event_hooks:
                client.event_hooks["request"] = []
            client.event_hooks["request"].append(sync_hook if not hasattr(client, "_transport") or not hasattr(client._transport, "handle_async_request") else async_hook)
        return client


class StepLockCallbackHandler:
    """
    LangChain & LangGraph callback handler for automatic step tracking and DAG recording.
    """

    def __init__(self, session_id: str, branch_id: str = "main", proxy_url: Optional[str] = None):
        self.session = StepLockSession(session_id=session_id, branch_id=branch_id, proxy_url=proxy_url)

    def on_llm_start(self, serialized: Any, prompts: Any, **kwargs: Any) -> Any:
        self.session.advance_step()

    def on_tool_start(self, serialized: Any, input_str: str, **kwargs: Any) -> Any:
        self.session.advance_step()
