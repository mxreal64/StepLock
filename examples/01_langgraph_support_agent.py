"""
StepLock + LangGraph Example: Deterministic Customer Support Agent
Demonstrates zero-cost deterministic replay & mutation side-effect barriers.
"""

import os
import httpx
from steplock import StepLockSession

# Start a deterministic agent session
with StepLockSession(session_id="customer-support-agent-01", branch_id="main") as session:
    print(f"\n[StepLock] Session active: {session.session_id} on branch: {session.branch_id}")

    # Step 0: Call LLM for triage
    headers = session.get_headers(target_url="https://api.openai.com/v1/chat/completions")
    session.advance_step()

    print("\n--- Step 0: LLM Completion (Safe Read-Only) ---")
    response = httpx.post(
        "http://localhost:5000",
        headers=headers,
        json={
            "model": "gpt-4o",
            "messages": [{"role": "user", "content": "Refund order #452 for $50 due to late delivery."}]
        }
    )
    print("Status:", response.status_code)
    print("Replayed from Cache?", response.headers.get("X-Deterministic-Replay") == "true")
    print("Merkle Frame Hash:", response.headers.get("X-Frame-Hash"))

    # Step 1: Execute Tool (Stripe Charge Mutation - Virtualized during replay!)
    headers = session.get_headers(target_url="https://api.stripe.com/v1/refunds")
    session.advance_step()

    print("\n--- Step 1: Stripe Tool Call (Mutating Side-Effect) ---")
    response = httpx.post(
        "http://localhost:5000",
        headers=headers,
        json={"charge_id": "ch_9921828", "amount": 5000}
    )
    print("Status:", response.status_code)
    print("Was Mutating Call Short-Circuited?", response.headers.get("X-Deterministic-Replay") == "true")
