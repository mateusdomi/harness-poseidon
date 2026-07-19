using System.Diagnostics;
using Harness.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harness.Launcher;

public sealed record LauncherOptions
{
    public string? DataDirectory { get; init; }

    public int? Port { get; init; }

    public bool OpenBrowser { get; init; } = true;

    public static LauncherOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? dataDirectory = null;
        int? port = null;
        var openBrowser = true;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--data-dir" when index + 1 < args.Count:
                    dataDirectory = args[++index];
                    break;
                case "--port" when index + 1 < args.Count:
                    port = int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--no-browser":
                    openBrowser = false;
                    break;
                default:
                    throw new ArgumentException(
                        $"Argumento desconhecido do launcher: {args[index]}. " +
                        "Use --data-dir <caminho>, --port <porta> e --no-browser.");
            }
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentException("A porta do launcher deve estar entre 1 e 65535.");
        }

        return new LauncherOptions
        {
            DataDirectory = dataDirectory,
            Port = port,
            OpenBrowser = openBrowser,
        };
    }

    public string ResolveDataDirectory() =>
        Path.GetFullPath(DataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harness-poseidon"));
}

public sealed class LauncherHandle(
    WebApplication host,
    Uri address,
    string dataDirectory,
    LauncherProcessLease processLease)
    : IAsyncDisposable
{
    public Uri Address { get; } = address;

    public string DataDirectory { get; } = dataDirectory;

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) =>
        host.WaitForShutdownAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await host.StopAsync();
            await host.DisposeAsync();
        }
        finally
        {
            processLease.Dispose();
        }
    }
}

public static class LauncherApplication
{
    public static async Task<LauncherHandle> StartAsync(
        LauncherOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var dataDirectory = options.ResolveDataDirectory();
        Directory.CreateDirectory(dataDirectory);
        var processLease = LauncherProcessLease.Acquire(dataDirectory);
        var urls = $"http://127.0.0.1:{options.Port ?? 0}";
        try
        {
            var host = HostApplication.Build(
            [
                "--urls",
                urls,
                "--Harness:DatabasePath",
                Path.Combine(dataDirectory, "harness.db"),
            ]);
            await host.StartAsync(cancellationToken);
            var addresses = host.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses
                ?? throw new InvalidOperationException("O Host não expôs endereço de escuta.");
            var address = new Uri(addresses.Single(value =>
                value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
            return new LauncherHandle(host, address, dataDirectory, processLease);
        }
        catch
        {
            processLease.Dispose();
            throw;
        }
    }

    public static bool TryOpenBrowser(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        try
        {
            var startInfo = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("/usr/bin/open", address.ToString())
                : OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("cmd", $"/c start {address}")
                    : new ProcessStartInfo("xdg-open", address.ToString());
            startInfo.UseShellExecute = false;
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
