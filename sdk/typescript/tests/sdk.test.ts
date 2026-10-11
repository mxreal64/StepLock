import { assertEquals } from "jsr:@std/assert";
import {
  ExecutionMode,
  SideEffectType,
  StepLockSession,
  StepLockClient,
  wrapOpenAI,
  createStepLockFetch,
} from "../src/index.ts";

Deno.test("StepLock Types and Enums", () => {
  assertEquals(ExecutionMode.RECORD, "Record");
  assertEquals(ExecutionMode.REPLAY, "Replay");
  assertEquals(SideEffectType.MUTATING, 1);
  assertEquals(SideEffectType.SAFE_READ_ONLY, 0);
});

Deno.test("StepLockSession headers and autoAdvance", () => {
  const session = new StepLockSession({
    sessionId: "ts_session_42",
    branchId: "eval-branch",
    executionMode: ExecutionMode.REPLAY,
    speedMultiplier: 10,
    autoAdvance: true,
  });

  assertEquals(session.sessionId, "ts_session_42");
  assertEquals(session.branchId, "eval-branch");
  assertEquals(session.stepIndex, 0);

  const headers0 = session.getHeaders("https://api.openai.com/v1/chat/completions");
  assertEquals(headers0["X-Agent-Session-ID"], "ts_session_42");
  assertEquals(headers0["X-Branch-Id"], "eval-branch");
  assertEquals(headers0["X-Step-Index"], "0");
  assertEquals(headers0["X-Execution-Mode"], "Replay");
  assertEquals(headers0["X-Speed-Multiplier"], "10");
  assertEquals(headers0["X-Target-Url"], "https://api.openai.com/v1/chat/completions");

  // Step index should have incremented
  assertEquals(session.stepIndex, 1);

  const headers1 = session.getHeaders("https://api.openai.com/v1/chat/completions");
  assertEquals(headers1["X-Step-Index"], "1");
  assertEquals(session.stepIndex, 2);
});

Deno.test("StepLockSession run scope context", async () => {
  const session = new StepLockSession({ sessionId: "scoped_sess" });
  assertEquals(StepLockSession.getCurrent(), null);

  await session.run(async (active) => {
    assertEquals(StepLockSession.getCurrent(), active);
    assertEquals(active.sessionId, "scoped_sess");
  });

  assertEquals(StepLockSession.getCurrent(), null);
});

Deno.test("StepLockClient configuration", () => {
  const client = new StepLockClient({
    baseUrl: "http://localhost:5000",
    timeoutMs: 5000,
  });
  assertEquals(client.baseUrl, "http://localhost:5000");
  assertEquals(client.timeoutMs, 5000);
});

Deno.test("wrapOpenAI intercepts and configures client headers", () => {
  const session = new StepLockSession({
    sessionId: "openai_sess",
    branchId: "main",
    executionMode: ExecutionMode.RECORD,
  });

  const mockOpenAIClient: { baseURL?: string; defaultHeaders?: Record<string, string> } = {
    baseURL: "https://api.openai.com/v1",
    defaultHeaders: {},
  };

  wrapOpenAI(mockOpenAIClient, session, "http://127.0.0.1:5000");

  assertEquals(mockOpenAIClient.baseURL, "http://127.0.0.1:5000");
  assertEquals(mockOpenAIClient.defaultHeaders?.["X-Agent-Session-ID"], "openai_sess");
  assertEquals(mockOpenAIClient.defaultHeaders?.["X-Execution-Mode"], "Record");
});

Deno.test("createStepLockFetch rewrites URL and passes headers", async () => {
  let capturedUrl = "";
  let capturedHeaders: Headers | undefined;

  const mockFetch: typeof fetch = async (input, init) => {
    capturedUrl = typeof input === "string" ? input : (input as Request).url;
    capturedHeaders = new Headers(init?.headers);
    return new Response(JSON.stringify({ ok: true }), { status: 200 });
  };

  const session = new StepLockSession({
    sessionId: "fetch_sess",
    executionMode: ExecutionMode.REPLAY,
  });

  const customFetch = createStepLockFetch({ session, proxyUrl: "http://proxy.local:5000" }, mockFetch);

  const res = await customFetch("https://api.anthropic.com/v1/messages", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
  });

  assertEquals(res.status, 200);
  assertEquals(capturedUrl, "http://proxy.local:5000");
  assertEquals(capturedHeaders?.get("X-Target-Url"), "https://api.anthropic.com/v1/messages");
  assertEquals(capturedHeaders?.get("X-Agent-Session-ID"), "fetch_sess");
  assertEquals(capturedHeaders?.get("X-Execution-Mode"), "Replay");
});
