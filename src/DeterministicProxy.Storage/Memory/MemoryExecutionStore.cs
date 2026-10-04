using System.Collections.Concurrent;
using DeterministicProxy.Core.Abstractions;
using DeterministicProxy.Core.Models;

namespace DeterministicProxy.Storage.Memory;

public sealed class MemoryExecutionStore : IExecutionStore
{
    private readonly struct BranchKey : IEquatable<BranchKey>
    {
        public readonly string SessionId;
        public readonly string BranchId;

        public BranchKey(string sessionId, string branchId)
        {
            SessionId = sessionId;
            BranchId = branchId;
        }

        public bool Equals(BranchKey other) =>
            string.Equals(SessionId, other.SessionId, StringComparison.Ordinal) &&
            string.Equals(BranchId, other.BranchId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is BranchKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(SessionId),
                StringComparer.Ordinal.GetHashCode(BranchId));
    }

    private readonly ConcurrentDictionary<string, ExecutionFrame> _framesByHash = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<BranchKey, BranchState> _branchFrames = new();
    private readonly ConcurrentDictionary<string, ExecutionSession> _sessions = new(StringComparer.Ordinal);

    private BranchState GetOrCreateBranch(string sessionId, string branchId)
    {
        return _branchFrames.GetOrAdd(new BranchKey(sessionId, branchId), static _ => new BranchState());
    }

    public ValueTask SaveFrameAsync(ExecutionFrame frame, CancellationToken ct = default)
    {
        _framesByHash[frame.FrameHash] = frame;
        var branch = GetOrCreateBranch(frame.SessionId, frame.BranchId);

        lock (branch.Gate)
        {
            branch.FramesByIndex[frame.StepIndex] = frame;
            branch.LatestHash = frame.FrameHash;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SaveFramesBatchAsync(IReadOnlyList<ExecutionFrame> frames, CancellationToken ct = default)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            _framesByHash[frame.FrameHash] = frame;
            var branch = GetOrCreateBranch(frame.SessionId, frame.BranchId);

            lock (branch.Gate)
            {
                branch.FramesByIndex[frame.StepIndex] = frame;
                branch.LatestHash = frame.FrameHash;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ExecutionFrame?> GetFrameByStepIndexAsync(string sessionId, string branchId, int stepIndex, CancellationToken ct = default)
    {
        if (_branchFrames.TryGetValue(new BranchKey(sessionId, branchId), out var branch))
        {
            lock (branch.Gate)
            {
                if (branch.FramesByIndex.TryGetValue(stepIndex, out var frame))
                {
                    return ValueTask.FromResult<ExecutionFrame?>(frame);
                }
            }
        }
        return ValueTask.FromResult<ExecutionFrame?>(null);
    }

    public ValueTask<ExecutionFrame?> GetFrameByHashAsync(string frameHash, CancellationToken ct = default)
    {
        _framesByHash.TryGetValue(frameHash, out var frame);
        return ValueTask.FromResult(frame);
    }

    public ValueTask<string?> GetLatestStepHashAsync(string sessionId, string branchId, CancellationToken ct = default)
    {
        if (_branchFrames.TryGetValue(new BranchKey(sessionId, branchId), out var branch))
        {
            return ValueTask.FromResult(branch.LatestHash);
        }
        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask<IReadOnlyList<ExecutionFrame>> GetExecutionHistoryAsync(string sessionId, string branchId, CancellationToken ct = default)
    {
        if (_branchFrames.TryGetValue(new BranchKey(sessionId, branchId), out var branch))
        {
            lock (branch.Gate)
            {
                var list = new List<ExecutionFrame>(branch.FramesByIndex.Count);
                foreach (var pair in branch.FramesByIndex.OrderBy(static k => k.Key))
                {
                    list.Add(pair.Value);
                }
                return ValueTask.FromResult<IReadOnlyList<ExecutionFrame>>(list);
            }
        }
        return ValueTask.FromResult<IReadOnlyList<ExecutionFrame>>(Array.Empty<ExecutionFrame>());
    }

    public ValueTask<ExecutionSession?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return ValueTask.FromResult(session);
    }

    public ValueTask<IReadOnlyList<ExecutionSession>> ListSessionsAsync(CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<ExecutionSession>>(_sessions.Values.ToList());
    }

    public ValueTask SaveSessionAsync(ExecutionSession session, CancellationToken ct = default)
    {
        _sessions[session.SessionId] = session;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<BranchDiff> CompareBranchesAsync(string sessionId, string baseBranchId, string targetBranchId, CancellationToken ct = default)
    {
        var baseFrames = await GetExecutionHistoryAsync(sessionId, baseBranchId, ct);
        var targetFrames = await GetExecutionHistoryAsync(sessionId, targetBranchId, ct);
        return BranchDiffer.Compute(sessionId, baseBranchId, targetBranchId, baseFrames, targetFrames);
    }

    public async ValueTask<bool> VerifyDagIntegrityAsync(string sessionId, string branchId, CancellationToken ct = default)
    {
        var frames = await GetExecutionHistoryAsync(sessionId, branchId, ct);
        return DagVerifier.Verify(frames);
    }

    private sealed class BranchState
    {
        public readonly object Gate = new();
        public readonly Dictionary<int, ExecutionFrame> FramesByIndex = new();
        public volatile string? LatestHash;
    }
}
