using System.Text;
using DeterministicProxy.Core.Abstractions;
using DeterministicProxy.Core.Models;
using DeterministicProxy.Engine.Safety;

namespace DeterministicProxy.Engine.Safety;

public sealed record VirtualizedMutationResult(
    bool WasShortCircuited,
    int StatusCode,
    Dictionary<string, string> Headers,
    byte[] SyntheticResponseBody
);

public interface ISideEffectBarrier : ISideEffectClassifier
{
    VirtualizedMutationResult GenerateVirtualResponse(
        string httpMethod,
        string targetUri,
        Dictionary<string, string> requestHeaders);
}

public sealed class SideEffectBarrier : ISideEffectBarrier
{
    private readonly ISideEffectClassifier _innerClassifier;

    public SideEffectBarrier(ISideEffectClassifier? innerClassifier = null)
    {
        _innerClassifier = innerClassifier ?? SideEffectSafetyBarrier.Default;
    }

    public SideEffectType Classify(string httpMethod, string targetUri, Dictionary<string, string> headers)
    {
        return _innerClassifier.Classify(httpMethod, targetUri, headers);
    }

    public VirtualizedMutationResult GenerateVirtualResponse(
        string httpMethod,
        string targetUri,
        Dictionary<string, string> requestHeaders)
    {
        var json = $$"""
        {
            "status": "virtualized_mock_success",
            "virtual_replay": true,
            "message": "Mutation was intercepted and safely virtualized without real-world side effects.",
            "target": "{{targetUri}}",
            "method": "{{httpMethod}}"
        }
        """;

        return new VirtualizedMutationResult(
            WasShortCircuited: true,
            StatusCode: 200,
            Headers: new() { ["Content-Type"] = "application/json", ["X-Deterministic-Virtualized"] = "true" },
            SyntheticResponseBody: Encoding.UTF8.GetBytes(json)
        );
    }
}
