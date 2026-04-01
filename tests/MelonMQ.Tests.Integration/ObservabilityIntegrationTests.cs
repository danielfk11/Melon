using FluentAssertions;
using MelonMQ.Broker.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace MelonMQ.Tests.Integration;

public class ObservabilityIntegrationTests
{
    [Fact]
    public async Task MetricsEndpoint_ShouldExposePrometheusCounters()
    {
        await using var factory = CreateFactory(enableOtlp: false, otlpEndpoint: null);
        using var client = factory.CreateClient();

        var queueName = $"metrics-{Guid.NewGuid():N}";
        var declareResponse = await client.PostAsJsonAsync("/queues/declare", new
        {
            name = queueName,
            durable = false
        });

        declareResponse.EnsureSuccessStatusCode();

        var publishResponse = await client.PostAsJsonAsync($"/queues/{queueName}/publish", new
        {
            message = "hello-metrics",
            persistent = false
        });

        publishResponse.EnsureSuccessStatusCode();

        var metricsText = await PollMetricsAsync(client, TimeSpan.FromSeconds(8));
        metricsText.Should().Contain("melonmq_messages_published_total");
        metricsText.Should().Contain("melonmq_operation_duration_ms");
    }

    [Fact]
    public async Task OtlpExporter_ShouldSendMetricsAndTraces_ToCollector()
    {
        await using var collector = await TestOtlpCollector.StartAsync();
        using var meter = new Meter("MelonMQ.Tests.OTLP");
        using var activitySource = new ActivitySource("MelonMQ.Tests.OTLP");
        var counter = meter.CreateCounter<long>("melonmq_test_events_total");

        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("MelonMQ.Tests.OTLP")
            .AddOtlpExporter((options, metricReaderOptions) =>
            {
                options.Endpoint = new Uri($"{collector.Endpoint}/v1/metrics");
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.TimeoutMilliseconds = 2000;

                metricReaderOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 500;
                metricReaderOptions.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = 2000;
            })
            .Build();

        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("MelonMQ.Tests.OTLP")
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri($"{collector.Endpoint}/v1/traces");
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.TimeoutMilliseconds = 2000;
            })
            .Build();

        counter.Add(1, new KeyValuePair<string, object?>("operation", "otlp-test"));
        using (var activity = activitySource.StartActivity("melonmq.test.operation", ActivityKind.Internal))
        {
            activity?.SetTag("component", "observability-integration-test");
        }

        meterProvider.ForceFlush();
        tracerProvider.ForceFlush();

        var metricsReceived = await collector.WaitForPathAsync("/v1/metrics", TimeSpan.FromSeconds(15));
        var tracesReceived = await collector.WaitForPathAsync("/v1/traces", TimeSpan.FromSeconds(15));

        var observed = string.Join(", ", collector.GetObservedPaths());
        metricsReceived.Should().BeTrue($"OTLP metrics should be exported to collector. Observed paths: {observed}");
        tracesReceived.Should().BeTrue($"OTLP traces should be exported to collector. Observed paths: {observed}");
    }

    private static async Task<string> PollMetricsAsync(HttpClient client, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            var response = await client.GetAsync("/metrics");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var payload = await response.Content.ReadAsStringAsync();
                if (payload.Contains("melonmq_messages_published_total", StringComparison.Ordinal))
                {
                    return payload;
                }
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("Prometheus metrics were not exposed in time.");
    }

    private static WebApplicationFactory<Program> CreateFactory(bool enableOtlp, string? otlpEndpoint)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "melonmq-observability-tests", Guid.NewGuid().ToString("N"));

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
                        DataDirectory = dataDirectory,
                        MaxMessageSize = 1048576,
                        Security = new SecurityConfiguration
                        {
                            RequireAuth = false,
                            RequireHashedPasswords = false,
                            RequireAdminApiKey = false,
                            ProtectReadEndpoints = false,
                            AllowedOrigins = Array.Empty<string>(),
                            Users = new Dictionary<string, string>()
                        },
                        Observability = new ObservabilityConfiguration
                        {
                            ServiceName = "MelonMQ.Tests",
                            ServiceVersion = "test",
                            Prometheus = new PrometheusConfiguration
                            {
                                Enabled = true,
                                EndpointPath = "/metrics",
                                RequireAdminApiKey = false
                            },
                            Otlp = new OtlpConfiguration
                            {
                                Enabled = enableOtlp,
                                Endpoint = otlpEndpoint ?? string.Empty,
                                Protocol = "http/protobuf",
                                EnableMetrics = true,
                                EnableTraces = true,
                                MetricsExportIntervalMs = 500,
                                TimeoutMs = 2000
                            }
                        },
                        Cluster = new ClusterConfiguration
                        {
                            Enabled = false
                        }
                    });
                });
            });
    }

    private sealed class TestOtlpCollector : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<string> _paths = new();

        private TestOtlpCollector(string endpoint, WebApplication app, ConcurrentQueue<string> paths)
        {
            Endpoint = endpoint.TrimEnd('/');
            _app = app;
            _paths = paths;
        }

        public string Endpoint { get; }

        public static async Task<TestOtlpCollector> StartAsync()
        {
            var port = GetFreePort();
            var endpoint = $"http://127.0.0.1:{port}";
            var paths = new ConcurrentQueue<string>();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Testing"
            });
            builder.WebHost.UseUrls(endpoint);

            var app = builder.Build();
            app.MapMethods("/{**path}", new[] { "POST", "GET", "PUT", "DELETE", "PATCH" }, async context =>
            {
                paths.Enqueue(context.Request.Path.Value ?? "/");

                using var reader = new StreamReader(context.Request.Body);
                _ = await reader.ReadToEndAsync();

                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.CompleteAsync();
            });

            await app.StartAsync();
            return new TestOtlpCollector(endpoint, app, paths);
        }

        public async Task<bool> WaitForPathAsync(string path, TimeSpan timeout)
        {
            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < timeout)
            {
                if (_paths.Any(p => PathsEqual(p, path)))
                {
                    return true;
                }

                await Task.Delay(200);
            }

            return false;
        }

        public IReadOnlyCollection<string> GetObservedPaths()
        {
            return _paths.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _app.StopAsync(cts.Token);
            }
            catch
            {
                // Ignore shutdown errors for test-only listener.
            }

            await _app.DisposeAsync();
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static bool PathsEqual(string left, string right)
        {
            var normalizedLeft = NormalizePath(left);
            var normalizedRight = NormalizePath(right);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "/";
            }

            var trimmed = path.Trim();
            if (trimmed.Length > 1)
            {
                trimmed = trimmed.TrimEnd('/');
            }

            return trimmed;
        }
    }
}
