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
public sealed class EnterprisePiiRedactor : IPiiRedactor
{
    public static readonly EnterprisePiiRedactor Instance = new();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly Regex[] RedactionRules = new[]
    {
        // API Keys (OpenAI, Anthropic, Stripe, AWS)
        new Regex(@"(sk-[a-zA-Z0-9_-]{20,})", RegexOptions.Compiled, RegexTimeout),
        new Regex(@"(sk-ant-[a-zA-Z0-9_-]{20,})", RegexOptions.Compiled, RegexTimeout),
        new Regex(@"(rk_live_[a-zA-Z0-9]{24,})", RegexOptions.Compiled, RegexTimeout),
        new Regex(@"(AKIA[0-9A-Z]{16})", RegexOptions.Compiled, RegexTimeout),
        // Bearer Tokens (replaces token after 'Bearer ' while preserving 'Bearer ')
        new Regex(@"(?<=Bearer\s)[a-zA-Z0-9_\-\.]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout),
        // Credit Card Numbers
        new Regex(@"\b(?:\d{4}[ -]?){3}\d{4}\b", RegexOptions.Compiled, RegexTimeout),
        // US Social Security Numbers
        new Regex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled, RegexTimeout),
        // Passwords & Secrets in JSON fields
        new Regex(@"""(password|secret|access_token|api_key)""\s*:\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout)
    };

    public string Redact(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        var result = input;
        foreach (var rule in RedactionRules)
        {
            try
            {
                result = rule.Replace(result, "[REDACTED_SECRET]");
            }
            catch (RegexMatchTimeoutException)
            {
                // Safety guard against pathological regex inputs
            }
        }
        return result;
    }

    public byte[] Redact(byte[] input)
    {
        if (input == null || input.Length == 0)
            return input ?? Array.Empty<byte>();

        var text = System.Text.Encoding.UTF8.GetString(input);
        var redacted = Redact(text);
        return System.Text.Encoding.UTF8.GetBytes(redacted);
    }
}
