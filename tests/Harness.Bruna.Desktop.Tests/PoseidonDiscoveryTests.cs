using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Harness.Bruna.Desktop.Tests;

public sealed class PoseidonDiscoveryTests : IDisposable
{
    private readonly string _dataDirectory;

    public PoseidonDiscoveryTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"bruna-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "bruna"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void ProbeRetornaParadoQuandoArquivoRuntimeNaoExiste()
    {
        var config = BrunaConfiguration.Load(_dataDirectory);
        using var discovery = new PoseidonDiscovery(config);

        var instance = discovery.Probe();

        Assert.Equal(PoseidonStatus.Stopped, instance.Status);
    }

    [Fact]
    public void ProbeRetornaParadoQuandoArquivoRuntimeEhJsonInvalido()
    {
        WriteRuntimeFile("not-json");
        var config = BrunaConfiguration.Load(_dataDirectory);
        using var discovery = new PoseidonDiscovery(config);

        var instance = discovery.Probe();

        Assert.Equal(PoseidonStatus.Stopped, instance.Status);
    }

    [Fact]
    public void ProbeRetornaNaoSaudavelQuandoEnderecoEhInvalido()
    {
        WriteRuntimeFile(new
        {
            schemaVersion = 2,
            launcherPid = 1,
            runnerPid = 2,
            address = "not-a-uri",
            dataDirectory = _dataDirectory,
            logsDirectory = _dataDirectory,
            startedAt = DateTimeOffset.UtcNow,
            readinessMode = "personal-session",
            readyAt = DateTimeOffset.UtcNow,
            verifiedEndpoints = Array.Empty<string>(),
        });

        var config = BrunaConfiguration.Load(_dataDirectory);
        using var discovery = new PoseidonDiscovery(config);

        var instance = discovery.Probe();

        Assert.Equal(PoseidonStatus.Unhealthy, instance.Status);
    }

    [Fact]
    public async Task ProbeRetornaSaudavelQuandoPoseidonResponde()
    {
        var listener = new HttpListener();
        var port = GetFreePort();
        var address = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(address);
        listener.Start();

        WriteRuntimeFile(new
        {
            schemaVersion = 2,
            launcherPid = ProcessIdHelper.CurrentProcessId(),
            runnerPid = 2,
            address,
            dataDirectory = _dataDirectory,
            logsDirectory = _dataDirectory,
            startedAt = DateTimeOffset.UtcNow,
            readinessMode = "personal-session",
            readyAt = DateTimeOffset.UtcNow,
            verifiedEndpoints = Array.Empty<string>(),
        });

        var responseTask = Task.Run(() =>
        {
            var context = listener.GetContext();
            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        var config = BrunaConfiguration.Load(_dataDirectory);
        using var discovery = new PoseidonDiscovery(config);
        var instance = await discovery.ProbeAsync();

        await responseTask;
        listener.Stop();
        listener.Close();

        Assert.Equal(PoseidonStatus.Healthy, instance.Status);
        Assert.Equal(address, instance.Address?.ToString());
    }

    private void WriteRuntimeFile(object state)
    {
        var runtimeDirectory = Path.Combine(_dataDirectory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        var path = Path.Combine(runtimeDirectory, "poseidon.json");
        File.WriteAllText(path, JsonSerializer.Serialize(state));
    }

    private static int GetFreePort()
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener?.Stop();
        }
    }
}
