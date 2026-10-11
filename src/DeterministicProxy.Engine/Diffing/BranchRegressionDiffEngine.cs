using System.Text.Json;
using DeterministicProxy.Core.Abstractions;
using DeterministicProxy.Core.Models;

namespace DeterministicProxy.Engine.Diffing;

public sealed record SemanticStepDiff(
    int StepIndex,
    bool IsMatch,
    string? BaseUri,
    string? TargetUri,
    int? BaseStatus,
    int? TargetStatus,
    string? BaseMethod,
    string? TargetMethod,
    string? BodyDiffSummary,
    string? DivergenceReason
);

public sealed record SemanticBranchReport(
    string SessionId,
    string BaseBranchId,
    string TargetBranchId,
    int? DivergenceStepIndex,
    int TotalStepsCompared,
    int IdenticalSteps,
    List<SemanticStepDiff> Details,
    DateTimeOffset AnalyzedAtUtc
);

public interface IBranchRegressionDiffEngine
{
    ValueTask<SemanticBranchReport> AnalyzeRegressionAsync(
        string sessionId,
        string baseBranchId,
        string targetBranchId,
        CancellationToken ct = default);
}

public sealed class BranchRegressionDiffEngine : IBranchRegressionDiffEngine
{
    private readonly IExecutionStore _store;

    public BranchRegressionDiffEngine(IExecutionStore store)
    {
        _store = store;
    }

    public async ValueTask<SemanticBranchReport> AnalyzeRegressionAsync(
        string sessionId,
        string baseBranchId,
        string targetBranchId,
        CancellationToken ct = default)
    {
        var baseFrames = await _store.GetExecutionHistoryAsync(sessionId, baseBranchId, ct);
        var targetFrames = await _store.GetExecutionHistoryAsync(sessionId, targetBranchId, ct);

        int maxLen = Math.Max(baseFrames.Count, targetFrames.Count);
        int? divergenceStep = null;
        int identicalCount = 0;
        var details = new List<SemanticStepDiff>();

        for (int i = 0; i < maxLen; i++)
        {
            var b = i < baseFrames.Count ? baseFrames[i] : null;
            var t = i < targetFrames.Count ? targetFrames[i] : null;

            bool isMatch = b != null && t != null && b.FrameHash == t.FrameHash;
            string? reason = null;
            string? diffSummary = null;

            if (isMatch)
            {
                identicalCount++;
            }
            else
            {
                divergenceStep ??= i;

                if (b == null)
                    reason = $"Step added in target branch '{targetBranchId}'";
                else if (t == null)
                    reason = $"Step omitted in target branch '{targetBranchId}'";
                else if (b.TargetUri != t.TargetUri)
                    reason = $"Target URI changed from '{b.TargetUri}' to '{t.TargetUri}'";
                else if (b.HttpMethod != t.HttpMethod)
                    reason = $"HTTP Method changed from '{b.HttpMethod}' to '{t.HttpMethod}'";
                else if (b.RequestBodyHash != t.RequestBodyHash)
                {
                    reason = "Prompt or Request payload content diverged";
                    diffSummary = SummarizeBodyDiff(b.RequestBodyCanonical, t.RequestBodyCanonical);
                }
                else if (b.ResponseStatusCode != t.ResponseStatusCode)
                    reason = $"Upstream Response status code changed from {b.ResponseStatusCode} to {t.ResponseStatusCode}";
                else
                    reason = "Parent lineage DAG hash divergence";
            }

            details.Add(new SemanticStepDiff(
                StepIndex: i,
                IsMatch: isMatch,
                BaseUri: b?.TargetUri,
                TargetUri: t?.TargetUri,
                BaseStatus: b?.ResponseStatusCode,
                TargetStatus: t?.ResponseStatusCode,
                BaseMethod: b?.HttpMethod,
                TargetMethod: t?.HttpMethod,
                BodyDiffSummary: diffSummary,
                DivergenceReason: reason
            ));
        }

        return new SemanticBranchReport(
            SessionId: sessionId,
            BaseBranchId: baseBranchId,
            TargetBranchId: targetBranchId,
            DivergenceStepIndex: divergenceStep,
            TotalStepsCompared: maxLen,
            IdenticalSteps: identicalCount,
            Details: details,
            AnalyzedAtUtc: DateTimeOffset.UtcNow
        );
    }

    private static string? SummarizeBodyDiff(byte[]? baseBody, byte[]? targetBody)
    {
        if (baseBody == null || targetBody == null) return null;
        try
        {
            return $"Base size: {baseBody.Length} bytes, Target size: {targetBody.Length} bytes";
        }
        catch
        {
            return "Binary payload divergence";
        }
    }
}
