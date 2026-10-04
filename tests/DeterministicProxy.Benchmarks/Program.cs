using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using DeterministicProxy.Core.Canonicalization;
using DeterministicProxy.Core.Cryptography;
using DeterministicProxy.Core.Models;
using DeterministicProxy.Engine.Pipelines;
using DeterministicProxy.Engine.Replay;
using DeterministicProxy.Engine.Safety;
using DeterministicProxy.Engine.Tls;
using DeterministicProxy.Storage.Memory;
using DeterministicProxy.Storage.Sqlite;

Console.WriteLine("==========================================================================================");
Console.WriteLine("             STEPLOCK OPEN SOURCE COMMUNITY CORE PERFORMANCE BENCHMARKS                   ");
Console.WriteLine($"        Runtime: {Environment.Version} | OS: {Environment.OSVersion} | Cores: {Environment.ProcessorCount}");
Console.WriteLine("==========================================================================================\n");

var results = new List<BenchmarkResult>();

// 1. Merkle DAG Cryptographic Hashing
results.Add(Benchmark("Merkle DAG Hashing (SHA256 Chaining)", iterations: 50_000, () =>
{
    var hasher = FrameHasher.Instance;
    var parent = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
    var uri = "https://api.openai.com/v1/chat/completions?model=gpt-4o";
    var bodyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    _ = hasher.ComputeFrameHash(parent, "POST", uri, bodyHash, 42);
}));

// 2. Semantic JSON Canonicalization (Property Sorting + Masking)
var sampleJson = Encoding.UTF8.GetBytes("""
{
    "messages": [
        {"role": "system", "content": "You are a helpful coding assistant."},
        {"role": "user", "content": "Write a high-performance HTTP proxy in C# using Pipelines."}
    ],
    "client_timestamp": 1727938800000,
    "nonce": "9f83-4a12-8c9e-2231",
    "request_id": "req_8832a91f",
    "model": "gpt-4o",
    "temperature": 0.2,
    "stream": true
}
""");
results.Add(Benchmark("Semantic Request Canonicalizer (JSON)", iterations: 20_000, () =>
{
    var canonicalizer = SemanticRequestCanonicalizer.Default;
    _ = canonicalizer.CanonicalizeBody("application/json", sampleJson);
}));

// 3. Duplex Streaming Tap (System.IO.Pipelines Zero-Copy Throughput)
var sseChunkData = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Hello World!\"}}]}\n\n");
results.Add(Benchmark("Zero-Copy Streaming Tap (100 Chunks / Call)", iterations: 2_000, () =>
{
    using var stream = new MemoryStream(sseChunkData);
    var pipe = new Pipe();
    var task = DuplexStreamingTap.InterceptAndForwardAsync(stream, pipe.Writer);
    task.GetAwaiter().GetResult();
    pipe.Writer.Complete();
    var readerTask = pipe.Reader.ReadAsync();
    var result = readerTask.GetAwaiter().GetResult();
    pipe.Reader.AdvanceTo(result.Buffer.End);
    pipe.Reader.Complete();
}));

// 4. PII & Secret Redaction (Regex Engine with ReDoS Protection)
var piiSample = """
{
    "authorization": "Bearer sk-proj-ab1234567890abcdef1234567890123456",
    "stripe_key": "rk_live_998877665544332211001122",
    "aws_key": "AKIAIOSFODNN7EXAMPLE",
    "card": "4111-2222-3333-4444",
    "user": "Alice Johnson",
    "query": "Please charge the card for order #9921"
}
""";
results.Add(Benchmark("PII & Secret Redaction Engine", iterations: 20_000, () =>
{
    _ = EnterprisePiiRedactor.Instance.Redact(piiSample);
}));

// 5. Dynamic TLS CA Certificate Generation (ECDsa P-256)
using var ca = new DynamicCertificateAuthority();
results.Add(Benchmark("Dynamic TLS Leaf Cert Generation (ECDsa P-256)", iterations: 500, () =>
{
    var domain = $"api-test-{Guid.NewGuid():N}.openai.com";
    using var cert = ca.GetOrCreateDomainCertificate(domain);
}));

// 6. In-Memory Execution Store (SortedList + ReaderWriterLock)
var memoryStore = new MemoryExecutionStore();
var testFrame = CreateDummyFrame("session-bench-mem", "main", 0);
results.Add(Benchmark("In-Memory Store (Write + Hash Lookup)", iterations: 50_000, () =>
{
    memoryStore.SaveFrameAsync(testFrame).GetAwaiter().GetResult();
    _ = memoryStore.GetFrameByHashAsync(testFrame.FrameHash).GetAwaiter().GetResult();
}));

