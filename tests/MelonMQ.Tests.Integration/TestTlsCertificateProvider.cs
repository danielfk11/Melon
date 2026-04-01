using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MelonMQ.Tests.Integration;

internal sealed class TestTlsCertificateProvider : IAsyncDisposable
{
    private const string TempCertificatePassword = "melonmq-test-password";
    private readonly bool _deleteOnDispose;

    public string CertificatePath { get; }
    public string CertificatePassword { get; }

    private TestTlsCertificateProvider(string certificatePath, string certificatePassword, bool deleteOnDispose)
    {
        CertificatePath = certificatePath;
        CertificatePassword = certificatePassword;
        _deleteOnDispose = deleteOnDispose;
    }

    public static Task<TestTlsCertificateProvider> CreateAsync()
    {
        var configuredPath = Environment.GetEnvironmentVariable("MELONMQ_TLS_CERT_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!File.Exists(configuredPath))
            {
                throw new FileNotFoundException(
                    $"Test TLS certificate not found at '{configuredPath}'.",
                    configuredPath);
            }

            var configuredPassword = Environment.GetEnvironmentVariable("MELONMQ_TLS_CERT_PASSWORD") ?? string.Empty;
            return Task.FromResult(new TestTlsCertificateProvider(configuredPath, configuredPassword, deleteOnDispose: false));
        }

        var directory = Path.Combine(Path.GetTempPath(), "melonmq-tests", "tls-certs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var certificatePath = Path.Combine(directory, "melonmq-test-server.pfx");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(7));

        var bytes = certificate.Export(X509ContentType.Pfx, TempCertificatePassword);
        File.WriteAllBytes(certificatePath, bytes);

        return Task.FromResult(
            new TestTlsCertificateProvider(certificatePath, TempCertificatePassword, deleteOnDispose: true));
    }

    public ValueTask DisposeAsync()
    {
        if (_deleteOnDispose)
        {
            try
            {
                if (File.Exists(CertificatePath))
                {
                    File.Delete(CertificatePath);
                }

                var directory = Path.GetDirectoryName(CertificatePath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for temp test assets.
            }
        }

        return ValueTask.CompletedTask;
    }
}
