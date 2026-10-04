using DeterministicProxy.Core.Models;
using DeterministicProxy.Engine.Safety;
using DeterministicProxy.Gateway.Commercial;
using FluentAssertions;
using Xunit;

namespace DeterministicProxy.Tests;

public class CommercialFeatureTests
{
    [Fact]
    public void EnterprisePiiRedactor_ShouldMaskSecretsAndCreditCards()
    {
        var redactor = EnterprisePiiRedactor.Instance;
        var input = """
            {
                "openai_key": "sk-proj-abc1234567890abcdef12345",
                "stripe_key": "rk_live_123456789012345678901234",
                "card": "4532-1234-5678-9012",
                "message": "User query completed"
            }
            """;

        var redacted = redactor.Redact(input);

        redacted.Should().NotContain("sk-proj-abc1234567890abcdef12345");
        redacted.Should().NotContain("4532-1234-5678-9012");
        redacted.Should().Contain("[REDACTED_SECRET]");
        redacted.Should().Contain("User query completed");
    }

    [Fact]
    public void EnterprisePiiRedactor_ShouldPreserveBearerKeywordWhenRedactingToken()
    {
        var redactor = EnterprisePiiRedactor.Instance;
        var input = "Authorization: Bearer sk-proj-abc1234567890abcdef12345678901234";
        var result = redactor.Redact(input);
        result.Should().Contain("Bearer");
        result.Should().NotContain("sk-proj-abc1234567890abcdef12345678901234");
    }

    [Fact]
    public void EnterprisePiiRedactor_ShouldMaskAwsAccessKey()
    {
        var redactor = EnterprisePiiRedactor.Instance;
        var input = "AWS Access Key: AKIAIOSFODNN7EXAMPLE";
        var result = redactor.Redact(input);
        result.Should().NotContain("AKIAIOSFODNN7EXAMPLE");
        result.Should().Contain("[REDACTED_SECRET]");
    }

    [Fact]
    public async Task TenantStore_ShouldValidateHashedApiKey()
    {
        var store = new InMemoryTenantStore();
        var tenant = await store.ValidateApiKeyAsync("dp_live_test_key_12345");

        tenant.Should().NotBeNull();
        tenant!.TenantId.Should().Be("tenant_enterprise_demo");
        tenant.Tier.Should().Be("Enterprise");

        var invalid = await store.ValidateApiKeyAsync("invalid_key_xyz");
        invalid.Should().BeNull();
    }

    [Fact]
    public void UsageMeteringService_ShouldTrackStepAndByteUsage()
    {
        var metering = new UsageMeteringService();
        var tenantId = "tenant_test_1";

        metering.RecordStep(tenantId, isReplay: false, bytesTransferred: 4096, durationMs: 150);
        metering.RecordStep(tenantId, isReplay: true, bytesTransferred: 2048, durationMs: 5);

        var metrics = metering.GetUsage(tenantId);

        metrics.TotalRecordedSteps.Should().Be(1);
        metrics.TotalReplayedSteps.Should().Be(1);
        metrics.TotalProxiedBytes.Should().Be(6144);
        metrics.EstimatedLlmCostSavedCents.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SideEffectClassifier_ShouldClassifyGetAsReadOnly()
    {
        var classifier = new SideEffectSafetyBarrier();
        var result = classifier.Classify("GET", "https://api.stripe.com/v1/customers/cus_123", new());
        result.Should().Be(SideEffectType.SafeReadOnly);
    }

    [Fact]
    public void SideEffectClassifier_ShouldClassifyStripeChargeAsMutating()
    {
        var classifier = new SideEffectSafetyBarrier();
        var result = classifier.Classify("POST", "https://api.stripe.com/v1/charges", new());
        result.Should().Be(SideEffectType.Mutating);
    }

    [Fact]
    public void SideEffectClassifier_ShouldClassifyIdempotentWriteWithIdempotencyKey()
    {
        var classifier = new SideEffectSafetyBarrier();
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = "key-abc" };
        var result = classifier.Classify("POST", "https://api.stripe.com/v1/charges", headers);
        result.Should().Be(SideEffectType.IdempotentWrite);
    }
}
