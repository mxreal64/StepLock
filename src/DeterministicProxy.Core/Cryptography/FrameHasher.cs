using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeterministicProxy.Core.Abstractions;

namespace DeterministicProxy.Core.Cryptography;

public sealed class FrameHasher : IFrameHasher
{
    public static readonly FrameHasher Instance = new();

    public string ComputeFrameHash(
        string? parentStepHash,
        string httpMethod,
        string targetUri,
        string requestBodyHash,
        int stepIndex)
    {
        // Lineage cryptographic chaining (Merkle DAG):
        // Hash(ParentHash + StepIndex + Method + NormalizedTargetUri + NormalizedBodyHash)
        var input = $"{parentStepHash ?? "ROOT"}:{stepIndex}:{httpMethod.ToUpperInvariant()}:{NormalizeUri(targetUri)}:{requestBodyHash}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string NormalizeUri(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return $"{parsed.Scheme}://{parsed.Authority}{parsed.AbsolutePath}".ToLowerInvariant();
        }
        return uri.ToLowerInvariant();
    }
}
