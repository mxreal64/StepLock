using DeterministicProxy.Core.Cryptography;
using DeterministicProxy.Core.Models;

namespace DeterministicProxy.Storage;

/// <summary>
/// Shared branch diffing and DAG integrity logic used by all IExecutionStore implementations.
/// </summary>
internal static class BranchDiffer
{
    public static BranchDiff Compute(
        string sessionId,
        string baseBranchId,
        string targetBranchId,
        IReadOnlyList<ExecutionFrame> baseFrames,
        IReadOnlyList<ExecutionFrame> targetFrames)
    {
        int maxLen = Math.Max(baseFrames.Count, targetFrames.Count);
        int? divergenceStep = null;
        int identicalCount = 0;
        var stepDiffs = new List<StepDiff>(maxLen);

        for (int i = 0; i < maxLen; i++)
        {
            var baseF = i < baseFrames.Count ? baseFrames[i] : null;
            var targetF = i < targetFrames.Count ? targetFrames[i] : null;

            bool isMatch = baseF != null && targetF != null && baseF.FrameHash == targetF.FrameHash;
            if (isMatch)
            {
                identicalCount++;
            }
            else
            {
                divergenceStep ??= i;
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
}

internal static class DagVerifier
{
    public static bool Verify(IReadOnlyList<ExecutionFrame> frames)
    {
        string? expectedParentHash = null;

        foreach (var frame in frames)
        {
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
