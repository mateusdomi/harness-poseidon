using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Modules.Agents.Application.Execution;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.RunTargets;
using Harness.SharedKernel.Time;

namespace Harness.Host.RunTargets;

public sealed record RunTargetDetectionContext(
    string TenantId,
    string ProjectId,
    string ConversationId,
    string AgentId);

public sealed class RunTargetAgentFallback(
    IAgentExecutor executor,
    IClock clock,
    ILogger<RunTargetAgentFallback> logger,
    IAuditEventStore? auditEvents = null)
{
    private static readonly HashSet<string> RootProperties =
        new(["version", "targets"], StringComparer.Ordinal);
    private static readonly HashSet<string> TargetProperties =
        new(["name", "kind", "workingDirectory", "executable", "arguments", "environment"], StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedEnvironmentCommands =
        new(["bundle", "cargo", "dotnet", "go", "gradle", "java", "mvn", "node", "npm", "php", "python", "python3", "ruby"], StringComparer.Ordinal);
    private static readonly string[] SecretFragments =
        ["CREDENTIAL", "KEY", "PASSWORD", "SECRET", "TOKEN"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, string, string, Exception?> InvalidAgentOutput =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(4201, nameof(InvalidAgentOutput)),
            "Run-target agent fallback returned no usable target for project {ProjectId}; error={ErrorType}.");
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<RunTargetDefinition>> InferAsync(
        string rootPath,
        RunTargetDetectionContext context,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootPath);
        var cacheKey = $"{context.TenantId}\n{context.ProjectId}\n{root}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > clock.UtcNow)
            return cached.Definitions;

        var gate = _gates.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(cacheKey, out cached) && cached.ExpiresAt > clock.UtcNow)
                return cached.Definitions;
            var definitions = await InferCoreAsync(root, context, cancellationToken);
            _cache[cacheKey] = new(definitions, clock.UtcNow.AddMinutes(5));
            return definitions;
        }
        finally
        {
            gate.Release();
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "An optional last-resort agent must never make deterministic run-target discovery unavailable.")]
    private async Task<IReadOnlyList<RunTargetDefinition>> InferCoreAsync(
        string root,
        RunTargetDetectionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var inventory = EnumerateInventory(root, cancellationToken).Take(200).ToArray();
            var request = new AgentExecutionRequest(
                context.TenantId,
                context.ProjectId,
                context.ConversationId,
                context.AgentId,
                CreateInstruction(inventory),
                "{\"purpose\":\"run-target-detection\",\"version\":1}",
                root);
            var execution = await executor.ExecuteAsync(request, cancellationToken);
            var chiefOutput = ChiefTurnOutputContract.Parse(execution.StructuredOutput);
            var definitions = ParseDefinitions(root, chiefOutput.Response);
            if (auditEvents is not null)
            {
                await auditEvents.AppendAsync(
                    new AuditEventAppendCommand(
                        context.TenantId,
                        "agent",
                        context.AgentId,
                        "runTarget.agentDetectionCompleted",
                        "project",
                        context.ProjectId,
                        $"Agent fallback proposed {definitions.Length.ToString(CultureInfo.InvariantCulture)} safe run target(s).",
                        clock.UtcNow),
                    cancellationToken);
            }
            return definitions;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            InvalidAgentOutput(logger, context.ProjectId, exception.GetType().Name, null);
            return [];
        }
    }

    private static string CreateInstruction(IReadOnlyList<string> inventory) =>
        """
        RUN_TARGET_DETECTION_V1
        You are the last-resort run-target detector. Inspect only the supplied working directory.
        Do not modify files and do not execute a project. Return your answer in the Chief response
        string as strict compact JSON with this exact shape:
        {"version":1,"targets":[{"name":"Service","kind":"http|process","workingDirectory":"relative/path","executable":"/usr/bin/env","arguments":["runtime","arg","{port}"],"environment":{"PORT":"{port}"}}]}
        Return at most 5 targets. Use {port} for every HTTP port. Never propose a shell, destructive
        command, credential, fixed host port, container command, or path outside the project. Use an
        empty targets array when no safe start command can be established. File inventory follows:
        """ + JsonSerializer.Serialize(inventory, JsonOptions);

    private static RunTargetDefinition[] ParseDefinitions(string root, string response)
    {
        using var document = JsonDocument.Parse(response, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 12,
        });
        var value = document.RootElement;
        EnsureObject(value, RootProperties, "agent detection output");
        if (!value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            version.GetInt32() != 1 || !value.TryGetProperty("targets", out var targets) ||
            targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() > 5)
        {
            throw new RunTargetValidationException("Run-target agent output version or target count is invalid.");
        }

        var definitions = new List<RunTargetDefinition>();
        foreach (var target in targets.EnumerateArray())
            definitions.Add(ParseDefinition(root, target));
        return definitions.GroupBy(definition => definition.Fingerprint, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static RunTargetDefinition ParseDefinition(string root, JsonElement target)
    {
        EnsureObject(target, TargetProperties, "agent target");
        var name = RequiredText(target, "name", 1, 160);
        var kind = RequiredText(target, "kind", 1, 20);
        if (kind is not ("http" or "process"))
            throw new RunTargetValidationException("Run-target agent kind is invalid.");
        var relativeWorkingDirectory = RequiredText(target, "workingDirectory", 1, 500);
        if (Path.IsPathRooted(relativeWorkingDirectory))
            throw new RunTargetValidationException("Run-target agent working directory must be relative.");
        var workingDirectory = ConfinedPath(root, Path.Combine(root, relativeWorkingDirectory));
        if (!Directory.Exists(workingDirectory))
            throw new RunTargetValidationException("Run-target agent working directory does not exist.");

        if (!target.TryGetProperty("arguments", out var argumentsNode) ||
            argumentsNode.ValueKind != JsonValueKind.Array || argumentsNode.GetArrayLength() > 50)
        {
            throw new RunTargetValidationException("Run-target agent arguments are invalid.");
        }
        var arguments = argumentsNode.EnumerateArray()
            .Select(argument => Text(argument, "argument", 0, 500))
            .ToArray();
        var executableProposal = RequiredText(target, "executable", 1, 500);
        var executable = ResolveExecutable(root, workingDirectory, executableProposal, arguments);
        var environment = ParseEnvironment(target);

        int? port = null;
        string? url = null;
        if (kind == "http")
        {
            if (!arguments.Any(argument => argument.Contains("{port}", StringComparison.Ordinal)) &&
                !environment.Values.Any(item => item.Contains("{port}", StringComparison.Ordinal)))
            {
                throw new RunTargetValidationException("HTTP agent targets must use the dynamic {port} placeholder.");
            }
            port = FreePort();
            url = $"http://127.0.0.1:{port.Value.ToString(CultureInfo.InvariantCulture)}";
            arguments = arguments.Select(argument => ReplacePort(argument, port.Value)).ToArray();
            environment = environment.ToDictionary(
                item => item.Key,
                item => ReplacePort(item.Value, port.Value),
                StringComparer.Ordinal);
        }
        else if (arguments.Any(argument => argument.Contains("{port}", StringComparison.Ordinal)) ||
                 environment.Values.Any(item => item.Contains("{port}", StringComparison.Ordinal)))
        {
            throw new RunTargetValidationException("Process-only agent targets cannot use a port placeholder.");
        }

        environment["HARNESS_RUN_DETECTION_SOURCE"] = "agent";
        var canonical = JsonSerializer.Serialize(
            new { name, kind, workingDirectory, executable, arguments, environment },
            JsonOptions);
        return new(
            Fingerprint(root, canonical),
            $"{name} (Agent)",
            kind,
            url,
            port,
            workingDirectory,
            executable,
            arguments,
            environment);
    }

    private static Dictionary<string, string> ParseEnvironment(JsonElement target)
    {
        if (!target.TryGetProperty("environment", out var environmentNode) ||
            environmentNode.ValueKind != JsonValueKind.Object || environmentNode.EnumerateObject().Count() > 20)
        {
            throw new RunTargetValidationException("Run-target agent environment is invalid.");
        }
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in environmentNode.EnumerateObject())
        {
            if (!IsEnvironmentName(property.Name) ||
                SecretFragments.Any(fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RunTargetValidationException("Run-target agent environment contains a forbidden key.");
            }
            environment[property.Name] = Text(property.Value, "environment value", 0, 1_000);
        }
        return environment;
    }

    private static string ResolveExecutable(
        string root,
        string workingDirectory,
        string proposal,
        string[] arguments)
    {
        if (proposal == "/usr/bin/env")
        {
            if (arguments.Length == 0 || !AllowedEnvironmentCommands.Contains(arguments[0]))
                throw new RunTargetValidationException("Run-target agent runtime command is not allowlisted.");
            return proposal;
        }

        var executable = Path.IsPathRooted(proposal)
            ? Path.GetFullPath(proposal)
            : Path.GetFullPath(Path.Combine(workingDirectory, proposal));
        executable = ConfinedPath(root, executable);
        if (!File.Exists(executable) || File.GetAttributes(executable).HasFlag(FileAttributes.ReparsePoint))
            throw new RunTargetValidationException("Run-target agent executable is not a regular project file.");
        return executable;
    }

    private static string ConfinedPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new RunTargetValidationException("Run-target agent path escapes the project root.");
        }
        var current = fullRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new RunTargetValidationException("Run-target agent path crosses a symbolic link.");
            }
        }
        return fullPath;
    }

    private static void EnsureObject(JsonElement value, HashSet<string> properties, string description)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Any(property => !properties.Contains(property.Name)))
        {
            throw new RunTargetValidationException($"The {description} contains an invalid property.");
        }
    }

    private static string RequiredText(JsonElement parent, string property, int minimum, int maximum)
    {
        if (!parent.TryGetProperty(property, out var value))
            throw new RunTargetValidationException($"Run-target agent property {property} is required.");
        return Text(value, property, minimum, maximum);
    }

    private static string Text(JsonElement value, string description, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new RunTargetValidationException($"Run-target agent {description} must be text.");
        var text = value.GetString()?.Trim() ?? string.Empty;
        if (text.Length < minimum || text.Length > maximum || text.Any(char.IsControl))
            throw new RunTargetValidationException($"Run-target agent {description} is invalid.");
        return text;
    }

    private static bool IsEnvironmentName(string value) =>
        value.Length is > 0 and <= 100 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string ReplacePort(string value, int port) =>
        value.Replace("{port}", port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string Fingerprint(string root, string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"agent\n{root}\n{canonical}")));

    private static IEnumerable<string> EnumerateInventory(string root, CancellationToken cancellationToken)
    {
        var ignored = new HashSet<string>(
            [".git", ".idea", ".vs", ".vscode", "bin", "build", "dist", "node_modules", "obj"],
            StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(current.Path); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files.Order(StringComparer.Ordinal))
                yield return Path.GetRelativePath(root, file);
            if (current.Depth >= 4) continue;
            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(current.Path); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var directory in directories.OrderDescending(StringComparer.Ordinal))
            {
                var info = new DirectoryInfo(directory);
                if (!ignored.Contains(info.Name) && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push((directory, current.Depth + 1));
            }
        }
    }

    private sealed record CacheEntry(
        IReadOnlyList<RunTargetDefinition> Definitions,
        DateTimeOffset ExpiresAt);
}
