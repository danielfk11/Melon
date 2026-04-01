using FluentAssertions;
using MelonMQ.Broker.Core;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Json;

namespace MelonMQ.Tests.Unit.Core;

public class ClusterCoordinatorTests
{
    [Fact]
    public void ElectionAndQuorum_ShouldFailoverAndRejectMinorityWrites()
    {
        var config = CreateClusterConfig();
        var coordinator = CreateCoordinator(config, _ => new HttpResponseMessage(HttpStatusCode.OK));

        coordinator.RegisterOrUpdateNode("node-a", "http://node-a:9090");
        coordinator.RegisterOrUpdateNode("node-c", "http://node-c:9090");

        coordinator.LeaderNodeId.Should().Be("node-a");
        coordinator.IsLeader.Should().BeFalse();
        coordinator.HasWriteQuorum.Should().BeTrue();

        coordinator.RemoveNode("node-a");
        coordinator.LeaderNodeId.Should().Be("node-b");
        coordinator.IsLeader.Should().BeTrue();
        coordinator.HasWriteQuorum.Should().BeTrue();

        coordinator.RemoveNode("node-c");
        coordinator.HasWriteQuorum.Should().BeFalse();
        coordinator.IsLeader.Should().BeFalse("node must not accept writes without quorum in split-brain scenario");

        var canWrite = coordinator.CanAcceptWrites(out var reason);
        canWrite.Should().BeFalse();
        reason.Should().Contain("Write quorum unavailable");
    }

    [Fact]
    public async Task QuorumConsistency_ShouldRequireMajorityReplication()
    {
        var failNodeA = false;
        var failNodeC = false;

        var config = CreateClusterConfig();
        var coordinator = CreateCoordinator(config, request =>
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            var shouldFail = (host.Contains("node-a") && failNodeA) || (host.Contains("node-c") && failNodeC);
            var status = shouldFail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            return new HttpResponseMessage(status)
            {
                Content = JsonContent.Create(new { success = !shouldFail })
            };
        });

        coordinator.RegisterOrUpdateNode("node-a", "http://node-a:9090");
        coordinator.RegisterOrUpdateNode("node-c", "http://node-c:9090");

        var queueMessage = new QueueMessage
        {
            MessageId = Guid.NewGuid(),
            Body = new byte[] { 1, 2, 3 },
            EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Persistent = true
        };

        failNodeA = false;
        failNodeC = true;
        var withOneReplica = await coordinator.ReplicatePublishAsync("orders", queueMessage);
        withOneReplica.Should().BeTrue("self + one successful follower should satisfy quorum of 3 nodes");

        failNodeA = true;
        failNodeC = true;
        var withNoReplica = await coordinator.ReplicatePublishAsync("orders", queueMessage);
        withNoReplica.Should().BeFalse("self alone should not satisfy quorum when both followers fail");
    }

    private static MelonMQConfiguration CreateClusterConfig()
    {
        return new MelonMQConfiguration
        {
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
                RequireQuorumForWrites = true,
                Consistency = "quorum"
            }
        };
    }

    private static ClusterCoordinator CreateCoordinator(
        MelonMQConfiguration config,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpClientFactory = new StubHttpClientFactory(responder);
        return new ClusterCoordinator(
            config,
            NullLogger<ClusterCoordinator>.Instance,
            httpClientFactory,
            new MelonMetrics());
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _client = new HttpClient(new StubHttpMessageHandler(responder))
            {
                Timeout = TimeSpan.FromSeconds(2)
            };
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}
