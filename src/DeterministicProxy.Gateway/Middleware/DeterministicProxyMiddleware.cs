using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using DeterministicProxy.Core.Abstractions;
using DeterministicProxy.Core.Metrics;
using DeterministicProxy.Core.Models;
using DeterministicProxy.Engine.Pipelines;
using DeterministicProxy.Engine.Replay;
using DeterministicProxy.Engine.Safety;
using DeterministicProxy.Engine.Wal;
using DeterministicProxy.Gateway.Commercial;

namespace DeterministicProxy.Gateway.Middleware;

public sealed class DeterministicProxyMiddleware
{
    private static readonly HashSet<string> ProxyControlHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Agent-Session-ID",
        "X-Branch-Id",
        "X-Step-Index",
        "X-Execution-Mode",
        "X-Speed-Multiplier",
        "X-Target-Url",
        "X-Virtual-Time"
    };

    private readonly RequestDelegate _next;
    private readonly IExecutionStore _store;
    private readonly IWalQueue _walQueue;
    private readonly IRequestCanonicalizer _canonicalizer;
    private readonly IFrameHasher _hasher;
    private readonly ISideEffectClassifier _classifier;
    private readonly IReplayEngine _replayEngine;
    private readonly IUsageMeteringService _metering;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DeterministicProxyMiddleware> _logger;

    public DeterministicProxyMiddleware(
        RequestDelegate next,
        IExecutionStore store,
        IWalQueue walQueue,
        IRequestCanonicalizer canonicalizer,
        IFrameHasher hasher,
        ISideEffectClassifier classifier,
        IReplayEngine replayEngine,
        IUsageMeteringService metering,
        IHttpClientFactory httpClientFactory,
        ILogger<DeterministicProxyMiddleware> logger)
    {
        _next = next;
        _store = store;
        _walQueue = walQueue;
        _canonicalizer = canonicalizer;
        _hasher = hasher;
        _classifier = classifier;
        _replayEngine = replayEngine;
        _metering = metering;
        _httpClient = httpClientFactory.CreateClient("UpstreamClient");
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Check if managed proxy request
        if (!context.Request.Headers.TryGetValue("X-Agent-Session-ID", out var sessionIdVal) ||
            string.IsNullOrWhiteSpace(sessionIdVal))
        {
            // Unmanaged pass-through (e.g. /health, /api, or standard routes)
            await _next(context);
            return;
        }

        var totalStopwatch = Stopwatch.StartNew();
        var sessionId = sessionIdVal.ToString();
        var branchId = context.Request.Headers.TryGetValue("X-Branch-Id", out var branchVal) && !string.IsNullOrWhiteSpace(branchVal)
            ? branchVal.ToString()
            : "main";

        int stepIndex = 0;
        if (context.Request.Headers.TryGetValue("X-Step-Index", out var stepIndexVal) &&
            int.TryParse(stepIndexVal, out var parsedIndex))
        {
            stepIndex = parsedIndex;
        }

        var modeStr = context.Request.Headers["X-Execution-Mode"].FirstOrDefault() ?? "Auto";
        var mode = Enum.TryParse<ExecutionMode>(modeStr, true, out var parsedMode) ? parsedMode : ExecutionMode.Auto;

        double speedMultiplier = 0.0; // Default: instant replay
        if (context.Request.Headers.TryGetValue("X-Speed-Multiplier", out var speedVal) &&
            double.TryParse(speedVal, out var parsedSpeed))
        {
            speedMultiplier = parsedSpeed;
        }

        // Determine Target URI
        string targetUri;
        if (context.Request.Headers.TryGetValue("X-Target-Url", out var targetHeader) && !string.IsNullOrWhiteSpace(targetHeader))
        {
            targetUri = targetHeader.ToString();
        }
        else
        {
            targetUri = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}";
        }

        // 1. Read & Canonicalize Request Body
        context.Request.EnableBuffering();
        using var bodyStream = new MemoryStream();
        await context.Request.Body.CopyToAsync(bodyStream);
        context.Request.Body.Position = 0;
        var rawBodyBytes = bodyStream.ToArray();

        var (bodyHash, canonicalBytes) = _canonicalizer.CanonicalizeBody(context.Request.ContentType, rawBodyBytes);
        var canonicalHeaders = _canonicalizer.CanonicalizeHeaders(context.Request.Headers.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value)));

        // 2. Fetch Parent Hash for Merkle Tree chaining (look up stepIndex - 1 with fallback to latest)
        string? parentHash = null;
        if (stepIndex > 0)
        {
            var parentFrame = await _store.GetFrameByStepIndexAsync(sessionId, branchId, stepIndex - 1);
            parentHash = parentFrame?.FrameHash;
            if (parentHash == null)
            {
                parentHash = await _store.GetLatestStepHashAsync(sessionId, branchId);
            }
        }

        var frameHash = _hasher.ComputeFrameHash(parentHash, context.Request.Method, targetUri, bodyHash, stepIndex);
        var sideEffectType = _classifier.Classify(context.Request.Method, targetUri, canonicalHeaders);

        // 3. Execution Mode Evaluation
        ExecutionFrame? existingFrame = null;
        if (mode is ExecutionMode.Auto or ExecutionMode.Replay)
        {
            existingFrame = await _store.GetFrameByStepIndexAsync(sessionId, branchId, stepIndex);
        }

        if (existingFrame != null && mode != ExecutionMode.Record)
        {
            // --- REPLAY MODE ---
            ProxyDiagnostics.ReplayedStepsCounter.Add(1);
            if (existingFrame.SideEffectType == SideEffectType.Mutating)
            {
                ProxyDiagnostics.ShortCircuitedMutationsCounter.Add(1);
                _logger.LogInformation("Short-circuiting mutating side-effect for session {SessionId}, step {StepIndex}", sessionId, stepIndex);
            }

            await ServeReplayFrameAsync(context, existingFrame, speedMultiplier);
            totalStopwatch.Stop();
            var durationMs = totalStopwatch.ElapsedMilliseconds;
            ProxyDiagnostics.StepLatencyHistogram.Record(durationMs);

            long totalBytes = existingFrame.Chunks.Sum(c => (long)c.Payload.Length);
            _metering.RecordStep(sessionId, isReplay: true, bytesTransferred: totalBytes, durationMs: durationMs);
            return;
        }

        if (mode == ExecutionMode.Replay && existingFrame == null)
        {
            context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "StepNotFoundInReplayMode",
                message = $"No recorded frame found for session '{sessionId}', branch '{branchId}', step {stepIndex}."
            });
            return;
        }

        // --- LIVE EXECUTION & ASYNC WAL RECORDING ---
        ProxyDiagnostics.RecordedStepsCounter.Add(1);
        await ExecuteLiveAndRecordAsync(
            context,
            sessionId,
            branchId,
            stepIndex,
            parentHash,
            frameHash,
            targetUri,
            rawBodyBytes,
            canonicalBytes,
            bodyHash,
            canonicalHeaders,
            sideEffectType,
            totalStopwatch);
    }

    private async Task ServeReplayFrameAsync(HttpContext context, ExecutionFrame frame, double speedMultiplier)
    {
        context.Response.StatusCode = frame.ResponseStatusCode;
        foreach (var header in frame.ResponseHeaders)
        {
            context.Response.Headers[header.Key] = header.Value;
        }

        context.Response.Headers["X-Deterministic-Replay"] = "true";
        context.Response.Headers["X-Frame-Hash"] = frame.FrameHash;

        await _replayEngine.ReplayChunksAsync(frame.Chunks, context.Response.BodyWriter, speedMultiplier, context.RequestAborted);
    }

    private async Task ExecuteLiveAndRecordAsync(
        HttpContext context,
        string sessionId,
        string branchId,
        int stepIndex,
        string? parentHash,
        string frameHash,
        string targetUri,
        byte[] rawBodyBytes,
        byte[] canonicalBytes,
        string bodyHash,
        Dictionary<string, string> canonicalHeaders,
        SideEffectType sideEffectType,
        Stopwatch totalStopwatch)
    {
        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

        if (rawBodyBytes.Length > 0)
        {
            upstreamRequest.Content = new ByteArrayContent(rawBodyBytes);
            if (!string.IsNullOrEmpty(context.Request.ContentType))
            {
                upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }
        }

        foreach (var header in context.Request.Headers)
        {
            // Only skip proxy-internal control headers; forward legitimate headers (X-Api-Key, etc.)
            if (ProxyControlHeaders.Contains(header.Key)) continue;
            upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await _httpClient.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to upstream {TargetUri}", targetUri);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new { error = "UpstreamConnectionFailed", details = ex.Message });
            return;
        }

        using (upstreamResponse)
        {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;

            var responseHeaders = new Dictionary<string, string>();
            foreach (var h in upstreamResponse.Headers)
            {
                var val = string.Join(",", h.Value);
                context.Response.Headers[h.Key] = val;
                responseHeaders[h.Key] = val;
            }
            foreach (var h in upstreamResponse.Content.Headers)
            {
                var val = string.Join(",", h.Value);
                context.Response.Headers[h.Key] = val;
                responseHeaders[h.Key] = val;
            }

            context.Response.Headers["X-Frame-Hash"] = frameHash;
            context.Response.Headers["X-Deterministic-Recorded"] = "true";

            // Duplex streaming tap
            using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
            var chunks = await DuplexStreamingTap.InterceptAndForwardAsync(upstreamStream, context.Response.BodyWriter, context.RequestAborted);

            totalStopwatch.Stop();
            var durationMs = totalStopwatch.ElapsedMilliseconds;
            var ttftMs = chunks.Count > 0 ? (long?)chunks[0].DeltaMs : null;

            if (ttftMs.HasValue)
            {
                ProxyDiagnostics.TimeToFirstTokenHistogram.Record(ttftMs.Value);
            }
            ProxyDiagnostics.StepLatencyHistogram.Record(durationMs);

            long totalBytes = chunks.Sum(c => (long)c.Payload.Length);
            _metering.RecordStep(sessionId, isReplay: false, bytesTransferred: totalBytes, durationMs: durationMs);

            // Construct immutable Merkle Frame
            var frame = new ExecutionFrame(
                SessionId: sessionId,
                BranchId: branchId,
                StepIndex: stepIndex,
                StepId: Guid.NewGuid().ToString("N"),
                ParentStepHash: parentHash,
                FrameHash: frameHash,
                HttpMethod: context.Request.Method,
                TargetUri: targetUri,
                RequestHeaders: canonicalHeaders,
                RequestBodyHash: bodyHash,
                RequestBodyCanonical: canonicalBytes,
                ResponseStatusCode: (int)upstreamResponse.StatusCode,
                ResponseHeaders: responseHeaders,
                Chunks: chunks,
                WebSocketFrames: null,
                SideEffectType: sideEffectType,
                DurationMs: durationMs,
                TimeToFirstTokenMs: ttftMs,
                CreatedAtUtc: DateTimeOffset.UtcNow
            );

            // High-throughput non-blocking enqueue to background WAL persistence
            await _walQueue.EnqueueFrameAsync(frame, context.RequestAborted);
        }
    }
}
