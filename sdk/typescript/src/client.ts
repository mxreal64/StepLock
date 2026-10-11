import {
  ClientOptions,
  ExecutionSession,
  BranchDiff,
  TenantUsageMetrics,
  ExecutionFrame,
} from "./types.ts";

/**
 * StepLockClient communicates with the StepLock Gateway API.
 * Provides cluster status, session inspection, cryptographic DAG verification,
 * time-travel branch diffing, and usage metrics.
 */
export class StepLockClient {
  public readonly baseUrl: string;
  public readonly timeoutMs: number;
  private readonly fetchFn: typeof fetch;

  constructor(options: ClientOptions = {}) {
    this.baseUrl = (options.baseUrl || "http://localhost:5000").replace(/\/+$/, "");
    this.timeoutMs = options.timeoutMs ?? 30000;
    this.fetchFn = options.fetchFn || globalThis.fetch;
  }

  private async request<T>(
    method: string,
    path: string,
    params?: Record<string, string | number | boolean | undefined>,
    body?: unknown
  ): Promise<T> {
    let url = `${this.baseUrl}${path}`;
    if (params) {
      const searchParams = new URLSearchParams();
      for (const [key, val] of Object.entries(params)) {
        if (val !== undefined) {
          searchParams.append(key, String(val));
        }
      }
      const qs = searchParams.toString();
      if (qs) {
        url += `?${qs}`;
      }
    }

    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.timeoutMs);

    try {
      const headers: Record<string, string> = {};
      let bodyStr: string | undefined;

      if (body !== undefined) {
        headers["Content-Type"] = "application/json";
        bodyStr = JSON.stringify(body);
      }

      const res = await this.fetchFn(url, {
        method,
        headers,
        body: bodyStr,
        signal: controller.signal,
      });

      if (!res.ok) {
        const errText = await res.text().catch(() => "");
        throw new Error(`StepLock HTTP ${res.status} (${res.statusText}): ${errText}`);
      }

      return (await res.json()) as T;
    } finally {
      clearTimeout(timer);
    }
  }

  /**
   * Check gateway health and engine build info.
   */
  public async health(): Promise<{ status: string; engine_version?: string }> {
    return this.request<{ status: string; engine_version?: string }>("GET", "/health");
  }

  /**
   * List all registered execution sessions.
   */
  public async listSessions(): Promise<Array<{ session_id: string; created_at_utc: string }>> {
    const data = await this.request<{ sessions: Array<{ session_id: string; created_at_utc: string }> }>(
      "GET",
      "/api/sessions"
    );
    return data.sessions || [];
  }

  /**
   * Retrieve session metadata.
   */
  public async getSession(sessionId: string): Promise<ExecutionSession | null> {
    try {
      return await this.request<ExecutionSession>("GET", `/api/sessions/${encodeURIComponent(sessionId)}`);
    } catch {
      return null;
    }
  }

  /**
   * Retrieve frame history for a session and branch.
   */
  public async getHistory(sessionId: string, branchId = "main"): Promise<{ frames: ExecutionFrame[] }> {
    return this.request<{ frames: ExecutionFrame[] }>(
      "GET",
      `/api/sessions/${encodeURIComponent(sessionId)}/history`,
      { branchId }
    );
  }

  /**
   * Verify cryptographic Merkle DAG integrity for the specified session and branch.
   */
  public async verifyDag(sessionId: string, branchId = "main"): Promise<boolean> {
    const res = await this.request<{ is_valid_dag: boolean }>(
      "GET",
      `/api/sessions/${encodeURIComponent(sessionId)}/verify-dag`,
      { branchId }
    );
    return res.is_valid_dag ?? false;
  }

  /**
   * Fork execution branch at a specific step index.
   */
  public async forkBranch(
    sessionId: string,
    sourceBranch: string,
    newBranch: string,
    forkAtStep: number
  ): Promise<{ session_id: string; branch_id: string; forked_at_step: number }> {
    return this.request("POST", "/api/sessions/fork", undefined, {
      sessionId,
      sourceBranchId: sourceBranch,
      newBranchId: newBranch,
      forkAtStepIndex: forkAtStep,
    });
  }

  /**
   * Compare two execution branches to identify step divergence.
   */
  public async compareBranches(
    sessionId: string,
    baseBranch: string,
    targetBranch: string
  ): Promise<BranchDiff> {
    return this.request<BranchDiff>(
      "GET",
      `/api/sessions/${encodeURIComponent(sessionId)}/compare-branches`,
      {
        sessionId,
        baseBranch,
        targetBranch,
      }
    );
  }

  /**
   * Look up an execution frame by its SHA-256 Merkle hash.
   */
  public async getFrame(frameHash: string): Promise<ExecutionFrame | null> {
    try {
      return await this.request<ExecutionFrame>("GET", `/api/frames/${encodeURIComponent(frameHash)}`);
    } catch {
      return null;
    }
  }

  /**
   * Retrieve usage metrics and estimated cost savings for a tenant.
   */
  public async getTenantUsage(tenantId: string): Promise<TenantUsageMetrics> {
    return this.request<TenantUsageMetrics>(
      "GET",
      `/api/tenants/${encodeURIComponent(tenantId)}/usage`
    );
  }
}

export const ProxyClient = StepLockClient;
