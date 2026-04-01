using FluentAssertions;
using MelonMQ.Client;
using System.Security.Authentication;
using System.Text;

namespace MelonMQ.Tests.Integration;

public class TcpTlsEndToEndTests
{
    [Fact]
    public async Task TlsConnection_ShouldPublishAndConsumeMessage()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-tests", Guid.NewGuid().ToString("N"));
        await using var certificate = await TestTlsCertificateProvider.CreateAsync();
        await using var host = new TestBrokerHost(dataDir, config =>
        {
            config.TcpTls.Enabled = true;
            config.TcpTls.CertificatePath = certificate.CertificatePath;
            config.TcpTls.CertificatePassword = certificate.CertificatePassword;
            config.TcpTls.CheckCertificateRevocation = false;
        });

        await host.StartAsync();

        await using var connection = await MelonConnection.ConnectAsync(
            $"melons://127.0.0.1:{host.TcpPort}",
            new MelonConnectionOptions
            {
                UseTls = true,
                AllowUntrustedServerCertificate = true,
                CheckCertificateRevocation = false,
                TlsTargetHost = "localhost",
                RetryPolicy = new ConnectionRetryPolicy { EnableRetry = false }
            });

        await using var channel = await connection.CreateChannelAsync();

        var queue = $"tls-roundtrip-{Guid.NewGuid():N}";
        await channel.DeclareQueueAsync(queue, durable: false);

        var expected = "hello-over-tls";
        await channel.PublishAsync(queue, Encoding.UTF8.GetBytes(expected));

        var delivered = await ConsumeSingleAsync(channel, queue, TimeSpan.FromSeconds(4));
        delivered.Should().NotBeNull();

        var body = Encoding.UTF8.GetString(delivered!.Body.Span);
        body.Should().Be(expected);

        await channel.AckAsync(delivered.DeliveryTag);
    }

    [Fact]
    public async Task TlsConnection_ShouldRejectUntrustedCertificate_WhenValidationEnabled()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-tests", Guid.NewGuid().ToString("N"));
        await using var certificate = await TestTlsCertificateProvider.CreateAsync();
        await using var host = new TestBrokerHost(dataDir, config =>
        {
            config.TcpTls.Enabled = true;
            config.TcpTls.CertificatePath = certificate.CertificatePath;
            config.TcpTls.CertificatePassword = certificate.CertificatePassword;
            config.TcpTls.CheckCertificateRevocation = false;
        });

        await host.StartAsync();

        var connect = async () => await MelonConnection.ConnectAsync(
            $"melons://127.0.0.1:{host.TcpPort}",
            new MelonConnectionOptions
            {
                UseTls = true,
                AllowUntrustedServerCertificate = false,
                CheckCertificateRevocation = false,
                TlsTargetHost = "localhost",
                RetryPolicy = new ConnectionRetryPolicy { EnableRetry = false }
            });

        await connect.Should().ThrowAsync<AuthenticationException>();
    }

    private static async Task<IncomingMessage?> ConsumeSingleAsync(MelonChannel channel, string queue, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await using var enumerator = channel.ConsumeAsync(queue, prefetch: 1, cts.Token).GetAsyncEnumerator();

        try
        {
            if (await enumerator.MoveNextAsync())
            {
                return enumerator.Current;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout with no messages available.
        }

        return null;
    }
}
