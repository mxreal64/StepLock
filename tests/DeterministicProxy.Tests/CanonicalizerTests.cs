using System.Text;
using DeterministicProxy.Core.Canonicalization;
using FluentAssertions;
using Xunit;

namespace DeterministicProxy.Tests;

public class CanonicalizerTests
{
    private readonly SemanticRequestCanonicalizer _canonicalizer = SemanticRequestCanonicalizer.Default;

    [Fact]
    public void CanonicalizeBody_ShouldSortKeysAndMaskDynamicNonces()
    {
        var json1 = """{"model":"gpt-4o","nonce":"12345-abc","messages":[{"role":"user","content":"hello"}]}""";
        var json2 = """{"messages":[{"role":"user","content":"hello"}],"nonce":"67890-xyz","model":"gpt-4o"}""";

        var (hash1, _) = _canonicalizer.CanonicalizeBody("application/json", Encoding.UTF8.GetBytes(json1));
        var (hash2, _) = _canonicalizer.CanonicalizeBody("application/json", Encoding.UTF8.GetBytes(json2));

        // Even though property order was reversed and nonce values differed, canonical hash must be identical!
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void CanonicalizeHeaders_ShouldIgnoreVolatileHeaders()
    {
        var headers1 = new Dictionary<string, IEnumerable<string>>
        {
            ["Authorization"] = new[] { "Bearer sk-123" },
            ["Date"] = new[] { "Fri, 02 Oct 2026 10:00:00 GMT" },
            ["User-Agent"] = new[] { "Python/3.11 LangGraph/0.2" }
        };

        var headers2 = new Dictionary<string, IEnumerable<string>>
        {
            ["Authorization"] = new[] { "Bearer sk-123" },
            ["Date"] = new[] { "Sat, 03 Oct 2026 15:30:00 GMT" },
            ["User-Agent"] = new[] { "Python/3.12 AutoGen/0.4" }
        };

        var canonical1 = _canonicalizer.CanonicalizeHeaders(headers1);
        var canonical2 = _canonicalizer.CanonicalizeHeaders(headers2);

        canonical1.Should().ContainKey("Authorization");
        canonical1.Should().NotContainKey("Date");
        canonical1.Should().NotContainKey("User-Agent");

        canonical1["Authorization"].Should().Be(canonical2["Authorization"]);
    }

    [Fact]
    public void CanonicalizeBody_EmptyBody_ShouldReturnEmptyHashAndBytes()
    {
        var (hash, bytes) = _canonicalizer.CanonicalizeBody("application/json", ReadOnlyMemory<byte>.Empty);
        hash.Should().NotBeNullOrWhiteSpace();
        bytes.Should().BeEmpty();
    }

    [Fact]
    public void CanonicalizeBody_NonJsonContent_ShouldHashRaw()
    {
        var body = Encoding.UTF8.GetBytes("raw text body");
        var (hash1, _) = _canonicalizer.CanonicalizeBody("text/plain", body);
        var (hash2, _) = _canonicalizer.CanonicalizeBody("text/plain", body);
        hash1.Should().Be(hash2);
        hash1.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void CanonicalizeBody_ShouldMaskNestedDynamicFields()
    {
        var json1 = """{"data":{"nonce":"abc","value":42}}"""; 
        var json2 = """{"data":{"nonce":"xyz","value":42}}"""; 
        var (hash1, _) = _canonicalizer.CanonicalizeBody("application/json", Encoding.UTF8.GetBytes(json1));
        var (hash2, _) = _canonicalizer.CanonicalizeBody("application/json", Encoding.UTF8.GetBytes(json2));
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void CanonicalizeHeaders_ShouldIgnoreProxyControlHeaders()
    {
        var headers = new Dictionary<string, IEnumerable<string>>
        {
            ["X-Agent-Session-ID"] = new[] { "session-123" },
            ["X-Step-Index"] = new[] { "5" },
            ["Authorization"] = new[] { "Bearer sk-abc" },
        };
        var canonical = _canonicalizer.CanonicalizeHeaders(headers);
        canonical.Should().NotContainKey("X-Agent-Session-ID");
        canonical.Should().NotContainKey("X-Step-Index");
        canonical.Should().ContainKey("Authorization");
    }
}
