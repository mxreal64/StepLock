using System.Text;
using System.Text.RegularExpressions;

namespace DeterministicProxy.Engine.Safety;

public interface IPiiRedactor
{
    string Redact(string input);
    byte[] Redact(byte[] input);
}

/// <summary>
/// High-performance Regex-based PII & Secret Redaction Engine
/// Masks OpenAI keys, Anthropic keys, AWS credentials, Credit Cards, SSNs, and Bearer tokens.
/// </summary>
public sealed partial class EnterprisePiiRedactor : IPiiRedactor
{
    public static readonly EnterprisePiiRedactor Instance = new();

    private static readonly MatchEvaluator Evaluator = static match =>
    {
        if (match.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return "Bearer [REDACTED_SECRET]";
        return "[REDACTED_SECRET]";
    };

    [GeneratedRegex(@"""(password|secret|access_token|api_key)""\s*:\s*""([^""]+)""|sk-[a-zA-Z0-9_-]{20,}|rk_live_[a-zA-Z0-9]{24,}|AKIA[0-9A-Z]{16}|\b(?:\d{4}[ -]?){3}\d{4}\b|\b\d{3}-\d{2}-\d{4}\b|Bearer\s+[a-zA-Z0-9_\-\.]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex UnifiedSecretsRegex();

    public string Redact(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        return UnifiedSecretsRegex().Replace(input, Evaluator);
    }

    public byte[] Redact(byte[] input)
    {
        if (input == null || input.Length == 0)
            return input ?? Array.Empty<byte>();

        var text = Encoding.UTF8.GetString(input);
        var redacted = Redact(text);
        if (ReferenceEquals(text, redacted) || text == redacted)
            return input;

        return Encoding.UTF8.GetBytes(redacted);
    }
}
