using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.RunTargets;

namespace Harness.Host.RunTargets;

public sealed class DockerRunTargetLifecycle
{
    private const string ManagedLabelName = "com.harness.managed";
    private const string RunTargetLabelName = "com.harness.run-target";

    public bool IsDockerLaunch(RunTargetLaunchRecord launch) =>
        launch.Environment.TryGetValue("HARNESS_RUN_DOCKER_MODE", out var mode) &&
        mode is "dockerfile" or "compose";

    public async Task<ProcessStartInfo> PrepareAsync(
        RunTargetLaunchRecord launch,
        Func<string, Task> appendLog,
        CancellationToken cancellationToken)
    {
        if (!IsDockerLaunch(launch))
            throw new RunTargetValidationException("The run target is not a managed Docker launch.");
        if (launch.Target.Port is not > 0 ||
            !launch.Environment.TryGetValue("HARNESS_RUN_CONTAINER_PORT", out var rawContainerPort) ||
            !int.TryParse(rawContainerPort, NumberStyles.None, CultureInfo.InvariantCulture, out var containerPort) ||
            containerPort is <= 0 or > 65535)
        {
            throw new RunTargetValidationException("The managed Docker target has no valid dynamic port mapping.");
        }

        await CleanupAsync(launch.Target.Id, appendLog, cancellationToken);
        var resourceName = ResourceName(launch.Target.Id);
        var docker = launch.Executable;
        var mode = launch.Environment["HARNESS_RUN_DOCKER_MODE"];
        if (!launch.Environment.TryGetValue("HARNESS_RUN_DOCKER_FILE", out var sourceFile) ||
            !File.Exists(sourceFile))
        {
            throw new RunTargetValidationException("The managed Docker source file no longer exists.");
        }

        if (mode == "dockerfile")
        {
            var image = $"{resourceName}:latest";
            var build = await RunAsync(
                docker,
                [
                    "build",
                    "--label", $"{ManagedLabelName}=true",
                    "--label", $"{RunTargetLabelName}={launch.Target.Id}",
                    "--tag", image,
                    "--file", sourceFile,
                    launch.WorkingDirectory,
                ],
                launch.WorkingDirectory,
                cancellationToken);
            await AppendOutputAsync(build, appendLog);
            EnsureSuccess(build, "The managed Docker image could not be built.");
            return StartInfo(
                docker,
                [
                    "run", "--rm",
                    "--name", resourceName,
                    "--label", $"{ManagedLabelName}=true",
                    "--label", $"{RunTargetLabelName}={launch.Target.Id}",
                    "--publish", $"127.0.0.1:{launch.Target.Port.Value.ToString(CultureInfo.InvariantCulture)}:{containerPort.ToString(CultureInfo.InvariantCulture)}",
                    image,
                ],
                launch.WorkingDirectory);
        }

        if (!launch.Environment.TryGetValue("HARNESS_RUN_COMPOSE_SERVICE", out var selectedService))
            throw new RunTargetValidationException("The managed Compose target has no selected HTTP service.");
        var configuration = await LoadComposeConfigurationAsync(
            docker,
            sourceFile,
            resourceName,
            launch.WorkingDirectory,
            cancellationToken);
        ValidateCompose(configuration, resourceName, selectedService);
        var overrideFile = await WriteComposeOverrideAsync(
            configuration,
            launch.Target.Id,
            selectedService,
            launch.Target.Port.Value,
            containerPort,
            cancellationToken);
        return StartInfo(
            docker,
            [
                "compose",
                "--file", sourceFile,
                "--file", overrideFile,
                "--project-name", resourceName,
                "up", "--build", "--remove-orphans",
            ],
            launch.WorkingDirectory);
    }

    public async Task CleanupAsync(
        string targetId,
        Func<string, Task> appendLog,
        CancellationToken cancellationToken)
    {
        var docker = ResolveDocker();
        var filter = $"label={RunTargetLabelName}={targetId}";
        var containers = await ListAsync(docker, ["container", "ls", "--all", "--filter", filter, "--format", "{{.Names}}"], cancellationToken);
        var networks = await ListAsync(docker, ["network", "ls", "--filter", filter, "--format", "{{.Name}}"], cancellationToken);
        var volumes = await ListAsync(docker, ["volume", "ls", "--filter", filter, "--format", "{{.Name}}"], cancellationToken);
        var images = await ListAsync(docker, ["image", "ls", "--filter", filter, "--format", "{{.Repository}}:{{.Tag}}"], cancellationToken);

        foreach (var resource in containers)
            await RemoveAsync(docker, "container", resource, ["container", "rm", "--force", resource], targetId, appendLog, cancellationToken);
        foreach (var resource in networks)
            await RemoveAsync(docker, "network", resource, ["network", "rm", resource], targetId, appendLog, cancellationToken);
        foreach (var resource in volumes)
            await RemoveAsync(docker, "volume", resource, ["volume", "rm", resource], targetId, appendLog, cancellationToken);
        foreach (var resource in images)
            await RemoveAsync(docker, "image", resource, ["image", "rm", resource], targetId, appendLog, cancellationToken);

        var temporaryDirectory = OverrideDirectory(targetId);
        if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
    }

