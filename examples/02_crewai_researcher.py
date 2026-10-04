"""
StepLock + CrewAI / Multi-Agent Collaboration
Captures multi-agent conversation tokens and intermediate tool reasoning into a Merkle DAG.
"""

import httpx
from steplock import StepLockSession

with StepLockSession(session_id="crewai-deep-research", branch_id="main") as session:
    print(f"[StepLock] Multi-Agent Session: {session.session_id}")

    # Agent A: Research Specialist
    headers = session.get_headers(target_url="https://api.anthropic.com/v1/messages")
    session.advance_step()
    resp_a = httpx.post(
        "http://localhost:5000",
        headers=headers,
        json={"model": "claude-3-5-sonnet", "messages": [{"role": "user", "content": "Analyze quantum computing trends"}]}
    )
    print(f"Step 0 (Researcher) - Frame Hash: {resp_a.headers.get('X-Frame-Hash')}")

    # Agent B: Technical Writer
    headers = session.get_headers(target_url="https://api.openai.com/v1/chat/completions")
    session.advance_step()
    resp_b = httpx.post(
        "http://localhost:5000",
        headers=headers,
        json={"model": "gpt-4o", "messages": [{"role": "user", "content": "Summarize findings into bullet points"}]}
    )
    print(f"Step 1 (Writer) - Frame Hash: {resp_b.headers.get('X-Frame-Hash')}")
