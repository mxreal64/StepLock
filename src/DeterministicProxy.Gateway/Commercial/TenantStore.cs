using System.Security.Cryptography;
using System.Text;

namespace DeterministicProxy.Gateway.Commercial;

public sealed record TenantInfo(
    string TenantId,
    string OrganizationName,
    string Tier, // Free, Pro, Enterprise
    int MonthlyStepLimit,
    bool PiiRedactionEnabled
);

public interface ITenantStore
{
    ValueTask<TenantInfo?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default);
}

public sealed class InMemoryTenantStore : ITenantStore
{
    private readonly Dictionary<string, TenantInfo> _tenantsByHashedKey = new();

    public InMemoryTenantStore()
    {
        // Default demo tenant for local development / testing
        var defaultKeyHash = HashKey("dp_live_test_key_12345");
        _tenantsByHashedKey[defaultKeyHash] = new TenantInfo(
            TenantId: "tenant_enterprise_demo",
            OrganizationName: "Acme Autonomous AI",
            Tier: "Enterprise",
            MonthlyStepLimit: 1_000_000,
            PiiRedactionEnabled: true
        );
    }

    public ValueTask<TenantInfo?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var hash = HashKey(apiKey);
        _tenantsByHashedKey.TryGetValue(hash, out var tenant);
        return ValueTask.FromResult(tenant);
    }

    private static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
