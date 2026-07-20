using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Modules.Agents.Application.Execution;
using Harness.SharedKernel.Security;

namespace Harness.Modules.Agents.Infrastructure.OmpRpc;

public sealed record OmpRpcAgentExecutorOptions
{
    public bool Enabled { get; init; }

    public string Executable { get; init; } = "omp";

    public IReadOnlyList<string> PrefixArguments { get; init; } = [];

    public string? ProcessWorkingDirectory { get; init; }

    public string? AgentWorkingDirectory { get; init; }

    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(2);

    public int MaxMessageCharacters { get; init; } = 1_000_000;
}

public sealed record AgentExecutorDescriptor(
    string Id,
    bool Available,
    bool Enabled,
    string? ExecutablePath,
    string License,
    string AvailabilityReason);

public sealed class AgentExecutorCatalog(OmpRpcAgentExecutorOptions ompOptions)
{
    private readonly OmpRpcAgentExecutorOptions _ompOptions =
        ompOptions ?? throw new ArgumentNullException(nameof(ompOptions));

    public IReadOnlyList<AgentExecutorDescriptor> List()
    {
        var path = OmpExecutableDetector.Find(_ompOptions.Executable);
        return
        [
            new("fake", true, true, null, "Poseidon", "available"),
            new("codex-cli", true, true, OmpExecutableDetector.Find("codex"), "provider", "configured by sandbox"),
            new("microsoft-agent-framework", false, false, null, "MIT", "provider adapter not configured"),
            new("omp-rpc", path is not null, _ompOptions.Enabled, path, "MIT", path is null ? "binary_not_found" : "available"),
        ];
    }

    public OmpRpcAgentExecutor? TryCreateOmp()
    {
        var path = OmpExecutableDetector.Find(_ompOptions.Executable);
        return !_ompOptions.Enabled || path is null
            ? null
            : new OmpRpcAgentExecutor(_ompOptions with { Executable = path });
    }
}

public static class OmpExecutableDetector
{
    public static string? Find(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (Path.IsPathRooted(executable)) return IsExecutable(executable) ? Path.GetFullPath(executable) : null;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : [string.Empty];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, executable + extension.ToLowerInvariant());
                if (IsExecutable(candidate)) return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
}

