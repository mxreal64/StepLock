using System.Text.Json.Serialization;

namespace DeterministicProxy.Core.Models;

/// <summary>
/// A single token or data chunk in a chunked/SSE stream.
/// </summary>
public sealed record StreamChunk(
    [property: JsonPropertyName("seq")] int Sequence,
    [property: JsonPropertyName("delta_ms")] long DeltaMs,
    [property: JsonPropertyName("data")] byte[] Payload
);

public enum ExecutionMode
{
    Auto,         // Replay if matching step exists, otherwise record live
    Record,       // Always execute live and overwrite/record
    Replay,       // Strictly serve from cache; fail if step missing
    Fork          // Create a new branch starting from this step
}

public enum SideEffectType
{
    SafeReadOnly,   // Safe to replay (GET, LLM completions, read DB queries)
    Mutating,       // Modifies real-world state (charges, emails, DB writes) - short-circuited in replay
    IdempotentWrite // Safe to execute if idempotency key is preserved
}

/// <summary>
/// Captured WebSocket / CDP Frame
/// </summary>
public sealed record WebSocketCapturedFrame(
    [property: JsonPropertyName("seq")] int Sequence,
    [property: JsonPropertyName("timestamp_ms")] long TimestampMs,
    [property: JsonPropertyName("is_client_to_server")] bool IsClientToServer,
    [property: JsonPropertyName("message_type")] string MessageType, // Text / Binary
    [property: JsonPropertyName("payload")] string Payload
);

/// <summary>
/// Immutable snapshot of an agent execution step.
/// </summary>
public sealed record ExecutionFrame(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("branch_id")] string BranchId,
    [property: JsonPropertyName("step_index")] int StepIndex,
    [property: JsonPropertyName("step_id")] string StepId,
    [property: JsonPropertyName("parent_step_hash")] string? ParentStepHash,
    [property: JsonPropertyName("frame_hash")] string FrameHash,
    [property: JsonPropertyName("http_method")] string HttpMethod,
    [property: JsonPropertyName("target_uri")] string TargetUri,
    [property: JsonPropertyName("request_headers")] Dictionary<string, string> RequestHeaders,
    [property: JsonPropertyName("request_body_hash")] string RequestBodyHash,
    [property: JsonPropertyName("request_body_canonical")] byte[]? RequestBodyCanonical,
    [property: JsonPropertyName("response_status")] int ResponseStatusCode,
    [property: JsonPropertyName("response_headers")] Dictionary<string, string> ResponseHeaders,
    [property: JsonPropertyName("chunks")] List<StreamChunk> Chunks,
    [property: JsonPropertyName("ws_frames")] List<WebSocketCapturedFrame>? WebSocketFrames,
    [property: JsonPropertyName("side_effect_type")] SideEffectType SideEffectType,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("ttft_ms")] long? TimeToFirstTokenMs,
    [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc
);

/// <summary>
/// Session metadata and active branch pointers.
/// </summary>
public sealed record ExecutionSession(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("root_branch_id")] string RootBranchId,
    [property: JsonPropertyName("active_branch_id")] string ActiveBranchId,
    [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("metadata")] Dictionary<string, string> Metadata
);

/// <summary>
/// Structural diff between two execution branches for regression debugging.
/// </summary>
public sealed record BranchDiff(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("base_branch_id")] string BaseBranchId,
    [property: JsonPropertyName("target_branch_id")] string TargetBranchId,
    [property: JsonPropertyName("divergence_step_index")] int? DivergenceStepIndex,
    [property: JsonPropertyName("identical_steps_count")] int IdenticalStepsCount,
    [property: JsonPropertyName("step_diffs")] List<StepDiff> StepDiffs
);

public sealed record StepDiff(
    [property: JsonPropertyName("step_index")] int StepIndex,
    [property: JsonPropertyName("base_frame_hash")] string? BaseFrameHash,
    [property: JsonPropertyName("target_frame_hash")] string? TargetFrameHash,
    [property: JsonPropertyName("is_match")] bool IsMatch,
    [property: JsonPropertyName("base_target_uri")] string? BaseTargetUri,
    [property: JsonPropertyName("target_target_uri")] string? TargetTargetUri,
    [property: JsonPropertyName("status_code_changed")] bool StatusCodeChanged
);

public sealed record TenantUsageMetrics(
    [property: JsonPropertyName("total_recorded_steps")] long TotalRecordedSteps,
    [property: JsonPropertyName("total_replayed_steps")] long TotalReplayedSteps,
    [property: JsonPropertyName("total_proxied_bytes")] long TotalProxiedBytes,
    [property: JsonPropertyName("estimated_llm_cost_saved_cents")] long EstimatedLlmCostSavedCents
);

public interface IUsageMeteringService
{
    void RecordStep(string tenantId, bool isReplay, long bytesTransferred, long durationMs);
    TenantUsageMetrics GetUsage(string tenantId);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StreamChunk))]
[JsonSerializable(typeof(List<StreamChunk>))]
[JsonSerializable(typeof(WebSocketCapturedFrame))]
[JsonSerializable(typeof(List<WebSocketCapturedFrame>))]
[JsonSerializable(typeof(ExecutionFrame))]
[JsonSerializable(typeof(List<ExecutionFrame>))]
[JsonSerializable(typeof(ExecutionSession))]
[JsonSerializable(typeof(List<ExecutionSession>))]
[JsonSerializable(typeof(BranchDiff))]
[JsonSerializable(typeof(StepDiff))]
[JsonSerializable(typeof(List<StepDiff>))]
[JsonSerializable(typeof(TenantUsageMetrics))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class DeterministicProxyJsonContext : JsonSerializerContext
{
}

