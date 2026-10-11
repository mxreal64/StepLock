import { StepLockSession } from "./session.ts";

export interface InterceptorOptions {
  session?: StepLockSession;
  proxyUrl?: string;
}

/**
 * Creates a custom fetch wrapper that redirects outbound LLM / API requests
 * through the StepLock deterministic proxy gateway, injecting necessary session headers.
 */
export function createStepLockFetch(
  options: InterceptorOptions = {},
  baseFetch: typeof fetch = globalThis.fetch
): typeof fetch {
  return async function stepLockFetch(
    input: RequestInfo | URL,
    init?: RequestInit
  ): Promise<Response> {
    const session = options.session || StepLockSession.getCurrent();
    const proxyUrl = options.proxyUrl || session?.proxyUrl || "http://localhost:5000";

    const targetUrl = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;

    const modifiedHeaders = new Headers(init?.headers || {});
    if (session) {
      const stepHeaders = session.getHeaders(targetUrl);
      for (const [k, v] of Object.entries(stepHeaders)) {
        modifiedHeaders.set(k, v);
      }
    } else {
      modifiedHeaders.set("X-Target-Url", targetUrl);
    }

    return baseFetch(proxyUrl, {
      ...init,
      headers: modifiedHeaders,
    });
  };
}

/**
 * Intercepts and wraps an OpenAI JavaScript/TypeScript SDK instance.
 * Sets baseURL to the StepLock gateway and injects session headers via defaultHeaders or custom fetch.
 */
export function wrapOpenAI<T extends { baseURL?: string; defaultHeaders?: Record<string, string> }>(
  client: T,
  session?: StepLockSession,
  proxyUrl?: string
): T {
  const active = session || StepLockSession.getCurrent();
  const targetProxy = proxyUrl || active?.proxyUrl || "http://localhost:5000";
  
  if (client.baseURL !== undefined) {
    client.baseURL = targetProxy;
  }
  
  if (active) {
    client.defaultHeaders = {
      ...(client.defaultHeaders || {}),
      "X-Agent-Session-ID": active.sessionId,
      "X-Branch-Id": active.branchId,
      "X-Execution-Mode": active.executionMode,
    };
  }
  return client;
}

/**
 * Intercepts and wraps an Anthropic TypeScript/JavaScript SDK instance.
 */
export function wrapAnthropic<T extends { baseURL?: string; defaultHeaders?: Record<string, string> }>(
  client: T,
  session?: StepLockSession,
  proxyUrl?: string
): T {
  return wrapOpenAI(client, session, proxyUrl);
}
