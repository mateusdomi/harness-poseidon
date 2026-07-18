using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Harness.Modules.Execution.Infrastructure.Sandbox;
using Npgsql;

namespace Harness.RecoveryTests.Fixtures;

internal sealed class ManagedRecoveryPostgresFixture : IAsyncDisposable
{
    private readonly string _artifactRoot;
    private readonly DockerSandboxProvider _provider;
    private int _disposed;

    private ManagedRecoveryPostgresFixture(
        string attemptId,
        string artifactRoot,
        string connectionString,
        string connectionReferencePath,
        DockerSandboxProvider provider)
    {
        AttemptId = attemptId;
        _artifactRoot = artifactRoot;
        ConnectionString = connectionString;
        ConnectionReferencePath = connectionReferencePath;
        _provider = provider;
    }

    public string AttemptId { get; }

    public string ConnectionString { get; }

    public string ConnectionReferencePath { get; }

    public static async Task<ManagedRecoveryPostgresFixture> StartAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var attemptId = $"gng2-{Guid.NewGuid():N}"[..17];
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "gng-2-postgres",
            attemptId);
        var passwordPath = Path.Combine(artifactRoot, "postgres-password");
        var connectionReferencePath = Path.Combine(artifactRoot, "connection-string");
        var imageName = $"harness-postgres-gng2:{attemptId}";
        var networkName = $"harness-postgres-gng2-network-{attemptId}";
        var volumeName = $"harness-postgres-gng2-data-{attemptId}";
        var containerName = $"harness-postgres-gng2-{attemptId}";
        var provider = new DockerSandboxProvider();
        Directory.CreateDirectory(artifactRoot);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await WritePrivateFileAsync(passwordPath, password, cancellationToken);

        try
        {
            await provider.BuildImageAsync(
                attemptId,
                imageName,
                Path.Combine(repositoryRoot, "infra", "postgres", "poc8"),
                cancellationToken);
            await DockerAsync(
                [
                    "network", "create",
                    "--label", DockerSandboxProvider.ManagedLabel,
                    "--label", $"com.harness.attempt={attemptId}",
                    networkName,
                ],
                cancellationToken);
            await DockerAsync(
                [
                    "volume", "create",
                    "--label", DockerSandboxProvider.ManagedLabel,
                    "--label", $"com.harness.attempt={attemptId}",
                    volumeName,
                ],
                cancellationToken);
            await DockerAsync(
                [
                    "run", "--detach",
                    "--name", containerName,
                    "--label", DockerSandboxProvider.ManagedLabel,
                    "--label", $"com.harness.attempt={attemptId}",
                    "--network", networkName,
                    "--publish=127.0.0.1::5432/tcp",
                    "--mount", $"type=volume,source={volumeName},target=/var/lib/postgresql/data",
                    "--mount", $"type=bind,source={passwordPath},target=/run/secrets/postgres-password,readonly",
                    "--env", "POSTGRES_USER=harness",
                    "--env", "POSTGRES_DB=harness_recovery",
                    "--env", "POSTGRES_PASSWORD_FILE=/run/secrets/postgres-password",
                    "--memory", "256m",
                    "--cpus", "0.5",
                    "--pids-limit", "128",
                    "--security-opt", "no-new-privileges",
                    "--health-cmd", "pg_isready --username harness --dbname harness_recovery",
                    "--health-interval", "1s",
                    "--health-timeout", "2s",
                    "--health-retries", "30",
                    imageName,
                ],
                cancellationToken);
            await WaitUntilHealthyAsync(containerName, cancellationToken);
            var portOutput = await DockerAsync(
                ["port", containerName, "5432/tcp"],
                cancellationToken);
            var separator = portOutput.LastIndexOf(':');
            if (separator < 0 ||
                !int.TryParse(
                    portOutput[(separator + 1)..].Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var port))
            {
                throw new InvalidOperationException("Docker returned an invalid PostgreSQL port mapping.");
            }

            var connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = port,
                Database = "harness_recovery",
                Username = "harness",
                Password = password,
                SslMode = SslMode.Disable,
                IncludeErrorDetail = false,
                Timeout = 5,
                CommandTimeout = 5,
            }.ConnectionString;
            await WritePrivateFileAsync(connectionReferencePath, connectionString, cancellationToken);
            return new ManagedRecoveryPostgresFixture(
                attemptId,
                artifactRoot,
                connectionString,
                connectionReferencePath,
                provider);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await provider.CleanupAsync(attemptId, cleanup.Token);
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _provider.CleanupAsync(AttemptId, cleanup.Token);
        if (!(await _provider.DetectResourcesAsync(AttemptId, cleanup.Token)).IsEmpty)
        {
            throw new InvalidOperationException("Managed GNG-2 PostgreSQL resources survived cleanup.");
        }

        if (Directory.Exists(_artifactRoot))
        {
            Directory.Delete(_artifactRoot, recursive: true);
        }
    }

    private static async Task WritePrivateFileAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, content, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task WaitUntilHealthyAsync(
        string containerName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var status = await RunDockerAsync(
                ["container", "inspect", "--format", "{{.State.Health.Status}}", containerName],
                cancellationToken);
            if (status.ExitCode == 0 &&
                string.Equals(status.StandardOutput.Trim(), "healthy", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new InvalidOperationException("Managed GNG-2 PostgreSQL did not become healthy.");
    }

    private static async Task<string> DockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker command failed with exit {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    private static async Task<DockerResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Docker CLI did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new DockerResult(process.ExitCode, await standardOutput, await standardError);
    }

    private sealed record DockerResult(int ExitCode, string StandardOutput, string StandardError);
}
