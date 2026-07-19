using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Modules.Execution.Application.Sandbox;

namespace Harness.Modules.Execution.Infrastructure.Sandbox;

public sealed partial class DockerSandboxProvider : ISandboxProvider
{
    public const string ManagedLabel = "com.harness.managed=true";
    private const string AttemptLabelName = "com.harness.attempt";
    private readonly string _dockerExecutable;

    public DockerSandboxProvider(string? dockerExecutable = null)
    {
        _dockerExecutable = dockerExecutable ?? FindDockerExecutable();
    }

    public async Task BuildImageAsync(
        string attemptId,
        string imageName,
        string buildContext,
        CancellationToken cancellationToken = default)
    {
        ValidateAttemptId(attemptId);
        ValidateImageName(imageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildContext);
        var fullContext = Path.GetFullPath(buildContext);
        if (!Directory.Exists(fullContext))
        {
            throw new DirectoryNotFoundException($"Docker build context was not found: {fullContext}");
        }

        var result = await RunDockerAsync(
            [
                "build",
                "--label", ManagedLabel,
                "--label", $"{AttemptLabelName}={attemptId}",
                "--tag", imageName,
                fullContext,
            ],
            cancellationToken);
        EnsureSuccess("build the managed sandbox image", result);
    }

    public async Task<ISandboxProcessSession> OpenProcessSessionAsync(
        SandboxProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateProcessRequest(request);

        var internalNetwork = $"harness-internal-{request.AttemptId}";
        var egressNetwork = $"harness-egress-{request.AttemptId}";
        var cacheVolume = $"harness-cache-{request.AttemptId}";
        var stateVolume = $"harness-state-{request.AttemptId}";
        var proxyContainer = $"harness-proxy-{request.AttemptId}";
        var sandboxContainer = $"harness-sandbox-{request.AttemptId}";
        var attemptLabel = $"{AttemptLabelName}={request.AttemptId}";

        try
        {
            EnsureSuccess(
                "create the internal process network",
                await RunDockerAsync(
                    ["network", "create", "--internal", "--label", ManagedLabel, "--label", attemptLabel, internalNetwork],
                    cancellationToken));
            EnsureSuccess(
                "create the proxy process network",
                await RunDockerAsync(
                    ["network", "create", "--label", ManagedLabel, "--label", attemptLabel, egressNetwork],
                    cancellationToken));
            foreach (var volume in new[] { cacheVolume, stateVolume })
            {
                EnsureSuccess(
                    "create a managed process volume",
                    await RunDockerAsync(
                        ["volume", "create", "--label", ManagedLabel, "--label", attemptLabel, volume],
                        cancellationToken));
            }

            EnsureSuccess(
                "start the process egress proxy",
                await RunDockerAsync(
                    [
                        "run", "--detach",
                        "--name", proxyContainer,
                        "--hostname", "harness-proxy",
                        "--label", ManagedLabel,
                        "--label", attemptLabel,
                        "--network", internalNetwork,
                        "--network-alias", "harness-proxy",
                        "--read-only",
                        "--tmpfs", "/tmp:rw,noexec,nosuid,size=8m",
                        "--cap-drop", "ALL",
                        "--security-opt", "no-new-privileges",
                        "--memory", "64m",
                        "--cpus", "0.25",
                        "--pids-limit", "64",
                        request.ProxyImageName,
                        request.ProxyCommand,
                    ],
                    cancellationToken));
            EnsureSuccess(
                "connect the process proxy to egress",
                await RunDockerAsync(
                    ["network", "connect", "--alias", "harness-egress-proxy", egressNetwork, proxyContainer],
                    cancellationToken));

            var worktreeMount =
                $"type=bind,source={Path.GetFullPath(request.WorktreePath)},target=/workspace";
            var prefixArguments = new List<string>
            {
                "run", "--interactive", "--rm",
                "--name", sandboxContainer,
                "--label", ManagedLabel,
                "--label", attemptLabel,
                "--network", internalNetwork,
                "--read-only",
                "--tmpfs", $"/tmp:rw,noexec,nosuid,size={request.WritableDiskBytes.ToString(CultureInfo.InvariantCulture)}",
                "--storage-opt", $"size={request.WritableDiskBytes.ToString(CultureInfo.InvariantCulture)}",
                "--mount", worktreeMount,
                "--mount", $"type=volume,source={cacheVolume},target=/cache",
                "--mount", $"type=volume,source={stateVolume},target=/codex-state",
                "--workdir", "/workspace",
                "--env", "CODEX_HOME=/codex-state",
                "--env", "HTTP_PROXY=http://harness-proxy:8080",
                "--env", "HTTPS_PROXY=http://harness-proxy:8080",
                "--env", "NO_PROXY=",
                "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges",
                "--memory", request.MemoryBytes.ToString(CultureInfo.InvariantCulture),
                "--cpus", request.CpuLimit.ToString(CultureInfo.InvariantCulture),
                "--pids-limit", request.PidsLimit.ToString(CultureInfo.InvariantCulture),
                request.AgentImageName,
                request.ContainerExecutable,
            };
            var plan = new SandboxProcessPlan(
                _dockerExecutable,
                prefixArguments,
                "/workspace",
                RootFilesystemReadOnly: true,
                WorktreeIsolated: true,
                EgressRestricted: true,
                ResourceLimitsApplied: true);
            return new DockerSandboxProcessSession(this, request.AttemptId, plan);
        }
        catch
        {
            await CleanupAsync(request.AttemptId, CancellationToken.None);
            throw;
        }
    }

