using DeterministicProxy.Core.Cryptography;
using FluentAssertions;
using Xunit;

namespace DeterministicProxy.Tests;

public class MerkleDagTests
{
    private readonly FrameHasher _hasher = FrameHasher.Instance;

    [Fact]
    public void ComputeFrameHash_ShouldBeDeterministic()
    {
        var hash1 = _hasher.ComputeFrameHash(null, "POST", "https://api.openai.com/v1/chat/completions", "abc123hash", 0);
        var hash2 = _hasher.ComputeFrameHash(null, "POST", "https://api.openai.com/v1/chat/completions", "abc123hash", 0);

        hash1.Should().Be(hash2);
        hash1.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ComputeFrameHash_ShouldChangeWhenParentChanges()
    {
        var hashStep0 = _hasher.ComputeFrameHash(null, "POST", "https://api.openai.com/v1/chat/completions", "abc123hash", 0);
        var hashStep1_A = _hasher.ComputeFrameHash(hashStep0, "POST", "https://api.stripe.com/v1/charges", "chargeHash", 1);
        var hashStep1_B = _hasher.ComputeFrameHash("different_parent_hash", "POST", "https://api.stripe.com/v1/charges", "chargeHash", 1);

        hashStep1_A.Should().NotBe(hashStep1_B);
    }

    [Fact]
    public void ComputeFrameHash_ShouldNormalizeMethodAndUri()
    {
        var hash1 = _hasher.ComputeFrameHash(null, "post", "HTTPS://API.OPENAI.COM/v1/chat/completions", "body", 0);
        var hash2 = _hasher.ComputeFrameHash(null, "POST", "https://api.openai.com/v1/chat/completions", "body", 0);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeFrameHash_ShouldProduceLowercaseHexString()
    {
        var hash = _hasher.ComputeFrameHash(null, "GET", "https://api.openai.com/", "empty", 0);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeFrameHash_ShouldChangeBetweenSteps()
    {
        var hash0 = _hasher.ComputeFrameHash(null, "GET", "https://api.openai.com/", "empty", 0);
        var hash1 = _hasher.ComputeFrameHash(hash0, "GET", "https://api.openai.com/", "empty", 1);
        hash0.Should().NotBe(hash1);
    }

    [Fact]
    public void ComputeFrameHash_ShouldStripQueryString()
    {
        var hash1 = _hasher.ComputeFrameHash(null, "GET", "https://api.openai.com/v1/models?version=1", "empty", 0);
        var hash2 = _hasher.ComputeFrameHash(null, "GET", "https://api.openai.com/v1/models?version=2", "empty", 0);
        hash1.Should().Be(hash2);
    }
}