public sealed class OmpRpcAgentExecutor(OmpRpcAgentExecutorOptions options) : IAgentExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OmpRpcAgentExecutorOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var executable = OmpExecutableDetector.Find(_options.Executable)
            ?? throw new AgentExecutorUnavailableException("omp-rpc", "binary_not_found");
        var arguments = _options.PrefixArguments.Concat(["--mode", "rpc"]).ToArray();
        SecretTextProtector.ThrowIfSensitiveCommandArguments(arguments, nameof(_options));
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _options.ProcessWorkingDirectory ?? request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        SanitizeEnvironment(start);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("OMP RPC process could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TurnTimeout);
        var chunks = new List<string>();
        var requestId = request.ConversationId;
        var rpcRequest = new OmpRpcExecuteRequest(
            "1.0.0",
            "execute",
            requestId,
            request.TenantId,
            request.ProjectId,
            request.AgentId,
            request.Instruction,
            request.StatusDigestJson,
            _options.AgentWorkingDirectory ?? request.WorkingDirectory,
            request.SessionId,
            request.Model,
            request.Effort);
        OmpRpcResultMessage? result = null;
        try
        {
            var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(rpcRequest, JsonOptions));
            await process.StandardInput.FlushAsync(timeout.Token);
            while (result is null)
            {
                var read = process.StandardOutput.ReadLineAsync(timeout.Token).AsTask();
                var heartbeatDeadline = Task.Delay(_options.HeartbeatTimeout, timeout.Token);
                var completed = await Task.WhenAny(read, heartbeatDeadline);
                if (completed != read)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    throw new TimeoutException("OMP RPC heartbeat deadline expired.");
                }

                var line = await read;
                if (line is null)
                {
                    var errorType = (await standardError.WaitAsync(timeout.Token)).Length > 0
                        ? "stderr_nonempty"
                        : "unexpected_eof";
                    throw new OmpRpcProtocolException(errorType);
                }

                if (line.Length > _options.MaxMessageCharacters)
                {
                    throw new OmpRpcProtocolException("message_too_large");
                }

                var envelope = DeserializeEnvelope(line);
                ValidateEnvelope(envelope);
                if (!string.Equals(envelope.RequestId, requestId, StringComparison.Ordinal))
                {
                    throw new OmpRpcProtocolException("request_id_mismatch");
                }

                switch (envelope.Type)
                {
                    case "heartbeat":
                        Validate(Deserialize<OmpRpcHeartbeatMessage>(line));
                        break;
                    case "chunk":
                        var chunk = Deserialize<OmpRpcChunkMessage>(line);
                        Validate(chunk);
                        chunks.Add(chunk.Text);
                        break;
                    case "result":
                        result = Deserialize<OmpRpcResultMessage>(line);
                        Validate(result);
                        break;
                    case "error":
                        var error = Deserialize<OmpRpcErrorMessage>(line);
                        Validate(error);
                        throw new OmpRpcProtocolException(error.Code);
                    default:
                        throw new OmpRpcProtocolException("unknown_message_type");
                }
            }

            await process.WaitForExitAsync(timeout.Token);
            _ = await standardError.WaitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new OmpRpcProtocolException("nonzero_exit");
            return new AgentExecutionResult(
                "omp-rpc",
                result.SessionId,
                result.TurnId,
                result.StructuredOutput,
                chunks,
                result.DurationMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelAndCleanupAsync(process, requestId);
            throw;
        }
        catch (OperationCanceledException)
        {
            await CancelAndCleanupAsync(process, requestId);
            throw new TimeoutException("OMP RPC turn timeout expired.");
        }
        catch
        {
            await CancelAndCleanupAsync(process, requestId);
            throw;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private async Task CancelAndCleanupAsync(Process process, string requestId)
    {
        if (process.HasExited) return;
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(
                new OmpRpcCancelRequest("1.0.0", "cancel", requestId), JsonOptions));
            await process.StandardInput.FlushAsync();
            using var grace = new CancellationTokenSource(_options.CancellationGrace);
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
        }
        catch (IOException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static T Deserialize<T>(string line) where T : class =>
        DeserializeCore<T>(line);

    private static OmpRpcEnvelope DeserializeEnvelope(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<OmpRpcEnvelope>(line, EnvelopeJsonOptions)
                ?? throw new OmpRpcProtocolException("invalid_message_schema");
        }
        catch (JsonException)
        {
            throw new OmpRpcProtocolException("invalid_message_schema");
        }
    }

    private static T DeserializeCore<T>(string line) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, JsonOptions)
                ?? throw new OmpRpcProtocolException("invalid_message_schema");
        }
        catch (JsonException)
        {
            throw new OmpRpcProtocolException("invalid_message_schema");
        }
    }

    private static void ValidateEnvelope(OmpRpcEnvelope value)
    {
        if (value.SchemaVersion != "1.0.0" || string.IsNullOrWhiteSpace(value.Type) ||
            string.IsNullOrWhiteSpace(value.RequestId))
        {
            throw new OmpRpcProtocolException("invalid_message_schema");
        }
    }

    private static void Validate(OmpRpcHeartbeatMessage value)
    {
        ValidateEnvelope(new OmpRpcEnvelope(value.SchemaVersion, value.Type, value.RequestId));
        if (value.Type != "heartbeat" || value.Timestamp == default)
        {
            throw new OmpRpcProtocolException("invalid_message_schema");
        }
    }

    private static void Validate(OmpRpcChunkMessage value)
    {
        ValidateEnvelope(new OmpRpcEnvelope(value.SchemaVersion, value.Type, value.RequestId));
        if (value.Type != "chunk" || value.Text is null)
        {
            throw new OmpRpcProtocolException("invalid_message_schema");
        }
    }

    private static void Validate(OmpRpcResultMessage value)
    {
        ValidateEnvelope(new OmpRpcEnvelope(value.SchemaVersion, value.Type, value.RequestId));
        if (value.Type != "result" || string.IsNullOrWhiteSpace(value.SessionId) ||
            string.IsNullOrWhiteSpace(value.TurnId) || string.IsNullOrWhiteSpace(value.StructuredOutput) ||
            value.DurationMs < 0)
        {
            throw new OmpRpcProtocolException("invalid_message_schema");
        }
    }

    private static void Validate(OmpRpcErrorMessage value)
    {
        ValidateEnvelope(new OmpRpcEnvelope(value.SchemaVersion, value.Type, value.RequestId));
        if (value.Type != "error" || string.IsNullOrWhiteSpace(value.Code) || value.Code.Length is < 1 or > 64 ||
            value.Code.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
        {
            throw new OmpRpcProtocolException("invalid_message_schema");
        }
    }

    private void Validate(AgentExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Instruction);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        if (_options.TurnTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("OMP RPC turn timeout must be positive.");
        if (_options.HeartbeatTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("OMP RPC heartbeat timeout must be positive.");
        if (_options.CancellationGrace <= TimeSpan.Zero)
            throw new InvalidOperationException("OMP RPC cancellation grace must be positive.");
        if (_options.MaxMessageCharacters < 1024)
            throw new InvalidOperationException("OMP RPC message limit must be at least 1024 characters.");
    }

    private static void SanitizeEnvironment(ProcessStartInfo start)
    {
        foreach (var key in start.Environment.Keys.Where(key =>
                     key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                     key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
                     key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
                     key.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }

        start.Environment["HARNESS_EXECUTOR"] = "omp-rpc";
    }
}

public sealed class AgentExecutorUnavailableException(string executor, string reason)
    : Exception($"Agent executor '{executor}' is unavailable: {reason}.");

public sealed class OmpRpcProtocolException(string code) : Exception($"OMP RPC protocol error: {code}.")
{
    public string Code { get; } = code;
}

public sealed record OmpRpcExecuteRequest(
    string SchemaVersion,
    string Type,
    string RequestId,
    string TenantId,
    string ProjectId,
    string AgentId,
    string Instruction,
    string StatusDigestJson,
    string WorkingDirectory,
    string? SessionId,
    string? Model,
    string? Effort);

public sealed record OmpRpcCancelRequest(string SchemaVersion, string Type, string RequestId);

public sealed record OmpRpcEnvelope(string SchemaVersion, string Type, string RequestId);

public sealed record OmpRpcHeartbeatMessage(
    string SchemaVersion,
    string Type,
    string RequestId,
    DateTimeOffset Timestamp);

public sealed record OmpRpcChunkMessage(
    string SchemaVersion,
    string Type,
    string RequestId,
    string Text);

public sealed record OmpRpcResultMessage(
    string SchemaVersion,
    string Type,
    string RequestId,
    string SessionId,
    string TurnId,
    string StructuredOutput,
    long DurationMs);

public sealed record OmpRpcErrorMessage(
    string SchemaVersion,
    string Type,
    string RequestId,
    string Code);