    public async Task<SandboxRunResult> RunAsync(
        SandboxRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var suffix = request.AttemptId;
        var internalNetwork = $"harness-internal-{suffix}";
        var egressNetwork = $"harness-egress-{suffix}";
        var cacheVolume = $"harness-cache-{suffix}";
        var targetContainer = $"harness-target-{suffix}";
        var proxyContainer = $"harness-proxy-{suffix}";
        var sandboxContainer = $"harness-sandbox-{suffix}";
        var attemptLabel = $"{AttemptLabelName}={request.AttemptId}";

        EnsureSuccess(
            "create the internal sandbox network",
            await RunDockerAsync(
                ["network", "create", "--internal", "--label", ManagedLabel, "--label", attemptLabel, internalNetwork],
                cancellationToken));
        EnsureSuccess(
            "create the proxy egress network",
            await RunDockerAsync(
                ["network", "create", "--label", ManagedLabel, "--label", attemptLabel, egressNetwork],
                cancellationToken));
        EnsureSuccess(
            "create the managed cache volume",
            await RunDockerAsync(
                ["volume", "create", "--label", ManagedLabel, "--label", attemptLabel, cacheVolume],
                cancellationToken));

        EnsureSuccess(
            "start the allowlisted target",
            await RunDockerAsync(
                [
                    "run", "--detach",
                    "--name", targetContainer,
                    "--label", ManagedLabel,
                    "--label", attemptLabel,
                    "--network", egressNetwork,
                    "--network-alias", "harness-target",
                    "--read-only",
                    "--tmpfs", "/tmp:rw,noexec,nosuid,size=4m",
                    "--cap-drop", "ALL",
                    "--security-opt", "no-new-privileges",
                    "--memory", "32m",
                    "--cpus", "0.25",
                    "--pids-limit", "32",
                    request.ImageName,
                    "target",
                ],
                cancellationToken));
        EnsureSuccess(
            "start the allowlisting proxy",
            await RunDockerAsync(
                [
                    "run", "--detach",
                    "--name", proxyContainer,
                    "--hostname", "harness-proxy",
                    "--label", ManagedLabel,
                    "--label", attemptLabel,
                    "--network", internalNetwork,
                    "--network-alias", "harness-proxy",
                    "--read-only",
                    "--tmpfs", "/tmp:rw,noexec,nosuid,size=4m",
                    "--cap-drop", "ALL",
                    "--security-opt", "no-new-privileges",
                    "--memory", "32m",
                    "--cpus", "0.25",
                    "--pids-limit", "32",
                    request.ImageName,
                    "proxy",
                ],
                cancellationToken));
        EnsureSuccess(
            "connect the proxy to the egress network",
            await RunDockerAsync(
                ["network", "connect", "--alias", "harness-target-proxy", egressNetwork, proxyContainer],
                cancellationToken));

        var worktreeMount = $"type=bind,source={Path.GetFullPath(request.WorktreePath)},target=/workspace";
        var cacheMount = $"type=volume,source={cacheVolume},target=/cache";
        var creation = await RunDockerAsync(
            [
                "create",
                "--name", sandboxContainer,
                "--label", ManagedLabel,
                "--label", attemptLabel,
                "--network", internalNetwork,
                "--read-only",
                "--tmpfs", $"/tmp:rw,noexec,nosuid,size={request.WritableDiskBytes.ToString(CultureInfo.InvariantCulture)}",
                "--storage-opt", $"size={request.WritableDiskBytes.ToString(CultureInfo.InvariantCulture)}",
                "--mount", worktreeMount,
                "--mount", cacheMount,
                "--workdir", "/workspace",
                "--env", "HTTP_PROXY=http://harness-proxy:8080",
                "--env", "HTTPS_PROXY=http://harness-proxy:8080",
                "--env", "NO_PROXY=",
                "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges",
                "--memory", request.MemoryBytes.ToString(CultureInfo.InvariantCulture),
                "--cpus", request.CpuLimit.ToString(CultureInfo.InvariantCulture),
                "--pids-limit", request.PidsLimit.ToString(CultureInfo.InvariantCulture),
                request.ImageName,
                "probe",
            ],
            cancellationToken);
        EnsureSuccess("create the sandbox container", creation);

        var execution = await RunDockerAsync(["start", "--attach", sandboxContainer], cancellationToken);
        var inspection = await InspectSandboxAsync(sandboxContainer, cancellationToken);
        return new SandboxRunResult(
            sandboxContainer,
            execution.ExitCode,
            execution.StandardOutput.Trim(),
            inspection.CpuLimit,
            inspection.MemoryBytes,
            inspection.WritableDiskBytes,
            inspection.PidsLimit,
            inspection.RootFilesystemReadOnly,
            inspection.NetworkMode);
    }

