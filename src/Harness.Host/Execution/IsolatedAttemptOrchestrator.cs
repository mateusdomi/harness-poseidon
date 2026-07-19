using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Execution.Application.Sandbox;
using Harness.Modules.Execution.Infrastructure.Git;
using Harness.Persistence.Abstractions.AttemptWorkspaces;
using Harness.SharedKernel.Time;

namespace Harness.Host.Execution;

public sealed class IsolatedAttemptOrchestrator(
    IAttemptWorkspaceStore store,
    ISandboxProvider sandboxProvider,
    ISandboxAgentExecutorFactory executorFactory,
    IClock clock,
    IsolatedExecutionOptions options)
{
    private readonly IAttemptWorkspaceStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly ISandboxProvider _sandboxProvider =
        sandboxProvider ?? throw new ArgumentNullException(nameof(sandboxProvider));
    private readonly ISandboxAgentExecutorFactory _executorFactory =
        executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IsolatedExecutionOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async Task<IsolatedExecutionResult> ExecuteAsync(
        StartIsolatedExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var acquired = await _store.AcquireAsync(
            new AttemptWorkspaceAcquireCommand
            {
                TenantId = command.TenantId,
                ProjectId = command.ProjectId,
                TaskId = command.TaskId,
                AttemptId = command.AttemptId,
                RepositoryRoot = command.RepositoryRoot,
                ControlledRoot = command.ControlledRoot,
                BaseReference = command.BaseReference,
                BranchName = command.BranchName,
                WorktreePath = command.WorktreePath,
                ScopeClaims = command.ScopeClaims,
                Owner = command.Owner,
                LeaseDuration = command.LeaseDuration,
                IdempotencyKey = command.IdempotencyKey,
                OccurredAt = _clock.UtcNow,
            },
            cancellationToken);
        if (acquired.Status == AttemptWorkspaceMutationStatus.ScopeConflict)
        {
            return new IsolatedExecutionResult(
                IsolatedExecutionStatus.ScopeConflict,
                null,
                acquired.Conflicts,
                null,
                null);
        }

        if (!acquired.Succeeded)
        {
            return new IsolatedExecutionResult(
                IsolatedExecutionStatus.Rejected,
                acquired.Workspace,
                [],
                null,
                null);
        }

        var workspace = acquired.Workspace
            ?? throw new InvalidOperationException("An acquired attempt workspace requires a snapshot.");
        if (AttemptWorkspaceLifecycle.IsTerminal(workspace.State))
        {
            if (workspace.CleanupState == AttemptWorkspaceCleanupState.Pending)
            {
                var reclaimedTerminal = await EnsureLeaseAsync(command, workspace, cancellationToken);
                if (reclaimedTerminal is null)
                {
                    return new IsolatedExecutionResult(
                        IsolatedExecutionStatus.Rejected,
                        workspace,
                        [],
                        null,
                        workspace.FinalError);
                }

                var terminalManager = await GitWorktreeManager.OpenAsync(
                    workspace.RepositoryRoot,
                    workspace.ControlledRoot,
                    cancellationToken);
                workspace = await TryCleanupAndReleaseAsync(
                    command,
                    reclaimedTerminal,
                    terminalManager,
                    SandboxAttemptLabel(workspace.AttemptId)) ?? reclaimedTerminal;
            }

            return TerminalResult(workspace);
        }

        var ensured = await EnsureLeaseAsync(command, workspace, cancellationToken);
        if (ensured is null)
        {
            return new IsolatedExecutionResult(
                IsolatedExecutionStatus.Rejected,
                workspace,
                [],
                null,
                null);
        }

        workspace = ensured;
        var sandboxLabel = SandboxAttemptLabel(workspace.AttemptId);
        GitWorktreeManager? manager = null;
        try
        {
            manager = await GitWorktreeManager.OpenAsync(
                workspace.RepositoryRoot,
                workspace.ControlledRoot,
                cancellationToken);
            if (workspace.State == AttemptWorkspaceState.Claimed)
            {
                var descriptor = await manager.CreateTaskWorktreeAsync(
                    workspace.BranchName,
                    workspace.AttemptId,
                    workspace.WorktreePath,
                    workspace.BaseReference,
                    cancellationToken);
                workspace = await TransitionAsync(
                    command,
                    workspace,
                    AttemptWorkspaceState.Prepared,
                    commitSha: descriptor.HeadCommit,
                    cancellationToken: cancellationToken);
            }

            AgentExecutionResult execution;
            await using (var session = await _sandboxProvider.OpenProcessSessionAsync(
                new SandboxProcessRequest(
                    sandboxLabel,
                    workspace.ControlledRoot,
                    workspace.WorktreePath,
                    _options.AgentImageName,
                    _options.ProxyImageName,
                    _options.ProxyCommand,
                    _options.ContainerExecutable,
                    _options.CpuLimit,
                    _options.MemoryBytes,
                    _options.WritableDiskBytes,
                    _options.PidsLimit),
                cancellationToken))
            {
                if (workspace.State == AttemptWorkspaceState.Prepared)
                {
                    workspace = await TransitionAsync(
                        command,
                        workspace,
                        AttemptWorkspaceState.Running,
                        sessionId: $"sandbox:{sandboxLabel}",
                        cancellationToken: cancellationToken);
                }

                using var executionCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var heartbeats = HeartbeatLoopAsync(command, workspace.FencingToken, executionCts);
                try
                {
                    var executor = _executorFactory.Create(session.ProcessPlan, command);
                    execution = await executor.ExecuteAsync(
                        new AgentExecutionRequest(
                            command.TenantId,
                            command.ProjectId,
                            command.ConversationId,
                            command.AgentId,
                            command.Instruction,
                            command.StatusDigestJson,
                            workspace.WorktreePath),
                        executionCts.Token);
                }
                finally
                {
                    await executionCts.CancelAsync();
                    await heartbeats;
                }
            }

            workspace = await TransitionAsync(
                command,
                workspace,
                AttemptWorkspaceState.Completed,
                sessionId: execution.SessionId,
                cancellationToken: cancellationToken);
            workspace = await TryCleanupAndReleaseAsync(
                command,
                workspace,
                manager,
                sandboxLabel) ?? workspace;
            return new IsolatedExecutionResult(
                IsolatedExecutionStatus.Completed,
                workspace,
                [],
                execution,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var finalError = AttemptWorkspaceErrorSanitizer.Sanitize(
                $"{exception.GetType().Name}: {exception.Message}");
            var failed = await TryFailAsync(command, finalError);
            if (failed is not null && manager is not null)
            {
                failed = await TryCleanupAndReleaseAsync(command, failed, manager, sandboxLabel);
            }

            return new IsolatedExecutionResult(
                IsolatedExecutionStatus.Failed,
                failed,
                [],
                null,
                finalError);
        }
    }

    public static string SandboxAttemptLabel(string attemptId) =>
        $"wsp-{attemptId[^20..].ToLowerInvariant()}";

    private static IsolatedExecutionResult TerminalResult(AttemptWorkspaceSnapshot workspace) =>
        new(
            workspace.State == AttemptWorkspaceState.Completed
                ? IsolatedExecutionStatus.Completed
                : IsolatedExecutionStatus.Failed,
            workspace,
            [],
            null,
            workspace.FinalError);

    private async Task<AttemptWorkspaceSnapshot?> EnsureLeaseAsync(
        StartIsolatedExecutionCommand command,
        AttemptWorkspaceSnapshot workspace,
        CancellationToken cancellationToken)
    {
        if (OwnsLease(workspace, command.Owner) && workspace.LeaseExpiresAt > _clock.UtcNow)
        {
            return workspace;
        }

        var reclaimed = await _store.ReclaimExpiredAsync(
            new AttemptWorkspaceReclaimCommand(
                command.TenantId,
                command.AttemptId,
                command.Owner,
                command.LeaseDuration,
                _clock.UtcNow),
            cancellationToken);
        return reclaimed.Succeeded ? reclaimed.Workspace : null;
    }

    private async Task<AttemptWorkspaceSnapshot> TransitionAsync(
        StartIsolatedExecutionCommand command,
        AttemptWorkspaceSnapshot workspace,
        AttemptWorkspaceState state,
        string? commitSha = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var receipt = await _store.TransitionAsync(
            new AttemptWorkspaceTransitionCommand
            {
                TenantId = command.TenantId,
                AttemptId = command.AttemptId,
                Owner = command.Owner,
                FencingToken = workspace.FencingToken,
                ExpectedState = workspace.State,
                State = state,
                CommitSha = commitSha,
                SessionId = sessionId,
                OccurredAt = _clock.UtcNow,
            },
            cancellationToken);
        return receipt.Succeeded && receipt.Workspace is not null
            ? receipt.Workspace
            : throw new InvalidOperationException(
                $"The workspace refused the {state} transition with {receipt.Status}.");
    }

    private async Task<AttemptWorkspaceSnapshot> CleanupAndReleaseAsync(
        StartIsolatedExecutionCommand command,
        AttemptWorkspaceSnapshot workspace,
        GitWorktreeManager manager,
        string sandboxLabel,
        CancellationToken cancellationToken)
    {
        await _sandboxProvider.CleanupAsync(sandboxLabel, cancellationToken);
        await manager.RemoveTaskWorktreeAsync(
            workspace.BranchName,
            workspace.WorktreePath,
            deleteBranch: false,
            cancellationToken);
        var released = await _store.ReleaseAsync(
            new AttemptWorkspaceReleaseCommand(
                command.TenantId,
                command.AttemptId,
                command.Owner,
                workspace.FencingToken,
                _clock.UtcNow),
            cancellationToken);
        return released.Succeeded && released.Workspace is not null
            ? released.Workspace
            : throw new InvalidOperationException(
                $"The workspace refused the claim release with {released.Status}.");
    }

    private async Task<AttemptWorkspaceSnapshot?> TryFailAsync(
        StartIsolatedExecutionCommand command,
        string finalError)
    {
        try
        {
            var current = await _store.GetAsync(command.TenantId, command.AttemptId, CancellationToken.None);
            if (current is null ||
                !OwnsLease(current, command.Owner) ||
                AttemptWorkspaceLifecycle.IsTerminal(current.State))
            {
                return current;
            }

            var receipt = await _store.TransitionAsync(
                new AttemptWorkspaceTransitionCommand
                {
                    TenantId = command.TenantId,
                    AttemptId = command.AttemptId,
                    Owner = current.Owner,
                    FencingToken = current.FencingToken,
                    ExpectedState = current.State,
                    State = AttemptWorkspaceState.Failed,
                    FinalError = finalError,
                    OccurredAt = _clock.UtcNow,
                },
                CancellationToken.None);
            return receipt.Workspace ?? current;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<AttemptWorkspaceSnapshot?> TryCleanupAndReleaseAsync(
        StartIsolatedExecutionCommand command,
        AttemptWorkspaceSnapshot workspace,
        GitWorktreeManager manager,
        string sandboxLabel)
    {
        try
        {
            return await CleanupAndReleaseAsync(
                command,
                workspace,
                manager,
                sandboxLabel,
                CancellationToken.None);
        }
        catch (Exception)
        {
            try
            {
                return await _store.GetAsync(command.TenantId, command.AttemptId, CancellationToken.None)
                    ?? workspace;
            }
            catch (Exception)
            {
                return workspace;
            }
        }
    }

    private async Task HeartbeatLoopAsync(
        StartIsolatedExecutionCommand command,
        long fencingToken,
        CancellationTokenSource executionCts)
    {
        try
        {
            while (!executionCts.Token.IsCancellationRequested)
            {
                await Task.Delay(_options.HeartbeatInterval, executionCts.Token);
                var receipt = await _store.HeartbeatAsync(
                    new AttemptWorkspaceLeaseCommand(
                        command.TenantId,
                        command.AttemptId,
                        command.Owner,
                        fencingToken,
                        command.LeaseDuration,
                        _clock.UtcNow),
                    CancellationToken.None);
                if (!receipt.Succeeded)
                {
                    await executionCts.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool OwnsLease(AttemptWorkspaceSnapshot workspace, string owner) =>
        string.Equals(workspace.Owner, owner, StringComparison.Ordinal);
}
