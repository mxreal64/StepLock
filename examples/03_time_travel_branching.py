"""
StepLock Time-Travel & Prompt Regression Diffing
Forks a failed agent session at Step 2 into a new experiment branch and analyzes divergence.
"""

from steplock import StepLockClient

client = StepLockClient("http://localhost:5000")

session_id = "customer-support-agent-01"

# 1. Verify DAG integrity of main branch
is_valid = client.verify_dag(session_id, branch_id="main")
print(f"[StepLock] Main DAG Cryptographic Integrity: {is_valid}")

# 2. Fork into a new experiment branch at step 1
fork_res = client.fork_branch(
    session_id=session_id,
    source_branch="main",
    new_branch="experiment-tweaked-prompt",
    fork_at_step=1
)
print(f"[StepLock] Fork Created: {fork_res}")

# 3. Compare branches to highlight where prompts or responses diverged
diff = client.compare_branches(
    session_id=session_id,
    base_branch="main",
    target_branch="experiment-tweaked-prompt"
)
print(f"[StepLock] Identical Steps: {diff.get('identical_steps_count')}")
print(f"[StepLock] Divergence Step Index: {diff.get('divergence_step_index')}")
