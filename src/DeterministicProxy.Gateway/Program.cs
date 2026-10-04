using System.Net.WebSockets;
using DeterministicProxy.Core.Abstractions;
using DeterministicProxy.Core.Canonicalization;
using DeterministicProxy.Core.Cryptography;
using DeterministicProxy.Core.Metrics;
using DeterministicProxy.Core.Models;
using DeterministicProxy.Engine.Replay;
using DeterministicProxy.Engine.Safety;
using DeterministicProxy.Engine.Tls;
using DeterministicProxy.Engine.Wal;
using DeterministicProxy.Engine.WebSocket;
using DeterministicProxy.Gateway.Commercial;
using DeterministicProxy.Gateway.Middleware;
using DeterministicProxy.Storage.Memory;
using DeterministicProxy.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// Register Core Singletons
builder.Services.AddSingleton<IRequestCanonicalizer>(SemanticRequestCanonicalizer.Default);
builder.Services.AddSingleton<IFrameHasher>(FrameHasher.Instance);
builder.Services.AddSingleton<ISideEffectClassifier>(SideEffectSafetyBarrier.Default);
builder.Services.AddSingleton<IReplayEngine>(ReplayEngine.Instance);
builder.Services.AddSingleton<DynamicCertificateAuthority>();

// Register Commercial Multi-Tenancy & Usage Metering
builder.Services.AddSingleton<ITenantStore, InMemoryTenantStore>();
builder.Services.AddSingleton<IUsageMeteringService, UsageMeteringService>();

// Persistence Store (SQLite WAL Mode)
var dbPath = builder.Configuration.GetValue<string>("DatabasePath") ?? "execution_traces.db";
var sqliteStore = new SqliteExecutionStore(dbPath);
builder.Services.AddSingleton<IExecutionStore>(sqliteStore);

// Background WAL Persistence Worker
builder.Services.AddSingleton<BackgroundWalQueue>();
builder.Services.AddSingleton<IWalQueue>(sp => sp.GetRequiredService<BackgroundWalQueue>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackgroundWalQueue>());

// Upstream HTTP Connection Pool with SocketsHttpHandler tuning
builder.Services.AddHttpClient("UpstreamClient", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    MaxConnectionsPerServer = 1000,
    EnableMultipleHttp2Connections = true
});

var app = builder.Build();

// Enable Static Files for Visual Dashboard
app.UseDefaultFiles();
app.UseStaticFiles();

// Enable WebSockets for CDP & Duplex protocols
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// Dashboard redirect
app.MapGet("/dashboard", () => Results.Redirect("/index.html"));

// Health & Diagnostics
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    runtime = ".NET 11.0",
    engine = "StepLock Enterprise",
    timestamp = DateTimeOffset.UtcNow
}));

// Control Plane API: List All Sessions
app.MapGet("/api/sessions", async (IExecutionStore store) =>
{
    var sessions = await store.ListSessionsAsync();
    return Results.Ok(new { total = sessions.Count, sessions });
});

// Control Plane API: Execution History
app.MapGet("/api/sessions/{sessionId}/history", async (string sessionId, string? branchId, IExecutionStore store) =>
{
    var branch = branchId ?? "main";
    var history = await store.GetExecutionHistoryAsync(sessionId, branch);
    return Results.Ok(new { session_id = sessionId, branch_id = branch, total_steps = history.Count, frames = history });
});

// Control Plane API: Verify Merkle DAG Integrity
app.MapGet("/api/sessions/{sessionId}/verify-dag", async (string sessionId, string? branchId, IExecutionStore store) =>
{
    var branch = branchId ?? "main";
    var isIntact = await store.VerifyDagIntegrityAsync(sessionId, branch);
    return Results.Ok(new { session_id = sessionId, branch_id = branch, is_valid_dag = isIntact });
});

// Control Plane API: Branch Regression Diffing
app.MapGet("/api/sessions/{sessionId}/compare-branches", async (string sessionId, string baseBranch, string targetBranch, IExecutionStore store) =>
{
    var diff = await store.CompareBranchesAsync(sessionId, baseBranch, targetBranch);
    return Results.Ok(diff);
});

// Control Plane API: Frame Lookup by Hash
app.MapGet("/api/frames/{frameHash}", async (string frameHash, IExecutionStore store) =>
{
    var frame = await store.GetFrameByHashAsync(frameHash);
    return frame != null ? Results.Ok(frame) : Results.NotFound(new { error = "FrameNotFound" });
});

