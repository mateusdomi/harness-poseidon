using System.Collections.Concurrent;
using System.Diagnostics;
using Harness.Persistence.Abstractions.RunTargets;
using Harness.SharedKernel.Time;

namespace Harness.Host.RunTargets;

public sealed class RunTargetProcessSupervisor(
    IRunTargetStore store,
    IClock clock,
    ILogger<RunTargetProcessSupervisor> logger) : IHostedService
{
    private static readonly Action<ILogger, string, Exception?> StopFailure =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4101, nameof(StopFailure)), "Failed to stop managed run target {TargetId} during shutdown.");
    private static readonly Action<ILogger, string, Exception?> ObservationFailure =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4102, nameof(ObservationFailure)), "Failed while observing run target {TargetId}.");
    private static readonly Action<ILogger, string, Exception?> LogFailure =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4103, nameof(LogFailure)), "Failed to persist run log for {TargetId}.");
    private readonly ConcurrentDictionary<string, ManagedProcess> _processes = new(StringComparer.Ordinal);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public bool IsRunning(string targetId) =>
        _processes.TryGetValue(targetId, out var managed) && !managed.Process.HasExited;

    public async Task<int> StartTargetAsync(
        string tenantId,
        string actorProfileId,
        RunTargetLaunchRecord launch,
        CancellationToken cancellationToken)
    {
        if (_processes.TryGetValue(launch.Target.Id, out var existing) && !existing.Process.HasExited)
            return existing.Process.Id;

        var start = new ProcessStartInfo
        {
            FileName = launch.Executable,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        foreach (var item in launch.Environment) start.Environment[item.Key] = item.Value;
        start.Environment["NO_COLOR"] = "1";

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var managed = new ManagedProcess(tenantId, actorProfileId, launch.Target.ProjectId, launch.Target.Id, process);
        process.OutputDataReceived += (_, args) => QueueLog(managed, args.Data);
        process.ErrorDataReceived += (_, args) => QueueLog(managed, args.Data);
        try
        {
            if (!process.Start()) throw new RunTargetValidationException("The target process could not be started.");
            if (!_processes.TryAdd(launch.Target.Id, managed))
            {
                process.Kill(entireProcessTree: true);
                throw new RunTargetValidationException("The target is already being started.");
            }
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            _ = ObserveExitAsync(managed);
            await Task.Delay(100, cancellationToken);
            if (process.HasExited)
            {
                _processes.TryRemove(launch.Target.Id, out _);
                throw new RunTargetValidationException($"The target exited during startup with code {process.ExitCode}.");
            }
            return process.Id;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public async Task<bool> StopTargetAsync(string targetId, CancellationToken cancellationToken)
    {
        if (!_processes.TryRemove(targetId, out var managed)) return false;
        await StopManagedAsync(managed, cancellationToken);
        return true;
    }

    public async Task<int> StopProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        var targets = _processes.Values.Where(value => value.ProjectId == projectId).ToArray();
        var stopped = 0;
        foreach (var managed in targets)
        {
            if (_processes.TryRemove(managed.TargetId, out _))
            {
                await StopManagedAsync(managed, cancellationToken);
                stopped++;
            }
        }
        return stopped;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var values = _processes.Values.ToArray(); _processes.Clear();
        foreach (var managed in values)
        {
            try
            {
                await StopManagedAsync(managed, cancellationToken);
                await store.SetStateAsync(new(managed.TenantId, managed.ActorProfileId, managed.TargetId, "stopped", "Host shutdown stopped the managed service.", clock.UtcNow), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StopFailure(logger, managed.TargetId, exception);
            }
        }
    }

    private async Task ObserveExitAsync(ManagedProcess managed)
    {
        var ownsDisposal = false;
        try
        {
            await managed.Process.WaitForExitAsync();
            if (_processes.TryRemove(new KeyValuePair<string, ManagedProcess>(managed.TargetId, managed)))
            {
                ownsDisposal = true;
                await store.SetStateAsync(new(managed.TenantId, managed.ActorProfileId, managed.TargetId, "stopped", $"Managed service exited with code {managed.Process.ExitCode}.", clock.UtcNow));
            }
        }
        catch (Exception exception)
        {
            ObservationFailure(logger, managed.TargetId, exception);
        }
        finally
        {
            // Removing the exact dictionary entry transfers lifecycle ownership. When an API stop,
            // project cleanup, or Host shutdown removed it first, that path owns WaitForExit and
            // disposal; disposing here would race its WaitForExitAsync and detach the process.
            if (ownsDisposal)
            {
                managed.Process.Dispose();
            }
        }
    }

    private static async Task StopManagedAsync(ManagedProcess managed, CancellationToken cancellationToken)
    {
        if (!managed.Process.HasExited)
        {
            managed.Process.Kill(entireProcessTree: true);
            await managed.Process.WaitForExitAsync(cancellationToken);
        }
        managed.Process.Dispose();
    }

    private void QueueLog(ManagedProcess managed, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _ = AppendLogSafeAsync(managed, line);
    }

    private async Task AppendLogSafeAsync(ManagedProcess managed, string line)
    {
        try { await store.AppendLogAsync(new(managed.TenantId, managed.ProjectId, line, clock.UtcNow)); }
        catch (Exception exception) { LogFailure(logger, managed.TargetId, exception); }
    }

    private sealed record ManagedProcess(
        string TenantId,
        string ActorProfileId,
        string ProjectId,
        string TargetId,
        Process Process);
}
