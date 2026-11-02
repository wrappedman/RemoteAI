using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;

namespace RemoteDesk.Security;

public sealed class DeviceIdentity(X509Certificate2 certificate, Action? cleanup = null) : IDisposable
{
    public X509Certificate2 Certificate { get; } = certificate;
    public string DeviceId => Fingerprint(Certificate);
    public static string Fingerprint(X509Certificate certificate)
    {
        using var cert = new X509Certificate2(certificate);
        using var rsa = cert.GetRSAPublicKey() ?? throw new CryptographicException("RSA identity required.");
        return Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
    }
    public static string NormalizeId(string value)
    {
        var id = value.Replace(" ", "").Replace("-", "").ToUpperInvariant();
        if (id.Length != 64 || !id.All(Uri.IsHexDigit))
            throw new ArgumentException("Enter the full 64-character device fingerprint.");
        return id;
    }
    public static bool ValidatePeer(X509Certificate? certificate, SslPolicyErrors errors, string? expectedId)
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
        try
        {
            using var cert = new X509Certificate2(certificate);
            var now = DateTime.UtcNow;
            if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime()) return false;
            using var rsa = cert.GetRSAPublicKey();
            if (rsa is null || rsa.KeySize < 3072) return false;
            // PKI name/chain validation is replaced by explicit SPKI pinning. On the Host,
            // TLS proves key possession; the still-untrusted peer MUST pass attended approval.
            return expectedId is null || CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(Fingerprint(cert)), Convert.FromHexString(NormalizeId(expectedId)));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { return false; }
    }

    [SupportedOSPlatform("windows")]
    public static DeviceIdentity LoadOrCreateWindows()
    {
        // User-scoped CNG key. No PFX or plaintext private key is written by this application.
        const string keyName = "RemoteDesk.DeviceIdentity.v1";
        using var key = CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)
            ? CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)
            : CngKey.Create(CngAlgorithm.Rsa, keyName, new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
                Parameters = { new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None) }
            });
        using var rsa = new RSACng(key);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var cert in store.Certificates)
        {
            using var publicRsa = cert.GetRSAPublicKey();
            if (cert.HasPrivateKey && publicRsa is not null &&
                publicRsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(rsa.ExportSubjectPublicKeyInfo()) &&
                cert.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(7)) return new(cert);
            cert.Dispose();
        }
        var created = CreateCertificate(rsa);
        store.Add(created);
        return new(created);
    }

    // Schannel requires a persisted provider key even for short-lived test certificates.
    // Test-only keys have unique names and are deleted on disposal; production uses LoadOrCreateWindows.
    public static DeviceIdentity CreateForTesting()
    {
        if (OperatingSystem.IsWindows()) return CreateWindowsTestIdentity();
        using var rsa = RSA.Create(3072);
        return new(CreateCertificate(rsa));
    }
    [SupportedOSPlatform("windows")]
    private static DeviceIdentity CreateWindowsTestIdentity()
    {
        string name = "RemoteDesk.Test." + Guid.NewGuid().ToString("N");
        using var key = CngKey.Create(CngAlgorithm.Rsa, name, new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
            Parameters = { new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None) }
        });
        using var rsa = new RSACng(key);
        return new(CreateCertificate(rsa), () =>
        {
            using var temporaryKey = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider);
            temporaryKey.Delete();
        });
    }
    private static X509Certificate2 CreateCertificate(RSA rsa)
    {
        var request = new CertificateRequest("CN=RemoteDesk Device", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var purposes = new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }
    private int _disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Certificate.Dispose(); cleanup?.Invoke();
    }
}
