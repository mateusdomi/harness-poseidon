using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Harness.Bruna.Desktop;

/// <summary>
/// Inicia o Poseidon via Harness.Launcher quando o companion detecta que ele está parado.
/// </summary>
public sealed class PoseidonLauncher(BrunaConfiguration configuration)
{
    public string? FindLauncherPath()
    {
        if (!string.IsNullOrWhiteSpace(configuration.InstallDirectory))
        {
            var fromConfig = Path.Combine(configuration.InstallDirectory, LauncherExecutableName());
            if (File.Exists(fromConfig))
            {
                return fromConfig;
            }
        }

        var executableDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(executableDirectory, LauncherExecutableName()),
            Path.Combine(Directory.GetParent(executableDirectory)?.FullName ?? string.Empty, LauncherExecutableName()),
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        for (var directory = new DirectoryInfo(executableDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, LauncherExecutableName());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<LaunchResult> StartAsync(CancellationToken cancellationToken = default)
    {
        var launcherPath = FindLauncherPath();
        if (launcherPath is null)
        {
            return new LaunchResult(false, null, "Harness.Launcher não encontrado.");
        }

        var dataDirectory = Path.GetDirectoryName(configuration.FilePath)
            ?? BrunaOptions.DefaultDataDirectory();
        dataDirectory = Path.GetFullPath(Path.Combine(dataDirectory, ".."));

        var startInfo = new ProcessStartInfo(launcherPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add("--no-browser");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            return new LaunchResult(false, null, $"Falha ao iniciar o Launcher: {exception.Message}");
        }

        if (process is null)
        {
            return new LaunchResult(false, null, "Processo do Launcher não foi criado.");
        }

        using var discovery = new PoseidonDiscovery(configuration);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (!timeout.IsCancellationRequested)
            {
                var instance = await discovery.ProbeAsync(timeout.Token).ConfigureAwait(false);
                if (instance.Status == PoseidonStatus.Healthy)
                {
                    return new LaunchResult(true, instance.Address, null);
                }

                if (process.HasExited)
                {
                    return new LaunchResult(false, null, $"Launcher encerrou inesperadamente (exit {process.ExitCode}).");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LaunchResult(false, null, "Timeout ao aguardar o Poseidon ficar disponível.");
        }

        return new LaunchResult(false, null, "Timeout ao aguardar o Poseidon ficar disponível.");
    }

    private static string LauncherExecutableName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Harness.Launcher.exe"
            : "Harness.Launcher";
}

public sealed record LaunchResult(bool Success, Uri? Address, string? Error);
