using DeterministicProxy.Core.Abstractions;
using DeterministicProxy.Core.Models;

namespace DeterministicProxy.Engine.Safety;

public sealed class SideEffectSafetyBarrier : ISideEffectClassifier
{
    public static readonly SideEffectSafetyBarrier Default = new();

    private static readonly HashSet<string> SafeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.openai.com",
        "api.anthropic.com",
        "generativelanguage.googleapis.com",
        "api.groq.com",
        "api.mistral.ai",
        "api.together.xyz",
        "localhost",
        "127.0.0.1"
    };

    public SideEffectType Classify(string httpMethod, string targetUri, Dictionary<string, string> headers)
    {
        var method = httpMethod.ToUpperInvariant();

        if (method is "GET" or "HEAD" or "OPTIONS")
        {
            return SideEffectType.SafeReadOnly;
        }

        if (Uri.TryCreate(targetUri, UriKind.Absolute, out var uri))
        {
            // LLM completion/chat endpoints are safe to re-query or replay
            if (SafeHosts.Contains(uri.Host) || uri.AbsolutePath.Contains("/chat/completions") || uri.AbsolutePath.Contains("/messages") || uri.AbsolutePath.Contains("/generateContent"))
            {
                return SideEffectType.SafeReadOnly;
            }

            // Check for explicit idempotency headers (e.g., Stripe-Idempotency-Key)
            if (headers.Keys.Any(k => k.Contains("Idempotency", StringComparison.OrdinalIgnoreCase)))
            {
                return SideEffectType.IdempotentWrite;
            }
        }

        return SideEffectType.Mutating;
    }
}
