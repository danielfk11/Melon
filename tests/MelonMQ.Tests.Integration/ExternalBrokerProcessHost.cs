using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MelonMQ.Tests.Integration;

internal sealed class ExternalBrokerProcessHost : IAsyncDisposable
{
    private Process? _process;

    public string DataDirectory { get; }
    public int TcpPort { get; }
    public int HttpPort { get; }
    public int BatchFlushMs { get; }

    public ExternalBrokerProcessHost(string dataDirectory, int batchFlushMs = 10)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(DataDirectory);
        BatchFlushMs = batchFlushMs;
        TcpPort = GetFreePort();
        HttpPort = GetFreePort();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false })
        {
            throw new InvalidOperationException("Broker process is already running.");
        }

        var root = ResolveRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run --project src/MelonMQ.Broker --configuration Release --no-build --nologo",
            WorkingDirectory = root,
            UseShellExecute = false
        };

        startInfo.Environment["MelonMQ__TcpBindAddress"] = "127.0.0.1";
        startInfo.Environment["MelonMQ__TcpPort"] = TcpPort.ToString();
        startInfo.Environment["MelonMQ__HttpPort"] = HttpPort.ToString();
        startInfo.Environment["MelonMQ__DataDirectory"] = DataDirectory;
        startInfo.Environment["MelonMQ__BatchFlushMs"] = BatchFlushMs.ToString();
        startInfo.Environment["MelonMQ__Security__RequireAuth"] = "false";
        startInfo.Environment["MelonMQ__Security__RequireAdminApiKey"] = "false";
        startInfo.Environment["MelonMQ__Security__ProtectReadEndpoints"] = "false";
        startInfo.Environment["MelonMQ__Security__RequireHashedPasswords"] = "false";

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start external broker process.");

        await WaitForPortOpenAsync("127.0.0.1", TcpPort, TimeSpan.FromSeconds(20), cancellationToken);
    }

    public async Task StopAsync()
    {
        if (_process == null || _process.HasExited)
        {
            return;
        }

        _process.Kill(entireProcessTree: true);
        await WaitForExitAsync(_process, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Hard termination equivalent to kill -9 for chaos testing.
    /// </summary>
    public async Task KillHardAsync()
    {
        if (_process == null || _process.HasExited)
        {
            return;
        }

        _process.Kill(entireProcessTree: true);
        await WaitForExitAsync(_process, TimeSpan.FromSeconds(10));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _process?.Dispose();
    }

    private static string ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MelonMQ.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve repository root (MelonMQ.sln not found).");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(250));
                await client.ConnectAsync(host, port, cts.Token);
                return;
            }
            catch
            {
                await Task.Delay(80, cancellationToken);
            }
        }

        throw new TimeoutException($"Timed out waiting for external broker on {host}:{port}.");
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        var exitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(exitTask, Task.Delay(timeout));
        if (completed != exitTask)
        {
            throw new TimeoutException("Timed out waiting for broker process to exit.");
        }
    }
}
