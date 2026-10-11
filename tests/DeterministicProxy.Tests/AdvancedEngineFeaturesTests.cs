using System.IO.Pipelines;
using System.Text;
using DeterministicProxy.Core.Models;
using DeterministicProxy.Engine.Archival;
using DeterministicProxy.Engine.Diffing;
using DeterministicProxy.Engine.Replay;
using DeterministicProxy.Engine.Safety;
using DeterministicProxy.Storage.Memory;
using FluentAssertions;
using Xunit;

namespace DeterministicProxy.Tests;

public class AdvancedEngineFeaturesTests
{
    private static ExecutionFrame CreateTestFrame(
        string sessionId = "diff_sess",
        string branchId = "main",
        int stepIndex = 0,
        string frameHash = "hash_0",
        string bodyHash = "body_hash_0",
        byte[]? bodyCanonical = null)
    {
        return new ExecutionFrame(
            SessionId: sessionId,
            BranchId: branchId,
            StepIndex: stepIndex,
            StepId: $"step_{stepIndex}",
            ParentStepHash: null,
            FrameHash: frameHash,
            HttpMethod: "POST",
            TargetUri: "https://api.openai.com/v1/chat/completions",
            RequestHeaders: new Dictionary<string, string>(),
            RequestBodyHash: bodyHash,
            RequestBodyCanonical: bodyCanonical ?? Encoding.UTF8.GetBytes("{\"prompt\":\"hello\"}"),
            ResponseStatusCode: 200,
            ResponseHeaders: new Dictionary<string, string>(),
            Chunks: new List<StreamChunk>(),
            WebSocketFrames: null,
            SideEffectType: SideEffectType.SafeReadOnly,
            DurationMs: 100,
            TimeToFirstTokenMs: 25,
            CreatedAtUtc: DateTimeOffset.UtcNow
        );
    }

    [Fact]
    public async Task VirtualTimingEngine_ShouldReplayWithJitterSimulation()
    {
        var engine = new VirtualTimingEngine();
        var pipe = new Pipe();

        var chunks = new List<StreamChunk>
        {
            new(0, 0, Encoding.UTF8.GetBytes("data: Hello\n\n")),
            new(1, 20, Encoding.UTF8.GetBytes("data: World\n\n"))
        };

        var replayTask = engine.ReplayWithJitterSimulationAsync(chunks, pipe.Writer, speedMultiplier: 100.0, jitterVarianceMs: 1.0);

        var readerTask = Task.Run(async () =>
        {
            var result = await pipe.Reader.ReadAsync();
            var text = Encoding.UTF8.GetString(result.Buffer);
            pipe.Reader.AdvanceTo(result.Buffer.End);
            return text;
        });

        await Task.WhenAll(replayTask, readerTask);
        var output = await readerTask;
        output.Should().Contain("Hello");
        output.Should().Contain("World");
    }

    [Fact]
    public void SideEffectBarrier_ShouldGenerateSyntheticMockResponse()
    {
        var barrier = new SideEffectBarrier();
        var result = barrier.GenerateVirtualResponse(
            "POST",
            "https://api.stripe.com/v1/charges",
            new Dictionary<string, string>()
        );

        result.WasShortCircuited.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.Headers.Should().ContainKey("X-Deterministic-Virtualized");
        var body = Encoding.UTF8.GetString(result.SyntheticResponseBody);
        body.Should().Contain("virtualized_mock_success");
    }

    [Fact]
    public async Task BranchRegressionDiffEngine_ShouldDetectDivergence()
    {
        var store = new MemoryExecutionStore();
        var diffEngine = new BranchRegressionDiffEngine(store);

        var frameBase = CreateTestFrame(
            sessionId: "diff_sess",
            branchId: "main",
            stepIndex: 0,
            frameHash: "hash_0",
            bodyHash: "body_hash_0",
            bodyCanonical: Encoding.UTF8.GetBytes("{\"prompt\":\"hello\"}")
        );

        var frameTarget = CreateTestFrame(
            sessionId: "diff_sess",
            branchId: "experiment",
            stepIndex: 0,
            frameHash: "hash_0_tweaked",
            bodyHash: "body_hash_1",
            bodyCanonical: Encoding.UTF8.GetBytes("{\"prompt\":\"world\"}")
        );

        await store.SaveFrameAsync(frameBase);
        await store.SaveFrameAsync(frameTarget);

        var report = await diffEngine.AnalyzeRegressionAsync("diff_sess", "main", "experiment");

        report.SessionId.Should().Be("diff_sess");
        report.DivergenceStepIndex.Should().Be(0);
        report.IdenticalSteps.Should().Be(0);
        report.Details.Should().HaveCount(1);
        report.Details[0].IsMatch.Should().BeFalse();
        report.Details[0].DivergenceReason.Should().Contain("Prompt or Request payload content diverged");
    }

    [Fact]
    public async Task L3CloudArchiveService_ShouldExportAndImportDpzBundle()
    {
        var store = new MemoryExecutionStore();
        var cloud = new InMemoryCloudStorageProvider();
        var archiveService = new L3CloudArchiveService(store, cloud);

        var frame = CreateTestFrame(
            sessionId: "arch_sess",
            branchId: "main",
            stepIndex: 0,
            frameHash: "hash_arch"
        );

        await store.SaveFrameAsync(frame);

        var manifest = await archiveService.ExportSessionToCloudAsync("arch_sess", "main");
        manifest.TotalFrames.Should().Be(1);
        manifest.ArchiveSha256.Should().NotBeNullOrWhiteSpace();

        // New empty store importing the bundle
        var secondStore = new MemoryExecutionStore();
        var secondArchiveService = new L3CloudArchiveService(secondStore, cloud);

        var importedCount = await secondArchiveService.ImportSessionFromCloudAsync("arch_sess", "main");
        importedCount.Should().Be(1);

        var importedFrames = await secondStore.GetExecutionHistoryAsync("arch_sess", "main");
        importedFrames.Should().HaveCount(1);
        importedFrames[0].FrameHash.Should().Be("hash_arch");
    }
}