    public async Task<SandboxResourceInventory> DetectResourcesAsync(
        string attemptId,
        CancellationToken cancellationToken = default)
    {
        ValidateAttemptId(attemptId);
        var filter = $"label={AttemptLabelName}={attemptId}";
        return new SandboxResourceInventory(
            await ListNamesAsync(["container", "ls", "--all", "--filter", filter, "--format", "{{.Names}}"], cancellationToken),
            await ListNamesAsync(["network", "ls", "--filter", filter, "--format", "{{.Name}}"], cancellationToken),
            await ListNamesAsync(["volume", "ls", "--filter", filter, "--format", "{{.Name}}"], cancellationToken),
            await ListNamesAsync(["image", "ls", "--filter", filter, "--format", "{{.Repository}}:{{.Tag}}"], cancellationToken));
    }

    public async Task CleanupAsync(
        string attemptId,
        CancellationToken cancellationToken = default)
    {
        var inventory = await DetectResourcesAsync(attemptId, cancellationToken);
        foreach (var container in inventory.Containers)
        {
            await RemoveManagedResourceAsync("container", container, ["container", "rm", "--force", container], cancellationToken);
        }

        foreach (var network in inventory.Networks)
        {
            await RemoveManagedResourceAsync("network", network, ["network", "rm", network], cancellationToken);
        }

        foreach (var volume in inventory.Volumes)
        {
            await RemoveManagedResourceAsync("volume", volume, ["volume", "rm", volume], cancellationToken);
        }

        foreach (var image in inventory.Images)
        {
            await RemoveManagedResourceAsync("image", image, ["image", "rm", image], cancellationToken);
        }
    }

    private async Task<SandboxInspection> InspectSandboxAsync(
        string containerName,
        CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(["container", "inspect", containerName], cancellationToken);
        EnsureSuccess("inspect the sandbox container", result);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement[0];
        var hostConfig = root.GetProperty("HostConfig");
        var nanoCpus = hostConfig.GetProperty("NanoCpus").GetInt64();
        var storageSize = hostConfig.GetProperty("StorageOpt").GetProperty("size").GetString();
        return new SandboxInspection(
            nanoCpus / 1_000_000_000m,
            hostConfig.GetProperty("Memory").GetInt64(),
            long.Parse(storageSize ?? "0", CultureInfo.InvariantCulture),
            hostConfig.GetProperty("PidsLimit").GetInt32(),
            hostConfig.GetProperty("ReadonlyRootfs").GetBoolean(),
            hostConfig.GetProperty("NetworkMode").GetString() ?? string.Empty);
    }

