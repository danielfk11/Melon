using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MelonMQ.Broker.Core;
using System.Net;
using System.Net.Http.Json;

namespace MelonMQ.Tests.Integration;

public class ProgramSecurityTests
{
    [Fact]
    public async Task HealthEndpoint_ShouldReturnUnauthorized_WhenReadProtectionIsEnabled()
    {
        await using var factory = CreateFactory(requireApiKey: true, protectReadEndpoints: true, adminApiKey: "test-key");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task QueuesEndpoint_ShouldReturnUnauthorized_WhenReadProtectionIsEnabled()
    {
        await using var factory = CreateFactory(requireApiKey: true, protectReadEndpoints: true, adminApiKey: "test-key");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/queues");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task QueuesEndpoint_ShouldReturnOk_WhenValidApiKeyIsProvided()
    {
        await using var factory = CreateFactory(requireApiKey: true, protectReadEndpoints: true, adminApiKey: "test-key");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-key");

        var response = await client.GetAsync("/queues");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QueuesEndpoint_ShouldAllowAnonymousRead_WhenReadProtectionIsDisabled()
    {
        await using var factory = CreateFactory(requireApiKey: true, protectReadEndpoints: false, adminApiKey: "test-key");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/queues");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeclareQueueEndpoint_ShouldReturnUnauthorized_WhenApiKeyIsMissing()
    {
        await using var factory = CreateFactory(requireApiKey: true, protectReadEndpoints: true, adminApiKey: "test-key");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/queues/declare", new
        {
            name = "secured-queue",
            durable = false
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeclareQueueEndpoint_ShouldReturnOk_WhenValidApiKeyIsProvided()
    {
        await using var factory = CreateFactory(requireApiKey: true, protectReadEndpoints: true, adminApiKey: "test-key");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-key");

        var response = await client.PostAsJsonAsync("/queues/declare", new
        {
            name = "secured-queue",
            durable = false
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool requireApiKey, bool protectReadEndpoints, string adminApiKey)
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
                        DataDirectory = Path.Combine(Path.GetTempPath(), "melonmq-program-tests", Guid.NewGuid().ToString("N")),
                        Security = new SecurityConfiguration
                        {
                            RequireAuth = false,
                            RequireHashedPasswords = false,
                            RequireAdminApiKey = requireApiKey,
                            ProtectReadEndpoints = protectReadEndpoints,
                            AdminApiKey = adminApiKey,
                            AllowedOrigins = Array.Empty<string>(),
                            Users = new Dictionary<string, string>()
                        }
                    });
                });
            });
    }
}
