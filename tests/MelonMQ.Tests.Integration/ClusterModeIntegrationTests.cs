using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MelonMQ.Broker.Core;
using System.Net;
using System.Net.Http.Json;

namespace MelonMQ.Tests.Integration;

public class ClusterModeIntegrationTests
{
    [Fact]
    public async Task WriteEndpoints_ShouldReturnConflict_WhenLeaderHasNoQuorum()
    {
        await using var factory = CreateFactory(requireQuorumForWrites: true);
        using var client = factory.CreateClient();

        var declareResponse = await client.PostAsJsonAsync("/queues/declare", new
        {
            name = "cluster-no-quorum",
            durable = false
        });

        declareResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var statusResponse = await client.GetAsync("/cluster/status");
        statusResponse.EnsureSuccessStatusCode();

        var payload = await statusResponse.Content.ReadAsStringAsync();
        payload.Should().Contain("\"hasWriteQuorum\":false");
    }

    [Fact]
    public async Task WriteEndpoints_ShouldAllowWrites_WhenQuorumRequirementIsDisabled()
    {
        await using var factory = CreateFactory(requireQuorumForWrites: false);
        using var client = factory.CreateClient();

        var declareResponse = await client.PostAsJsonAsync("/queues/declare", new
        {
            name = "cluster-dev-mode",
            durable = false
        });

        declareResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool requireQuorumForWrites)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<MelonMQConfiguration>();

                    services.AddSingleton(new MelonMQConfiguration
                    {
                        TcpBindAddress = "127.0.0.1",
                        TcpPort = 5672,
                        HttpPort = 9090,
                        DataDirectory = Path.Combine(Path.GetTempPath(), "melonmq-cluster-tests", Guid.NewGuid().ToString("N")),
                        Security = new SecurityConfiguration
                        {
                            RequireAuth = false,
                            RequireHashedPasswords = false,
                            RequireAdminApiKey = false,
                            ProtectReadEndpoints = false,
                            AllowedOrigins = Array.Empty<string>(),
                            Users = new Dictionary<string, string>()
                        },
                        Cluster = new ClusterConfiguration
                        {
                            Enabled = true,
                            NodeId = "node-b",
                            NodeAddress = "http://node-b:9090",
                            SeedNodes =
                            [
                                "http://node-a:9090",
                                "http://node-b:9090",
                                "http://node-c:9090"
                            ],
                            SharedKey = "test-cluster-key",
                            DiscoveryIntervalSeconds = 2,
                            NodeTimeoutSeconds = 8,
                            EnableReplication = true,
                            RequireQuorumForWrites = requireQuorumForWrites,
                            Consistency = "quorum"
                        }
                    });
                });
            });
    }
}
