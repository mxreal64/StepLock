using DeterministicProxy.Core.Models;

namespace DeterministicProxy.Core.Abstractions;

public interface IExecutionStore
{
    ValueTask SaveFrameAsync(ExecutionFrame frame, CancellationToken ct = default);
    ValueTask SaveFramesBatchAsync(IReadOnlyList<ExecutionFrame> frames, CancellationToken ct = default);
    ValueTask<ExecutionFrame?> GetFrameByStepIndexAsync(string sessionId, string branchId, int stepIndex, CancellationToken ct = default);
    ValueTask<ExecutionFrame?> GetFrameByHashAsync(string frameHash, CancellationToken ct = default);
    ValueTask<string?> GetLatestStepHashAsync(string sessionId, string branchId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ExecutionFrame>> GetExecutionHistoryAsync(string sessionId, string branchId, CancellationToken ct = default);
    ValueTask<ExecutionSession?> GetSessionAsync(string sessionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ExecutionSession>> ListSessionsAsync(CancellationToken ct = default);
    ValueTask SaveSessionAsync(ExecutionSession session, CancellationToken ct = default);
    ValueTask<BranchDiff> CompareBranchesAsync(string sessionId, string baseBranchId, string targetBranchId, CancellationToken ct = default);
    ValueTask<bool> VerifyDagIntegrityAsync(string sessionId, string branchId, CancellationToken ct = default);
}

public interface IRequestCanonicalizer
{
    (string NormalizedBodyHash, byte[] CanonicalBytes) CanonicalizeBody(string? contentType, ReadOnlyMemory<byte> rawBody);
    Dictionary<string, string> CanonicalizeHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers);
}

public interface IFrameHasher
{
    string ComputeFrameHash(
        string? parentStepHash,
        string httpMethod,
        string targetUri,
        string requestBodyHash,
        int stepIndex);
}

public interface ISideEffectClassifier
{
    SideEffectType Classify(string httpMethod, string targetUri, Dictionary<string, string> headers);
}
