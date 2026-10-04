using System.Security.Cryptography.X509Certificates;
using DeterministicProxy.Engine.Tls;
using FluentAssertions;
using Xunit;

namespace DeterministicProxy.Tests;

public class DynamicCertificateAuthorityTests
{
    [Fact]
    public void DynamicCA_ShouldGenerateValidRootAndDomainCertificates()
    {
        using var ca = new DynamicCertificateAuthority();

        ca.RootCertificate.Should().NotBeNull();
        ca.RootCertificate.Subject.Should().Contain("Deterministic Execution Proxy Root CA");

        using var domainCert = ca.GetOrCreateDomainCertificate("api.openai.com");
        domainCert.Should().NotBeNull();
        domainCert.Subject.Should().Contain("api.openai.com");
        domainCert.HasPrivateKey.Should().BeTrue();

        // Cached certificate should return same instance
        var cachedCert = ca.GetOrCreateDomainCertificate("api.openai.com");
        cachedCert.Thumbprint.Should().Be(domainCert.Thumbprint);
    }
}
