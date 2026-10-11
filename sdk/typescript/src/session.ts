import { ExecutionMode, SessionOptions } from "./types.ts";

/**
 * Global and context storage fallback for environments without AsyncLocalStorage
 */
let activeSession: StepLockSession | null = null;

/**
 * StepLockSession represents an active agent run session.
 * Tracks deterministic step sequence and injects canonical gateway headers.
 */
export class StepLockSession {
  public readonly sessionId: string;
  public readonly branchId: string;
  public readonly executionMode: string;
  public readonly speedMultiplier: number;
  public readonly proxyUrl: string;
  public readonly autoAdvance: boolean;
  public stepIndex: number;

  constructor(options: SessionOptions) {
    this.sessionId = options.sessionId;
    this.branchId = options.branchId || "main";
    this.executionMode = options.executionMode || ExecutionMode.AUTO;
    this.speedMultiplier = options.speedMultiplier ?? 0.0;
    this.proxyUrl = options.proxyUrl || (typeof process !== "undefined" && (process.env?.STEPLOCK_PROXY_URL || process.env?.DETERMINISTIC_PROXY_URL)) || "http://localhost:5000";
    this.autoAdvance = options.autoAdvance ?? false;
    this.stepIndex = 0;
  }

  /**
   * Returns the current ambient StepLockSession.
   */
  public static getCurrent(): StepLockSession | null {
    return activeSession;
  }

  /**
   * Sets or clears the current active StepLockSession.
   */
  public static setCurrent(session: StepLockSession | null): void {
    activeSession = session;
  }

  /**
   * Executes a synchronous or asynchronous callback within the context of this session.
   */
  public async run<T>(fn: (session: StepLockSession) => Promise<T> | T): Promise<T> {
    const prev = activeSession;
    activeSession = this;
    try {
      return await fn(this);
    } finally {
      activeSession = prev;
    }
  }

  /**
   * Increments and returns the previous step index.
   */
  public advanceStep(): number {
    const current = this.stepIndex;
    this.stepIndex += 1;
    return current;
  }

  /**
   * Returns canonical HTTP headers to route upstream calls through the StepLock Gateway.
   */
  public getHeaders(targetUrl: string): Record<string, string> {
    const idx = this.autoAdvance ? this.advanceStep() : this.stepIndex;
    return {
      "X-Agent-Session-ID": this.sessionId,
      "X-Branch-Id": this.branchId,
      "X-Step-Index": idx.toString(),
      "X-Execution-Mode": this.executionMode,
      "X-Speed-Multiplier": this.speedMultiplier.toString(),
      "X-Target-Url": targetUrl,
    };
  }
}

export const DeterministicSession = StepLockSession;
