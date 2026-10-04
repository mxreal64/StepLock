using DeterministicProxy.Core.Models;
using DeterministicProxy.Storage.Sqlite;
using FluentAssertions;
using Xunit;

namespace DeterministicProxy.Tests;

public class StorageAndBranchingTests : IAsyncLifetime
{
    private readonly string _dbPath = $"test_traces_{Guid.NewGuid():N}.db";
    private SqliteExecutionStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteExecutionStore(_dbPath);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public async Task SaveAndRetrieveFrame_ShouldPersistCorrectly()
    {
        var frame = new ExecutionFrame(
            SessionId: "session-test-1",
            BranchId: "main",
            StepIndex: 0,
            StepId: "step-001",
            ParentStepHash: null,
            FrameHash: "hash-001",
            HttpMethod: "POST",
            TargetUri: "https://api.openai.com/v1/chat/completions",
            RequestHeaders: new() { ["Authorization"] = "Bearer token" },
            RequestBodyHash: "bodyhash123",
            RequestBodyCanonical: null,
            ResponseStatusCode: 200,
            ResponseHeaders: new() { ["Content-Type"] = "application/json" },
            Chunks: new() { new StreamChunk(0, 10, System.Text.Encoding.UTF8.GetBytes("{\"id\":\"chatcmpl-1\"}")) },
            WebSocketFrames: null,
            SideEffectType: SideEffectType.SafeReadOnly,
            DurationMs: 120,
            TimeToFirstTokenMs: 25,
            CreatedAtUtc: DateTimeOffset.UtcNow
        );

        await _store.SaveFrameAsync(frame);

        var retrieved = await _store.GetFrameByStepIndexAsync("session-test-1", "main", 0);
        retrieved.Should().NotBeNull();
        retrieved!.FrameHash.Should().Be("hash-001");
        retrieved.Chunks.Should().HaveCount(1);
        retrieved.DurationMs.Should().Be(120);
        retrieved.TimeToFirstTokenMs.Should().Be(25);
    }

    [Fact]
    public async Task GetFrameByHash_ShouldFindFrameByContentHash()
    {
        var frame = new ExecutionFrame(
            SessionId: "session-hash-test",
            BranchId: "main",
            StepIndex: 0,
            StepId: "step-hash-01",
            ParentStepHash: null,
            FrameHash: "unique-frame-hash-xyz",
            HttpMethod: "GET",
            TargetUri: "https://api.openai.com/v1/models",
            RequestHeaders: new(),
            RequestBodyHash: "emptyhash",
            RequestBodyCanonical: null,
            ResponseStatusCode: 200,
            ResponseHeaders: new(),
            Chunks: new(),
            WebSocketFrames: null,
            SideEffectType: SideEffectType.SafeReadOnly,
            DurationMs: 50,
            TimeToFirstTokenMs: null,
            CreatedAtUtc: DateTimeOffset.UtcNow
        );
        await _store.SaveFrameAsync(frame);

        var retrieved = await _store.GetFrameByHashAsync("unique-frame-hash-xyz");
        retrieved.Should().NotBeNull();
        retrieved!.TargetUri.Should().Be("https://api.openai.com/v1/models");
    }

    [Fact]
    public async Task SaveFramesBatch_ShouldPersistAllFrames()
    {
        var frames = Enumerable.Range(0, 5).Select(i => new ExecutionFrame(
            SessionId: "batch-session",
            BranchId: "main",
            StepIndex: i,
            StepId: $"step-{i}",
            ParentStepHash: i == 0 ? null : $"hash-{i - 1}",
            FrameHash: $"hash-{i}",
            HttpMethod: "POST",
            TargetUri: "https://api.openai.com/v1/chat/completions",
            RequestHeaders: new(),
            RequestBodyHash: $"body-{i}",
            RequestBodyCanonical: null,
            ResponseStatusCode: 200,
            ResponseHeaders: new(),
            Chunks: new(),
            WebSocketFrames: null,
            SideEffectType: SideEffectType.SafeReadOnly,
            DurationMs: 10 * i,
            TimeToFirstTokenMs: null,
            CreatedAtUtc: DateTimeOffset.UtcNow
        )).ToList();

        await _store.SaveFramesBatchAsync(frames);

        var history = await _store.GetExecutionHistoryAsync("batch-session", "main");
        history.Should().HaveCount(5);
        history.Select(f => f.StepIndex).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ListSessions_ShouldReturnSavedSessions()
    {
        var session = new ExecutionSession(
            SessionId: "list-test-session",
            RootBranchId: "main",
            ActiveBranchId: "main",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Metadata: new() { ["agent"] = "test" }
        );
        await _store.SaveSessionAsync(session);

        var sessions = await _store.ListSessionsAsync();
        sessions.Should().Contain(s => s.SessionId == "list-test-session");
    }
}
