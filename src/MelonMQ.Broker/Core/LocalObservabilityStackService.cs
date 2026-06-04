using System.Diagnostics;

namespace MelonMQ.Broker.Core;

public sealed class LocalObservabilityStackService : IHostedService
{
    private readonly MelonMQConfiguration _config;
    private readonly ILogger<LocalObservabilityStackService> _logger;
    private readonly IWebHostEnvironment _environment;
    private Process? _process;

    public LocalObservabilityStackService(
        MelonMQConfiguration config,
        ILogger<LocalObservabilityStackService> logger,
        IWebHostEnvironment environment)
    {
        _config = config;
        _logger = logger;
        _environment = environment;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var localStack = _config.Observability.LocalStack;
        if (!localStack.Enabled)
        {
            return;
        }

        if (!_environment.IsDevelopment())
        {
            _logger.LogWarning(
                "Local observability stack auto-start is enabled, but the current environment is {Environment}. Skipping.",
                _environment.EnvironmentName);
            return;
        }

        var scriptPath = ResolvePath(localStack.StartScriptPath);
        if (scriptPath == null)
        {
            var message = $"Local observability start script not found: '{localStack.StartScriptPath}'.";
            await HandleStartError(message, null);
            return;
        }

        try
        {
            var startInfo = CreateStartInfo(scriptPath, localStack);
            _process = Process.Start(startInfo);
            if (_process == null)
            {
                await HandleStartError("Local observability stack process did not start.", null);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (_process.HasExited)
            {
                var exitCode = _process.ExitCode;
                _process.Dispose();
                _process = null;
                await HandleStartError($"Local observability stack exited during startup with code {exitCode}.", null);
                return;
            }

            _logger.LogInformation(
                "Started local observability stack with script {ScriptPath}. Prometheus: http://{PrometheusListenAddress}; Grafana: http://{GrafanaAddress}:{GrafanaPort}",
                scriptPath,
                localStack.PrometheusListenAddress,
                localStack.GrafanaAddress,
                localStack.GrafanaPort);
        }
        catch (Exception ex)
        {
            await HandleStartError("Failed to start local observability stack.", ex);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process == null || _process.HasExited)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Stopping local observability stack...");
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop local observability stack cleanly.");
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private Task HandleStartError(string message, Exception? exception)
    {
        if (_config.Observability.LocalStack.FailBrokerOnStartError)
        {
            throw new InvalidOperationException(message, exception);
        }

        if (exception == null)
        {
            _logger.LogWarning("{Message} Broker will continue without Grafana/Prometheus auto-start.", message);
        }
        else
        {
            _logger.LogWarning(exception, "{Message} Broker will continue without Grafana/Prometheus auto-start.", message);
        }

        return Task.CompletedTask;
    }

    private ProcessStartInfo CreateStartInfo(
        string scriptPath,
        LocalObservabilityStackConfiguration localStack)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/env",
            RedirectStandardError = false,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            WorkingDirectory = ResolveRepositoryRoot() ?? Directory.GetCurrentDirectory()
        };

        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment["MELONMQ_PROMETHEUS_LISTEN"] = localStack.PrometheusListenAddress;
        startInfo.Environment["MELONMQ_GRAFANA_ADDR"] = localStack.GrafanaAddress;
        startInfo.Environment["MELONMQ_GRAFANA_PORT"] = localStack.GrafanaPort.ToString();

        return startInfo;
    }

    private string? ResolvePath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var repositoryRoot = ResolveRepositoryRoot();
        if (repositoryRoot != null)
        {
            var fromRoot = Path.GetFullPath(Path.Combine(repositoryRoot, configuredPath));
            if (File.Exists(fromRoot))
            {
                return fromRoot;
            }
        }

        var fromCurrentDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuredPath));
        if (File.Exists(fromCurrentDirectory))
        {
            return fromCurrentDirectory;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var current = new DirectoryInfo(baseDirectory);
        while (current != null)
        {
            var candidate = Path.GetFullPath(Path.Combine(current.FullName, configuredPath));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MelonMQ.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
