using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Ipc;
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
    LauncherReadiness readiness,
    LauncherProcessLease processLease,
    Process runner,
    CancellationTokenSource runnerOutputCancellation,
    IReadOnlyList<Task> runnerOutputTasks,
    string tokenFile,
    string runtimeFile)
    : IAsyncDisposable
{
    public Uri Address { get; } = address;

    public string DataDirectory { get; } = dataDirectory;

    public LauncherReadiness Readiness { get; } = readiness;

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) =>
        host.WaitForShutdownAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            runnerOutputCancellation.Cancel();
            if (!runner.HasExited)
            {
                runner.Kill(entireProcessTree: true);
                await runner.WaitForExitAsync();
            }
            await Task.WhenAll(runnerOutputTasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            runner.Dispose();
            runnerOutputCancellation.Dispose();
            await host.StopAsync();
            await host.DisposeAsync();
        }
        finally
        {
            if (File.Exists(tokenFile)) File.Delete(tokenFile);
            if (File.Exists(runtimeFile)) File.Delete(runtimeFile);
            processLease.Dispose();
        }
    }
}

public static class LauncherApplication
{
    private static readonly JsonSerializerOptions RuntimeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<LauncherHandle> StartAsync(
        LauncherOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var dataDirectory = options.ResolveDataDirectory();
        Directory.CreateDirectory(dataDirectory);
        var runtimeDirectory = Path.Combine(dataDirectory, "runtime");
        var logsDirectory = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(runtimeDirectory);
        Directory.CreateDirectory(logsDirectory);
        var processLease = LauncherProcessLease.Acquire(dataDirectory);
        WebApplication? host = null;
        Process? runner = null;
        CancellationTokenSource? runnerOutputCancellation = null;
        IReadOnlyList<Task> runnerOutputTasks = [];
        var urls = $"http://127.0.0.1:{options.Port ?? 0}";
        var tokenValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var tokenFile = Path.Combine(runtimeDirectory, "runner.token");
        var runtimeFile = Path.Combine(runtimeDirectory, "poseidon.json");
        try
        {
            await File.WriteAllTextAsync(tokenFile, tokenValue, cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            host = HostApplication.Build(
            [
                "--urls",
                urls,
                    "--Harness:DatabasePath",
                    Path.Combine(dataDirectory, "harness.db"),
            ], new RunnerIpcToken(tokenValue));
            await host.StartAsync(cancellationToken);
            var addresses = host.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses
                ?? throw new InvalidOperationException("O Host não expôs endereço de escuta.");
            var address = new Uri(addresses.Single(value =>
                value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
            var runnerPath = ResolveRunnerPath();
            var runnerLog = Path.Combine(logsDirectory, "runner.log");
            var startInfo = new ProcessStartInfo(runnerPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("service"); startInfo.ArgumentList.Add("--host-url");
            startInfo.ArgumentList.Add(address.ToString()); startInfo.ArgumentList.Add("--token-file");
            startInfo.ArgumentList.Add(tokenFile);
            runner = Process.Start(startInfo) ?? throw new InvalidOperationException("Não foi possível iniciar o Runner.");
            runnerOutputCancellation = new CancellationTokenSource();
            runnerOutputTasks = new[]
            {
                PumpAsync(runner.StandardOutput, runnerLog, runnerOutputCancellation.Token),
                PumpAsync(runner.StandardError, runnerLog, runnerOutputCancellation.Token),
            };
            var readiness = await LauncherReadinessProbe.WaitAsync(
                address, tokenValue, runner, cancellationToken);
            await WriteRuntimeStateAsync(runtimeFile, new
            {
                schemaVersion = 2,
                launcherPid = Environment.ProcessId,
                runnerPid = runner.Id,
                address = address.ToString(),
                dataDirectory,
                logsDirectory,
                startedAt = DateTimeOffset.UtcNow,
                readinessMode = readiness.Mode,
                readyAt = readiness.ReadyAt,
                verifiedEndpoints = readiness.VerifiedEndpoints,
            }, cancellationToken);
            return new LauncherHandle(host, address, dataDirectory, readiness, processLease, runner,
                runnerOutputCancellation, runnerOutputTasks, tokenFile, runtimeFile);
        }
        catch
        {
            runnerOutputCancellation?.Cancel();
            if (runner is not null)
            {
                if (!runner.HasExited)
                {
                    runner.Kill(entireProcessTree: true);
                    await runner.WaitForExitAsync(CancellationToken.None);
                }
                await Task.WhenAll(runnerOutputTasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                runner.Dispose();
            }
            runnerOutputCancellation?.Dispose();
            if (host is not null)
            {
                await host.StopAsync(CancellationToken.None);
                await host.DisposeAsync();
            }
            if (File.Exists(tokenFile)) File.Delete(tokenFile);
            if (File.Exists(runtimeFile)) File.Delete(runtimeFile);
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

    private static string ResolveRunnerPath()
    {
        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "runner", $"Harness.Runner{suffix}"),
        };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "src", "Harness.Runner", "bin", "Release", "net10.0", $"Harness.Runner{suffix}"));
        }
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("O executável self-contained do Runner não foi encontrado.");
    }

    private static async Task PumpAsync(StreamReader reader, string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };
        while (!token.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            if (line is null) break;
            await writer.WriteLineAsync($"{DateTimeOffset.UtcNow:O} {line}");
        }
    }

    private static async Task WriteRuntimeStateAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, RuntimeJsonOptions), token);
        File.Move(temporary, path, overwrite: true);
    }
}