// Control Plane API: Tenant Usage Metering
app.MapGet("/api/tenants/{tenantId}/usage", (string tenantId, IUsageMeteringService metering) =>
{
    var usage = metering.GetUsage(tenantId);
    return Results.Ok(usage);
});

// Control Plane API: Branch Forking
app.MapPost("/api/sessions/fork", async (ForkRequest request, IExecutionStore store) =>
{
    var sourceFrames = await store.GetExecutionHistoryAsync(request.SessionId, request.SourceBranchId);
    var framesToCopy = sourceFrames.Where(f => f.StepIndex <= request.ForkAtStepIndex).ToList();

    var forkedFrames = new List<ExecutionFrame>(framesToCopy.Count);
    foreach (var frame in framesToCopy)
    {
        var forkedFrame = frame with { BranchId = request.NewBranchId };
        forkedFrames.Add(forkedFrame);
    }

    if (forkedFrames.Count > 0)
    {
        await store.SaveFramesBatchAsync(forkedFrames);
    }

    var existingSession = await store.GetSessionAsync(request.SessionId);
    var newSession = new ExecutionSession(
        SessionId: request.SessionId,
        RootBranchId: request.SourceBranchId,
        ActiveBranchId: request.NewBranchId,
        CreatedAtUtc: DateTimeOffset.UtcNow,
        Metadata: existingSession?.Metadata ?? new Dictionary<string, string>()
    );
    await store.SaveSessionAsync(newSession);

    return Results.Ok(new
    {
        message = "BranchCreated",
        session_id = request.SessionId,
        new_branch_id = request.NewBranchId,
        copied_steps = forkedFrames.Count
    });
});

// WebSocket / CDP Proxy Endpoint
app.Map("/ws/cdp", async (HttpContext context, ILogger<Program> logger) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var targetWsUri = context.Request.Query["target"].FirstOrDefault() ?? "ws://localhost:9222/devtools/browser";
        using var clientWs = await context.WebSockets.AcceptWebSocketAsync();
        using var upstreamWs = new ClientWebSocket();

        try
        {
            await upstreamWs.ConnectAsync(new Uri(targetWsUri), context.RequestAborted);
            var capturedFrames = await WebSocketInterceptor.ProxyAndRecordAsync(clientWs, upstreamWs, context.RequestAborted);
            logger.LogInformation("Captured {Count} CDP frames for browser session", capturedFrames.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to proxy CDP websocket session");
        }
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

// Deterministic Proxy Interceptor Middleware
app.UseMiddleware<DeterministicProxyMiddleware>();

PrintBanner();

app.Run();

static void PrintBanner()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("""
     ███████╗████████╗███████╗██████╗ ██╗      ██████╗  ██████╗██╗  ██╗
     ██╔════╝╚══██╔══╝██╔════╝██╔══██╗██║     ██╔═══██╗██╔════╝██║ ██╔╝
     ███████╗   ██║   █████╗  ██████╔╝██║     ██║   ██║██║     █████╔╝ 
     ╚════██║   ██║   ██╔══╝  ██╔═══╝ ██║     ██║   ██║██║     ██╔═██╗ 
     ███████║   ██║   ███████╗██║     ███████╗╚██████╔╝╚██████╗██║  ██╗
     ╚══════╝   ╚═╝   ╚══════╝╚═╝     ╚══════╝ ╚═════╝  ╚═════╝╚═╝  ╚═╝
    """);
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine(" ⚡ StepLock: Git for Live Agent Execution RAM & Network Traffic");
    Console.ResetColor();
    Console.WriteLine(" ──────────────────────────────────────────────────────────────────");
    Console.WriteLine(" • Dashboard UI:      http://localhost:5000/dashboard");
    Console.WriteLine(" • Health Probe:      http://localhost:5000/health");
    Console.WriteLine(" • Python SDK:        pip install steplock");
    Console.WriteLine(" • Open-Core Mode:    Active (Community Core + Enterprise Ready)");
    Console.WriteLine(" ──────────────────────────────────────────────────────────────────\n");
}

public sealed record ForkRequest(string SessionId, string SourceBranchId, string NewBranchId, int ForkAtStepIndex);

public partial class Program { }
