using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace Harness.Modules.Agents.Infrastructure.CodexCli;

public sealed class CodexCliAppServer : IAsyncDisposable
{
    private const int MaximumCapturedErrorLines = 50;
    private readonly CodexCliAppServerOptions _options;
    private readonly Process _process;
    private readonly Func<CodexCliHeartbeat, CancellationToken, ValueTask>? _heartbeatSink;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, PendingTurn> _turnsByThread = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _standardInputLock = new(1, 1);
    private readonly object _errorLock = new();
    private readonly Queue<string> _standardError = new();
    private readonly Task _standardOutputReader;
    private readonly Task _standardErrorReader;
    private readonly Task _heartbeatWorker;
    private long _requestSequence;
    private int _stopping;
    private int _disposed;

    private CodexCliAppServer(
        CodexCliAppServerOptions options,
        Process process,
        Func<CodexCliHeartbeat, CancellationToken, ValueTask>? heartbeatSink)
    {
        _options = options;
        _process = process;
        _heartbeatSink = heartbeatSink;
        _standardOutputReader = ReadStandardOutputAsync();
        _standardErrorReader = ReadStandardErrorAsync();
        _heartbeatWorker = EmitHeartbeatsAsync();
    }

    public int ProcessId => _process.Id;

    public bool IsRunning => !_process.HasExited;

    public string CapturedStandardError
    {
        get
        {
            lock (_errorLock)
            {
                return string.Join(Environment.NewLine, _standardError);
            }
        }
    }

    public static async Task<CodexCliAppServer> StartAsync(
        CodexCliAppServerOptions options,
        Func<CodexCliHeartbeat, CancellationToken, ValueTask>? heartbeatSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.WorkingDirectory);
        Directory.CreateDirectory(options.StateDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in options.ExecutablePrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");
        startInfo.ArgumentList.Add("--strict-config");

        SanitizeEnvironment(startInfo, options.StateDirectory);

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new CodexAppServerException("Codex CLI app-server did not start.");
        }

        var server = new CodexCliAppServer(options, process, heartbeatSink);
        try
        {
            await server.InitializeAsync(cancellationToken);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public async Task<CodexThreadSession> StartThreadAsync(
        bool ephemeral,
        string? developerInstructions = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(
            "thread/start",
            new
            {
                cwd = _options.AgentWorkingDirectory,
                ephemeral,
                developerInstructions,
                approvalPolicy = "never",
                sandbox = "read-only",
            },
            cancellationToken);

        return ReadThread(response);
    }

    public async Task<CodexThreadSession> ResumeThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var response = await SendRequestAsync(
            "thread/resume",
            new
            {
                threadId,
                cwd = _options.AgentWorkingDirectory,
                approvalPolicy = "never",
                sandbox = "read-only",
            },
            cancellationToken);

        return ReadThread(response);
    }

    public async Task InjectSessionMarkerAsync(
        string threadId,
        string marker,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);