// 7. SQLite WAL Mode Batch Write (50 Frames / Transaction)
var dbPath = $"bench_traces_{Guid.NewGuid():N}.db";
var sqliteStore = new SqliteExecutionStore(dbPath);
sqliteStore.InitializeAsync().GetAwaiter().GetResult();
var batchFrames = Enumerable.Range(0, 50).Select(i => CreateDummyFrame("session-sqlite-batch", "main", i)).ToList();
results.Add(Benchmark("SQLite WAL Batch Persistence (50 Frames/Tx)", iterations: 200, () =>
{
    sqliteStore.SaveFramesBatchAsync(batchFrames).GetAwaiter().GetResult();
}));
sqliteStore.DisposeAsync().GetAwaiter().GetResult();
try { File.Delete(dbPath); } catch { }

// Print Formatted Report Table
Console.WriteLine("\n========================================================================================================================");
Console.WriteLine(string.Format("{0,-48} | {1,12} | {2,10} | {3,10} | {4,10} | {5,12}", "Benchmark Target", "Throughput", "p50 (µs)", "p95 (µs)", "p99 (µs)", "Alloc (B/op)"));
Console.WriteLine("------------------------------------------------------------------------------------------------------------------------");

foreach (var r in results)
{
    Console.WriteLine(string.Format("{0,-48} | {1,9:N0} op/s | {2,8:N2} µs | {3,8:N2} µs | {4,8:N2} µs | {5,10:N0} B",
        r.Name, r.OpsPerSec, r.P50Us, r.P95Us, r.P99Us, r.AllocatedBytesPerOp));
}
Console.WriteLine("========================================================================================================================\n");

static BenchmarkResult Benchmark(string name, int iterations, Action action)
{
    // Warmup
    for (int i = 0; i < Math.Min(iterations / 10 + 1, 50); i++) action();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var latenciesUs = new double[iterations];
    var startMemory = GC.GetAllocatedBytesForCurrentThread();
    var sw = new Stopwatch();

    var totalSw = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++)
    {
        sw.Restart();
        action();
        sw.Stop();
        latenciesUs[i] = sw.Elapsed.TotalMicroseconds;
    }
    totalSw.Stop();
    var totalAllocated = GC.GetAllocatedBytesForCurrentThread() - startMemory;

    Array.Sort(latenciesUs);
    var p50 = latenciesUs[(int)(iterations * 0.50)];
    var p95 = latenciesUs[(int)(iterations * 0.95)];
    var p99 = latenciesUs[(int)(iterations * 0.99)];
    var opsPerSec = iterations / totalSw.Elapsed.TotalSeconds;
    var allocPerOp = (double)totalAllocated / iterations;

    return new BenchmarkResult(name, iterations, opsPerSec, p50, p95, p99, allocPerOp);
}

static ExecutionFrame CreateDummyFrame(string sessionId, string branchId, int stepIndex)
{
    return new ExecutionFrame(
        SessionId: sessionId,
        BranchId: branchId,
        StepIndex: stepIndex,
        StepId: Guid.NewGuid().ToString("N"),
        ParentStepHash: stepIndex > 0 ? "parent_hash_000" : null,
        FrameHash: $"hash_{sessionId}_{branchId}_{stepIndex}_{Guid.NewGuid():N}",
        HttpMethod: "POST",
        TargetUri: "https://api.openai.com/v1/chat/completions",
        RequestHeaders: new() { ["Content-Type"] = "application/json" },
        RequestBodyHash: "body_hash_123456",
        RequestBodyCanonical: Encoding.UTF8.GetBytes("{\"model\":\"gpt-4o\"}"),
        ResponseStatusCode: 200,
        ResponseHeaders: new() { ["Content-Type"] = "text/event-stream" },
        Chunks: new()
        {
            new StreamChunk(0, 10, Encoding.UTF8.GetBytes("data: {\"token\":\"A\"}\n\n")),
            new StreamChunk(1, 25, Encoding.UTF8.GetBytes("data: {\"token\":\"B\"}\n\n"))
        },
        WebSocketFrames: null,
        SideEffectType: SideEffectType.SafeReadOnly,
        DurationMs: 35,
        TimeToFirstTokenMs: 10,
        CreatedAtUtc: DateTimeOffset.UtcNow
    );
}

sealed record BenchmarkResult(
    string Name,
    int Iterations,
    double OpsPerSec,
    double P50Us,
    double P95Us,
    double P99Us,
    double AllocatedBytesPerOp
);
