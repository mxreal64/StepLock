using System.Collections.Concurrent;
using DeterministicProxy.Core.Abstractions;
using DeterministicProxy.Core.Cryptography;
using DeterministicProxy.Core.Models;

namespace DeterministicProxy.Storage.Memory;

public sealed class MemoryExecutionStore : IExecutionStore
{
    private readonly ConcurrentDictionary<string, ExecutionFrame> _framesByHash = new();
    private readonly ConcurrentDictionary<string, (SortedList<int, ExecutionFrame> Frames, ReaderWriterLockSlim Lock)> _branchFrames = new();
    private readonly ConcurrentDictionary<string, ExecutionSession> _sessions = new();

    private static string GetBranchKey(string sessionId, string branchId) => $"{sessionId}:{branchId}";

    private (SortedList<int, ExecutionFrame> Frames, ReaderWriterLockSlim Lock) GetOrCreateBranch(string key)
    {
        return _branchFrames.GetOrAdd(key, _ => (new SortedList<int, ExecutionFrame>(), new ReaderWriterLockSlim()));
    }

    public ValueTask SaveFrameAsync(ExecutionFrame frame, CancellationToken ct = default)
    {
        _framesByHash[frame.FrameHash] = frame;

        var key = GetBranchKey(frame.SessionId, frame.BranchId);
        var branch = GetOrCreateBranch(key);

        branch.Lock.EnterWriteLock();
        try
        {
            branch.Frames[frame.StepIndex] = frame;
        }
        finally
        {
            branch.Lock.ExitWriteLock();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SaveFramesBatchAsync(IReadOnlyList<ExecutionFrame> frames, CancellationToken ct = default)
    {
        foreach (var frame in frames)
        {
            _framesByHash[frame.FrameHash] = frame;

            var key = GetBranchKey(frame.SessionId, frame.BranchId);
            var branch = GetOrCreateBranch(key);

            branch.Lock.EnterWriteLock();
            try
            {
                branch.Frames[frame.StepIndex] = frame;
            }
            finally
            {
                branch.Lock.ExitWriteLock();
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ExecutionFrame?> GetFrameByStepIndexAsync(string sessionId, string branchId, int stepIndex, CancellationToken ct = default)
    {
        var key = GetBranchKey(sessionId, branchId);
        if (_branchFrames.TryGetValue(key, out var branch))
        {
            branch.Lock.EnterReadLock();
            try
            {
                if (branch.Frames.TryGetValue(stepIndex, out var frame))
                {
                    return ValueTask.FromResult<ExecutionFrame?>(frame);
                }
            }
            finally
            {
                branch.Lock.ExitReadLock();
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
        var key = GetBranchKey(sessionId, branchId);
        if (_branchFrames.TryGetValue(key, out var branch))
        {
            branch.Lock.EnterReadLock();
            try
            {
                if (branch.Frames.Count > 0)
                {
                    var last = branch.Frames.Values[branch.Frames.Count - 1];
                    return ValueTask.FromResult<string?>(last.FrameHash);
                }
            }
            finally
            {
                branch.Lock.ExitReadLock();
            }
        }
        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask<IReadOnlyList<ExecutionFrame>> GetExecutionHistoryAsync(string sessionId, string branchId, CancellationToken ct = default)
    {
        var key = GetBranchKey(sessionId, branchId);
        if (_branchFrames.TryGetValue(key, out var branch))
        {
            branch.Lock.EnterReadLock();
            try
            {
                return ValueTask.FromResult<IReadOnlyList<ExecutionFrame>>(branch.Frames.Values.ToList());
            }
            finally
            {
                branch.Lock.ExitReadLock();
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

        int maxLen = Math.Max(baseFrames.Count, targetFrames.Count);
        int? divergenceStep = null;
        int identicalCount = 0;
        var stepDiffs = new List<StepDiff>();

        for (int i = 0; i < maxLen; i++)
        {
            var baseF = i < baseFrames.Count ? baseFrames[i] : null;
            var targetF = i < targetFrames.Count ? targetFrames[i] : null;

            bool isMatch = baseF != null && targetF != null && baseF.FrameHash == targetF.FrameHash;
            if (isMatch)
            {
                identicalCount++;
            }
            else if (divergenceStep == null)
            {
                divergenceStep = i;
            }

            stepDiffs.Add(new StepDiff(
                StepIndex: i,
                BaseFrameHash: baseF?.FrameHash,
                TargetFrameHash: targetF?.FrameHash,
                IsMatch: isMatch,
                BaseTargetUri: baseF?.TargetUri,
                TargetTargetUri: targetF?.TargetUri,
                StatusCodeChanged: baseF?.ResponseStatusCode != targetF?.ResponseStatusCode
            ));
        }

        return new BranchDiff(sessionId, baseBranchId, targetBranchId, divergenceStep, identicalCount, stepDiffs);
    }

    public async ValueTask<bool> VerifyDagIntegrityAsync(string sessionId, string branchId, CancellationToken ct = default)
    {
        var frames = await GetExecutionHistoryAsync(sessionId, branchId, ct);
        string? expectedParentHash = null;

        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.ParentStepHash != expectedParentHash)
                return false;

            var computedHash = FrameHasher.Instance.ComputeFrameHash(
                frame.ParentStepHash,
                frame.HttpMethod,
                frame.TargetUri,
                frame.RequestBodyHash,
                frame.StepIndex);

            if (computedHash != frame.FrameHash)
                return false;

            expectedParentHash = frame.FrameHash;
        }

        return true;
    }
}