        await SendRequestAsync(
            "thread/inject_items",
            new
            {
                threadId,
                items = new[]
                {
                    new
                    {
                        type = "message",
                        role = "user",
                        content = new[]
                        {
                            new { type = "input_text", text = marker },
                        },
                    },
                },
            },
            cancellationToken);
    }

    public async Task<CodexTurnResult> RunTurnAsync(
        string threadId,
        string instruction,
        JsonElement outputSchema,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        var pendingTurn = new PendingTurn(threadId);
        if (!_turnsByThread.TryAdd(threadId, pendingTurn))
        {
            throw new InvalidOperationException("A thread cannot execute more than one turn concurrently.");
        }

        try
        {
            var response = await SendRequestAsync(
                "turn/start",
                new
                {
                    threadId,
                    input = new[] { new { type = "text", text = instruction } },
                    outputSchema,
                    approvalPolicy = "never",
                    sandboxPolicy = new { type = "externalSandbox", networkAccess = "restricted" },
                },
                cancellationToken);
            pendingTurn.SetTurnId(ReadTurnId(response));
            return await pendingTurn.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _turnsByThread.TryRemove(threadId, out _);
        }
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0 && !_process.HasExited)
        {
            _process.StandardInput.Close();
            using var gracefulTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            gracefulTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await _process.WaitForExitAsync(gracefulTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }

        if (!_process.HasExited)
        {
            await _process.WaitForExitAsync(cancellationToken);
        }

        _lifetime.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await InterruptAsync();
        await AwaitWorkerShutdownAsync(_standardOutputReader);
        await AwaitWorkerShutdownAsync(_standardErrorReader);
        await AwaitWorkerShutdownAsync(_heartbeatWorker);

        _standardInputLock.Dispose();
        _lifetime.Dispose();
        _process.Dispose();
    }

    private static void SanitizeEnvironment(ProcessStartInfo startInfo, string stateDirectory)
    {
        var inheritedPath = startInfo.Environment.TryGetValue("PATH", out var path) ? path : null;
        var inheritedLanguage = startInfo.Environment.TryGetValue("LANG", out var language) ? language : null;

        startInfo.Environment.Clear();
        if (!string.IsNullOrWhiteSpace(inheritedPath))
        {
            startInfo.Environment["PATH"] = inheritedPath;
        }

        if (!string.IsNullOrWhiteSpace(inheritedLanguage))
        {
            startInfo.Environment["LANG"] = inheritedLanguage;
        }

        // CODEX_HOME is used only for its documented purpose: isolating Codex config,
        // session rollouts, logs and auth from the invoking user's personal state.
        startInfo.Environment["CODEX_HOME"] = stateDirectory;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            "initialize",
            new
            {
                clientInfo = new
                {
                    name = "harness_poseidon",
                    title = "Harness",
                    version = "0.1.0",
                },
            },
            cancellationToken);

        await SendNotificationAsync("initialized", new { }, cancellationToken);
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_process.HasExited)
        {
            throw CreateExitedException();
        }

        var id = Interlocked.Increment(ref _requestSequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("A duplicate app-server request identifier was generated.");
        }

        try
        {
            await WriteMessageAsync(new { method, id, @params = parameters }, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken) =>
        WriteMessageAsync(new { method, @params = parameters }, cancellationToken);

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(message);
        await _standardInputLock.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _standardInputLock.Release();
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A transport reader failure must be transferred to every pending JSON-RPC request.")]
    private async Task ReadStandardOutputAsync()
    {
        Exception? transportFailure = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_lifetime.Token);
                if (line is null)
                {
                    break;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out _) &&
                    root.TryGetProperty("method", out var methodElement) &&
                    root.TryGetProperty("params", out var parameters))
                {
                    HandleNotification(methodElement.GetString(), parameters);
                    continue;
                }
                if (!root.TryGetProperty("id", out var idElement) ||
                    !idElement.TryGetInt64(out var id) ||
                    !_pending.TryGetValue(id, out var completion))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : "Codex app-server returned an unspecified error.";
                    completion.TrySetException(new CodexAppServerException(message ?? "Codex app-server request failed."));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    completion.TrySetResult(result.Clone());
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            transportFailure = exception;
        }
        finally
        {
            if (Volatile.Read(ref _stopping) == 0)
            {
                var exception = transportFailure is null
                    ? CreateExitedException()
                    : new CodexAppServerException("Codex app-server transport failed.", transportFailure);
                foreach (var completion in _pending.Values)
                {
                    completion.TrySetException(exception);
                }
                foreach (var turn in _turnsByThread.Values)
                {
                    turn.Completion.TrySetException(exception);
                }
            }
        }
    }

    private async Task ReadStandardErrorAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(_lifetime.Token);
                if (line is null)
                {
                    break;
                }

                lock (_errorLock)
                {
                    if (_standardError.Count == MaximumCapturedErrorLines)
                    {
                        _standardError.Dequeue();
                    }

                    _standardError.Enqueue(line);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task EmitHeartbeatsAsync()
    {
        if (_heartbeatSink is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        var sequence = 0L;
        while (await timer.WaitForNextTickAsync(_lifetime.Token) && !_process.HasExited)
        {
            var heartbeat = new CodexCliHeartbeat(
                _process.Id,
                Interlocked.Increment(ref sequence),
                DateTimeOffset.UtcNow);
            await _heartbeatSink(heartbeat, _lifetime.Token);
        }
    }

    private static CodexThreadSession ReadThread(JsonElement response)
    {
        if (!response.TryGetProperty("thread", out var thread) ||
            !thread.TryGetProperty("id", out var idElement))
        {
            throw new CodexAppServerException("Codex app-server response did not contain a thread identifier.");
        }

        var threadId = idElement.GetString();
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new CodexAppServerException("Codex app-server returned an empty thread identifier.");
        }

        var ephemeral = thread.TryGetProperty("ephemeral", out var ephemeralElement) &&
            ephemeralElement.ValueKind is JsonValueKind.True;
        return new CodexThreadSession(threadId, ephemeral);
    }

    private static string ReadTurnId(JsonElement response)
    {
        if (!response.TryGetProperty("turn", out var turn) ||
            !turn.TryGetProperty("id", out var idElement) ||
            string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            throw new CodexAppServerException("Codex app-server response did not contain a turn identifier.");
        }

        return idElement.GetString()!;
    }

    private void HandleNotification(string? method, JsonElement parameters)
    {
        if (method is null ||
            !parameters.TryGetProperty("threadId", out var threadElement) ||
            string.IsNullOrWhiteSpace(threadElement.GetString()) ||
            !_turnsByThread.TryGetValue(threadElement.GetString()!, out var pendingTurn))
        {
            return;
        }

        if (method == "item/agentMessage/delta" &&
            parameters.TryGetProperty("delta", out var deltaElement) &&
            deltaElement.ValueKind == JsonValueKind.String)
        {
            pendingTurn.AddDelta(deltaElement.GetString() ?? string.Empty);
            return;
        }

        if (method == "item/completed" &&
            parameters.TryGetProperty("item", out var item) &&
            item.TryGetProperty("type", out var itemType) &&
            itemType.GetString() == "agentMessage" &&
            item.TryGetProperty("text", out var textElement))
        {
            pendingTurn.SetFinalMessage(textElement.GetString() ?? string.Empty);
            return;
        }

        if (method == "turn/completed" && parameters.TryGetProperty("turn", out var turn))
        {
            pendingTurn.Complete(turn);
        }
    }

    private CodexAppServerException CreateExitedException()
    {
        var exitCode = _process.HasExited
            ? _process.ExitCode.ToString(CultureInfo.InvariantCulture)
            : "unknown";
        var detail = CapturedStandardError;
        var message = string.IsNullOrWhiteSpace(detail)
            ? $"Codex app-server exited unexpectedly with code {exitCode}."
            : $"Codex app-server exited unexpectedly with code {exitCode}: {detail}";
        return new CodexAppServerException(message);
    }

    private static async Task AwaitWorkerShutdownAsync(Task worker)
    {
        try
        {
            await worker;
        }
        catch (OperationCanceledException)
        {
            // Expected when the supervised process is interrupted.
        }
    }

    private sealed class PendingTurn(string threadId)
    {
        private readonly object _sync = new();
        private readonly List<string> _deltas = [];
        private readonly long _started = Stopwatch.GetTimestamp();
        private string? _turnId;
        private string? _finalMessage;

        public TaskCompletionSource<CodexTurnResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetTurnId(string turnId)
        {
            lock (_sync)
            {
                _turnId = turnId;
            }
        }

        public void AddDelta(string delta)
        {
            if (delta.Length == 0) return;
            lock (_sync)
            {
                _deltas.Add(delta);
            }
        }

        public void SetFinalMessage(string message)
        {
            lock (_sync)
            {
                _finalMessage = message;
            }
        }

        public void Complete(JsonElement turn)
        {
            lock (_sync)
            {
                var turnId = turn.TryGetProperty("id", out var idElement)
                    ? idElement.GetString()
                    : _turnId;
                var status = turn.TryGetProperty("status", out var statusElement)
                    ? statusElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(turnId) || string.IsNullOrWhiteSpace(status))
                {
                    Completion.TrySetException(new CodexAppServerException(
                        "Codex turn completion did not contain an identifier and status."));
                    return;
                }

                if (!string.Equals(status, "completed", StringComparison.Ordinal))
                {
                    Completion.TrySetException(new CodexAppServerException(
                        $"Codex turn {turnId} finished with status {status}."));
                    return;
                }

                var finalMessage = _finalMessage ?? string.Concat(_deltas);
                if (string.IsNullOrWhiteSpace(finalMessage))
                {
                    Completion.TrySetException(new CodexAppServerException(
                        $"Codex turn {turnId} completed without an agent message."));
                    return;
                }

                Completion.TrySetResult(new CodexTurnResult(
                    threadId,
                    turnId,
                    status,
                    finalMessage,
                    _deltas.ToArray(),
                    (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds));
            }
        }
    }
}
