import os
from typing import Optional
from .client import DeterministicSession

def patch_openai(client_or_class=None, proxy_url: Optional[str] = None):
    """
    Configures OpenAI client to route through the deterministic proxy.
    """
    proxy_endpoint = proxy_url or os.getenv("DETERMINISTIC_PROXY_URL", "http://localhost:5000")
    
    if client_or_class is not None and hasattr(client_or_class, "base_url"):
        client_or_class.base_url = f"{proxy_endpoint}/"
    
    os.environ["OPENAI_BASE_URL"] = f"{proxy_endpoint}/"
    return client_or_class

def patch_environment(proxy_url: Optional[str] = None):
    """
    Enables universal forward proxying (HTTP_PROXY / HTTPS_PROXY) for all libraries.
    """
    proxy_endpoint = proxy_url or os.getenv("DETERMINISTIC_PROXY_URL", "http://localhost:5000")
    os.environ["HTTP_PROXY"] = proxy_endpoint
    os.environ["HTTPS_PROXY"] = proxy_endpoint
    os.environ["ALL_PROXY"] = proxy_endpoint
