using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DeterministicProxy.Engine.Tls;

/// <summary>
/// Manages dynamic root CA and on-the-fly SSL/TLS leaf certificate generation for MITM HTTP_PROXY
/// using high-performance ECDsa P-256 keys.
/// </summary>
public sealed class DynamicCertificateAuthority : IDisposable
{
    private readonly X509Certificate2 _rootCert;
    private readonly ECDsa _rootEcdsaKey;
    private readonly ECDsa _leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509SignatureGenerator _signatureGenerator;
    private readonly ConcurrentDictionary<string, X509Certificate2> _certCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly X509BasicConstraintsExtension LeafBasicConstraints = new(false, false, 0, false);
    private static readonly X509KeyUsageExtension LeafKeyUsage = new(
        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true);

    public X509Certificate2 RootCertificate => _rootCert;

    public DynamicCertificateAuthority(X509Certificate2? customRoot = null)
    {
        if (customRoot != null)
        {
            _rootCert = customRoot;
            _rootEcdsaKey = customRoot.GetECDsaPrivateKey() ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        }
        else
        {
            _rootEcdsaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _rootCert = CreateSelfSignedRootCertificate(_rootEcdsaKey);
        }
        _signatureGenerator = X509SignatureGenerator.CreateForECDsa(_rootEcdsaKey);
    }

    public X509Certificate2 GetOrCreateDomainCertificate(string domain)
    {
        return _certCache.GetOrAdd(domain, CreateDomainCertificate);
    }

    private static X509Certificate2 CreateSelfSignedRootCertificate(ECDsa ecdsa)
    {
        var req = new CertificateRequest(
            "CN=Deterministic Execution Proxy Root CA, O=DeterministicProxy, OU=AgentInfra",
            ecdsa,
            HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(5);

        return req.CreateSelfSigned(notBefore, notAfter);
    }

    private X509Certificate2 CreateDomainCertificate(string domain)
    {
        var req = new CertificateRequest(
            $"CN={domain}",
            _leafKey,
            HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(LeafBasicConstraints);
        req.CertificateExtensions.Add(LeafKeyUsage);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(domain);
        req.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(90);

        Span<byte> serialNumber = stackalloc byte[8];
        RandomNumberGenerator.Fill(serialNumber);

        using var leafCert = req.Create(_rootCert.SubjectName, _signatureGenerator, notBefore, notAfter, serialNumber);
        return leafCert.CopyWithPrivateKey(_leafKey);
    }

    public void Dispose()
    {
        _rootCert.Dispose();
        _rootEcdsaKey.Dispose();
        _leafKey.Dispose();
        foreach (var cert in _certCache.Values)
        {
            cert.Dispose();
        }
        _certCache.Clear();
    }
}