    private async Task RemoveManagedResourceAsync(
        string resourceType,
        string resourceName,
        IReadOnlyList<string> removalArguments,
        CancellationToken cancellationToken)
    {
        if (!resourceName.StartsWith("harness-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cleanup refused a resource without the harness- prefix.");
        }

        var inspectionArguments = resourceType is "image" or "container"
            ? new[] { resourceType, "inspect", "--format", "{{index .Config.Labels \"com.harness.managed\"}}", resourceName }
            : [resourceType, "inspect", "--format", "{{index .Labels \"com.harness.managed\"}}", resourceName];
        var inspection = await RunDockerAsync(inspectionArguments, cancellationToken);
        EnsureSuccess($"inspect managed {resourceType} {resourceName}", inspection);
        if (!string.Equals(inspection.StandardOutput.Trim(), "true", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cleanup refused a resource without com.harness.managed=true.");
        }

        EnsureSuccess(
            $"remove managed {resourceType} {resourceName}",
            await RunDockerAsync(removalArguments, cancellationToken));
    }

    private async Task<IReadOnlyList<string>> ListNamesAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunDockerAsync(arguments, cancellationToken);
        EnsureSuccess("list managed Docker resources", result);
        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name != "<none>:<none>")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<DockerCommandResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _dockerExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
        return new DockerCommandResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static void ValidateRequest(SandboxRunRequest request)
    {
        ValidateAttemptId(request.AttemptId);
        ValidateImageName(request.ImageName);
        ValidateResourceLimits(
            request.ExecutionRoot,
            request.WorktreePath,
            request.CpuLimit,
            request.MemoryBytes,
            request.WritableDiskBytes,
            request.PidsLimit,
            nameof(request));
    }

    private static void ValidateProcessRequest(SandboxProcessRequest request)
    {
        ValidateAttemptId(request.AttemptId);
        ValidateImageName(request.AgentImageName);
        ValidateImageName(request.ProxyImageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProxyCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContainerExecutable);
        ValidateResourceLimits(
            request.ExecutionRoot,
            request.WorktreePath,
            request.CpuLimit,
            request.MemoryBytes,
            request.WritableDiskBytes,
            request.PidsLimit,
            nameof(request));
    }

    private static void ValidateResourceLimits(
        string executionRootValue,
        string worktreePathValue,
        decimal cpuLimit,
        long memoryBytes,
        long writableDiskBytes,
        int pidsLimit,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionRootValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePathValue);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cpuLimit, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryBytes, 16 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(writableDiskBytes, 4 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(pidsLimit, 16);

        var executionRoot = Path.GetFullPath(executionRootValue);
        var worktree = Path.GetFullPath(worktreePathValue);
        var relative = Path.GetRelativePath(executionRoot, worktree);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) ||
            worktree.Contains(',') ||
            !Directory.Exists(worktree))
        {
            throw new ArgumentException(
                "The worktree must be an existing path under the execution root.",
                parameterName);
        }
    }

    private static void ValidateAttemptId(string attemptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        if (!SafeSuffix().IsMatch(attemptId))
        {
            throw new ArgumentException("Attempt id must contain 6-24 lowercase letters, digits or hyphens.", nameof(attemptId));
        }
    }

    private static void ValidateImageName(string imageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        if (!imageName.StartsWith("harness-", StringComparison.Ordinal) || imageName[0] == '-')
        {
            throw new ArgumentException("Managed image tags must use the harness- prefix.", nameof(imageName));
        }
    }

    private static string FindDockerExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "docker");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException("Docker CLI was not found on PATH.");
    }

    private static void EnsureSuccess(string operation, DockerCommandResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker could not {operation} (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{5,23}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSuffix();

    private sealed record DockerCommandResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record SandboxInspection(
        decimal CpuLimit,
        long MemoryBytes,
        long WritableDiskBytes,
        int PidsLimit,
        bool RootFilesystemReadOnly,
        string NetworkMode);

    private sealed class DockerSandboxProcessSession(
        DockerSandboxProvider owner,
        string attemptId,
        SandboxProcessPlan processPlan) : ISandboxProcessSession
    {
        private int _disposed;

        public SandboxProcessPlan ProcessPlan { get; } = processPlan;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await owner.CleanupAsync(attemptId, CancellationToken.None);
            }
        }
    }
}
