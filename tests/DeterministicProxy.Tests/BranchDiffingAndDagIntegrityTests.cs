using DeterministicProxy.Core.Cryptography;
using DeterministicProxy.Core.Models;
using DeterministicProxy.Storage.Memory;
using FluentAssertions;
using Xunit;

namespace DeterministicProxy.Tests;

public class BranchDiffingAndDagIntegrityTests
{
    private readonly MemoryExecutionStore _store = new();
    private readonly FrameHasher _hasher = FrameHasher.Instance;

    [Fact]
    public async Task VerifyDagIntegrity_ShouldPassForValidMerkleChain()
    {
        var sessionId = "dag-session-1";
        var branchId = "main";

        // Step 0
        var hash0 = _hasher.ComputeFrameHash(null, "POST", "https://api.openai.com/v1/chat/completions", "hash0", 0);
        var frame0 = CreateTestFrame(sessionId, branchId, 0, null, hash0, "https://api.openai.com/v1/chat/completions", "hash0");
        await _store.SaveFrameAsync(frame0);

        // Step 1
        var hash1 = _hasher.ComputeFrameHash(hash0, "POST", "https://api.stripe.com/v1/customers", "hash1", 1);
        var frame1 = CreateTestFrame(sessionId, branchId, 1, hash0, hash1, "https://api.stripe.com/v1/customers", "hash1");
        await _store.SaveFrameAsync(frame1);

        var isValid = await _store.VerifyDagIntegrityAsync(sessionId, branchId);
        isValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyDagIntegrity_ShouldFailForTamperedFrameHash()
    {
        var sessionId = "tamper-session";
        var branchId = "main";

        // Intentionally use wrong frame hash to simulate tampering
        var frame0 = CreateTestFrame(sessionId, branchId, 0, null, "tampered-hash-xyz", "https://api.openai.com/v1/chat/completions", "hash0");
        await _store.SaveFrameAsync(frame0);

        var isValid = await _store.VerifyDagIntegrityAsync(sessionId, branchId);
        isValid.Should().BeFalse();
    }

    [Fact]
    public async Task CompareBranches_ShouldDetectDivergenceStep()
    {
        var sessionId = "diff-session-1";

        // Step 0 (Shared across main and experiment)
        var hash0 = _hasher.ComputeFrameHash(null, "POST", "https://api.openai.com/v1/chat/completions", "hash0", 0);
        var frame0_main = CreateTestFrame(sessionId, "main", 0, null, hash0, "https://api.openai.com/v1/chat/completions", "hash0");
        var frame0_exp = CreateTestFrame(sessionId, "experiment", 0, null, hash0, "https://api.openai.com/v1/chat/completions", "hash0");
        await _store.SaveFrameAsync(frame0_main);
        await _store.SaveFrameAsync(frame0_exp);

        // Step 1 in main
        var hash1_main = _hasher.ComputeFrameHash(hash0, "POST", "https://api.stripe.com/v1/charges", "chargeA", 1);
        var frame1_main = CreateTestFrame(sessionId, "main", 1, hash0, hash1_main, "https://api.stripe.com/v1/charges", "chargeA");
        await _store.SaveFrameAsync(frame1_main);

        // Step 1 in experiment (different prompt -> different target/body)
        var hash1_exp = _hasher.ComputeFrameHash(hash0, "POST", "https://api.stripe.com/v1/invoices", "invoiceB", 1);
        var frame1_exp = CreateTestFrame(sessionId, "experiment", 1, hash0, hash1_exp, "https://api.stripe.com/v1/invoices", "invoiceB");
        await _store.SaveFrameAsync(frame1_exp);

        var diff = await _store.CompareBranchesAsync(sessionId, "main", "experiment");

        diff.IdenticalStepsCount.Should().Be(1);
        diff.DivergenceStepIndex.Should().Be(1);
        diff.StepDiffs.Should().HaveCount(2);
        diff.StepDiffs[0].IsMatch.Should().BeTrue();
        diff.StepDiffs[1].IsMatch.Should().BeFalse();
    }

    [Fact]
    public async Task CompareBranches_IdenticalBranches_ShouldHaveZeroDivergence()
    {
        var sessionId = "identical-session";

        var hash0 = _hasher.ComputeFrameHash(null, "POST", "https://api.openai.com/v1/chat/completions", "body0", 0);
        var frame_main = CreateTestFrame(sessionId, "branch-a", 0, null, hash0, "https://api.openai.com/v1/chat/completions", "body0");
        var frame_exp = CreateTestFrame(sessionId, "branch-b", 0, null, hash0, "https://api.openai.com/v1/chat/completions", "body0");
        await _store.SaveFrameAsync(frame_main);
        await _store.SaveFrameAsync(frame_exp);

        var diff = await _store.CompareBranchesAsync(sessionId, "branch-a", "branch-b");
        diff.DivergenceStepIndex.Should().BeNull();
        diff.IdenticalStepsCount.Should().Be(1);
        diff.StepDiffs.All(s => s.IsMatch).Should().BeTrue();
    }

    private static ExecutionFrame CreateTestFrame(
        string sessionId, string branchId, int stepIndex, string? parentHash, string frameHash, string targetUri, string bodyHash)
    {
        return new ExecutionFrame(
            SessionId: sessionId,
            BranchId: branchId,
            StepIndex: stepIndex,
            StepId: Guid.NewGuid().ToString("N"),
            ParentStepHash: parentHash,
            FrameHash: frameHash,
            HttpMethod: "POST",
            TargetUri: targetUri,
            RequestHeaders: new(),
            RequestBodyHash: bodyHash,
            RequestBodyCanonical: null,
            ResponseStatusCode: 200,
            ResponseHeaders: new(),
            Chunks: new(),
            WebSocketFrames: null,
            SideEffectType: SideEffectType.SafeReadOnly,
            DurationMs: 45,
            TimeToFirstTokenMs: 12,
            CreatedAtUtc: DateTimeOffset.UtcNow
        );
    }
}