    private static async Task<JsonDocument> LoadComposeConfigurationAsync(
        string docker,
        string composeFile,
        string projectName,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            docker,
            ["compose", "--file", composeFile, "--project-name", projectName, "config", "--format", "json"],
            workingDirectory,
            cancellationToken);
        EnsureSuccess(result, "The Compose configuration is invalid.");
        try
        {
            return JsonDocument.Parse(result.StandardOutput);
        }
        catch (JsonException exception)
        {
            throw new RunTargetValidationException($"The Compose configuration could not be parsed: {exception.Message}");
        }
    }

    private static void ValidateCompose(JsonDocument configuration, string projectName, string selectedService)
    {
        var root = configuration.RootElement;
        if (!root.TryGetProperty("services", out var services) ||
            services.ValueKind != JsonValueKind.Object ||
            !services.TryGetProperty(selectedService, out _))
        {
            throw new RunTargetValidationException("The selected Compose service no longer exists.");
        }

        foreach (var service in services.EnumerateObject())
        {
            if (service.Value.TryGetProperty("container_name", out var containerName) &&
                containerName.ValueKind == JsonValueKind.String &&
                !HasHarnessPrefix(containerName.GetString()))
            {
                throw new RunTargetValidationException("Compose container_name must use the harness- prefix.");
            }
            if (service.Value.TryGetProperty("ports", out var ports) &&
                ports.ValueKind == JsonValueKind.Array && ports.GetArrayLength() > 0)
            {
                throw new RunTargetValidationException("Compose targets with pre-published host ports are refused; expose a container-only port instead.");
            }
            if (service.Value.TryGetProperty("build", out var build) && build.ValueKind != JsonValueKind.Null &&
                service.Value.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.String &&
                image.GetString() is { } imageName &&
                !imageName.StartsWith(projectName + "-", StringComparison.Ordinal) &&
                !HasHarnessPrefix(imageName))
            {
                throw new RunTargetValidationException("Compose-built images must use the harness- prefix.");
            }
            if (service.Value.TryGetProperty("volumes", out var mounts) && mounts.ValueKind == JsonValueKind.Array)
            {
                foreach (var mount in mounts.EnumerateArray())
                {
                    if (mount.TryGetProperty("type", out var type) && type.GetString() == "volume" &&
                        (!mount.TryGetProperty("source", out var source) || string.IsNullOrWhiteSpace(source.GetString())))
                    {
                        throw new RunTargetValidationException("Anonymous Compose volumes are refused because their ownership cannot be audited.");
                    }
                }
            }
        }

        ValidateComposeResources(root, "networks", projectName);
        ValidateComposeResources(root, "volumes", projectName);
    }

    private static void ValidateComposeResources(JsonElement root, string propertyName, string projectName)
    {
        if (!root.TryGetProperty(propertyName, out var resources) || resources.ValueKind != JsonValueKind.Object) return;
        foreach (var resource in resources.EnumerateObject())
        {
            var external = resource.Value.TryGetProperty("external", out var externalNode) && externalNode.ValueKind == JsonValueKind.True;
            if (external) continue;
            if (resource.Value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                name.GetString() is { } resourceName &&
                !resourceName.StartsWith(projectName + "_", StringComparison.Ordinal) &&
                !HasHarnessPrefix(resourceName))
            {
                throw new RunTargetValidationException($"Compose {propertyName} must use the harness- prefix.");
            }
        }
    }

    private static async Task<string> WriteComposeOverrideAsync(
        JsonDocument configuration,
        string targetId,
        string selectedService,
        int hostPort,
        int containerPort,
        CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManagedLabelName] = "true",
            [RunTargetLabelName] = targetId,
        };
        var services = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var service in configuration.RootElement.GetProperty("services").EnumerateObject())
        {
            var value = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["labels"] = labels,
            };
            if (service.Value.TryGetProperty("build", out var build) && build.ValueKind != JsonValueKind.Null)
                value["build"] = new Dictionary<string, object?> { ["labels"] = labels };
            if (service.Name == selectedService)
                value["ports"] = new[] { $"127.0.0.1:{hostPort.ToString(CultureInfo.InvariantCulture)}:{containerPort.ToString(CultureInfo.InvariantCulture)}" };
            services[service.Name] = value;
        }

        var root = new Dictionary<string, object?>(StringComparer.Ordinal) { ["services"] = services };
        AddResourceOverrides(configuration.RootElement, root, "networks", labels);
        AddResourceOverrides(configuration.RootElement, root, "volumes", labels);
        var directory = OverrideDirectory(targetId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "compose.override.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(root), cancellationToken);
        return path;
    }

    private static void AddResourceOverrides(
        JsonElement configuration,
        Dictionary<string, object?> root,
        string propertyName,
        IReadOnlyDictionary<string, string> labels)
    {
        if (!configuration.TryGetProperty(propertyName, out var resources) || resources.ValueKind != JsonValueKind.Object) return;
        var overrides = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var resource in resources.EnumerateObject())
        {
            var external = resource.Value.TryGetProperty("external", out var externalNode) && externalNode.ValueKind == JsonValueKind.True;
            if (!external) overrides[resource.Name] = new Dictionary<string, object?> { ["labels"] = labels };
        }
        if (overrides.Count > 0) root[propertyName] = overrides;
    }

    private static async Task RemoveAsync(
        string docker,
        string resourceType,
        string resource,
        IReadOnlyList<string> removeArguments,
        string targetId,
        Func<string, Task> appendLog,
        CancellationToken cancellationToken)
    {
        if (!HasHarnessPrefix(resource))
            throw new RunTargetValidationException("Docker cleanup refused a resource without the harness- prefix.");
        var labelPath = resourceType is "container" or "image" ? ".Config.Labels" : ".Labels";
        var managed = await RunAsync(
            docker,
            [resourceType, "inspect", "--format", $"{{{{index {labelPath} \"{ManagedLabelName}\"}}}}", resource],
            Directory.GetCurrentDirectory(),
            cancellationToken);
        var owner = await RunAsync(
            docker,
            [resourceType, "inspect", "--format", $"{{{{index {labelPath} \"{RunTargetLabelName}\"}}}}", resource],
            Directory.GetCurrentDirectory(),
            cancellationToken);
        if (managed.ExitCode != 0 || owner.ExitCode != 0 ||
            managed.StandardOutput.Trim() != "true" || owner.StandardOutput.Trim() != targetId)
        {
            throw new RunTargetValidationException("Docker cleanup refused a resource without exact Harness ownership labels.");
        }
        var removal = await RunAsync(docker, removeArguments, Directory.GetCurrentDirectory(), cancellationToken);
        await AppendOutputAsync(removal, appendLog);
        EnsureSuccess(removal, $"The managed Docker {resourceType} could not be removed.");
    }

    private static async Task<IReadOnlyList<string>> ListAsync(
        string docker,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(docker, arguments, Directory.GetCurrentDirectory(), cancellationToken);
        EnsureSuccess(result, "Managed Docker resources could not be inventoried.");
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value != "<none>:<none>")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task<DockerCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var start = StartInfo(executable, arguments, workingDirectory);
        using var process = Process.Start(start)
            ?? throw new RunTargetValidationException("The Docker CLI could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new(process.ExitCode, await standardOutput, await standardError);
    }

    private static async Task AppendOutputAsync(DockerCommandResult result, Func<string, Task> appendLog)
    {
        foreach (var line in (result.StandardOutput + "\n" + result.StandardError)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await appendLog(line);
    }

    private static void EnsureSuccess(DockerCommandResult result, string message)
    {
        if (result.ExitCode != 0) throw new RunTargetValidationException(message);
    }

    private static bool HasHarnessPrefix(string? value) =>
        value?.StartsWith("harness-", StringComparison.Ordinal) == true;

    private static string ResourceName(string targetId) =>
        $"harness-run-{targetId.ToLowerInvariant()}";

    private static string OverrideDirectory(string targetId) =>
        Path.Combine(Path.GetTempPath(), "harness-run-targets", targetId.ToLowerInvariant());

    private static string ResolveDocker()
    {
        var configured = Environment.GetEnvironmentVariable("HARNESS_DOCKER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var candidate in new[] { "/usr/local/bin/docker", "/opt/homebrew/bin/docker", "/usr/bin/docker" })
            if (File.Exists(candidate)) return candidate;
        return "docker";
    }

    private sealed record DockerCommandResult(int ExitCode, string StandardOutput, string StandardError);
}
