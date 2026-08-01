using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Execution.Infrastructure.Git;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Agents;

/// <summary>
/// Captura e retomada do trabalho parcial de um card quando a tentativa morre por motivo que não é
/// culpa do trabalho — tipicamente cota esgotada no meio da execução.
///
/// O estado recuperável pertence ao CARD, não à tentativa nem ao ator: a tentativa é descartável e
/// fenced; conta, provider e executor são recursos substituíveis. Enquanto o checkpoint estava
/// amarrado ao ator, o único caso que ele precisava cobrir — trocar de conta porque a cota acabou —
/// era justamente o que a política recusava, e o trabalho parcial era descartado.
///
/// O checkpoint carrega só estado TRANSFERÍVEL: branch, commit, arquivos alterados, escopo,
/// pendências e evidência. Nunca segredo, token, lease ou identidade da conta anterior — o que ele
/// registra dela é proveniência, não autorização.
/// </summary>
public sealed partial class ExecutionCheckpointService(
    IExecutionCheckpointStore checkpoints,
    IClock clock,
    ILogger<ExecutionCheckpointService> logger)
{
    private readonly IExecutionCheckpointStore _checkpoints =
        checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Grava o checkpoint de uma tentativa que não vai terminar. Devolve nulo quando não há nada
    /// transferível — sem trabalho em disco, um checkpoint vazio só criaria a ilusão de retomada.
    /// </summary>
    public async Task<ExecutionCheckpointRecord?> CaptureAsync(
        string tenantId,
        string projectId,
        string taskId,
        string executionId,
        string attemptId,
        string accountAlias,
        string role,
        CheckpointOrigin origin,
        string branchName,
        string repositoryRoot,
        string controlledRoot,
        IReadOnlyList<string> scopeClaims,
        long fencingToken,
        string? progressNote,
        string? worktreePath,
        CancellationToken cancellationToken)
    {
        var sourceCommit = await PreserveWorktreeAsync(
            repositoryRoot, controlledRoot, worktreePath, attemptId, cancellationToken);
        var changed = await ReadChangedFilesAsync(repositoryRoot, controlledRoot, branchName, cancellationToken);
        if (changed.Count == 0 && string.IsNullOrWhiteSpace(progressNote))
        {
            LogNothingToCheckpoint(logger, attemptId, branchName);
            return null;
        }

        var now = _clock.UtcNow;
        var record = await _checkpoints.SaveAsync(
            new ExecutionCheckpointSaveCommand(
                tenantId,
                UlidValue.New(now).ToString(),
                projectId,
                taskId,
                executionId,
                attemptId,
                accountAlias,
                role,
                CheckpointResumePolicy.Serialize(origin),
                branchName,
                SourceCommit: sourceCommit,
                repositoryRoot,
                scopeClaims,
                changed,
                progressNote,
                Pending: [],
                Evidence: sourceCommit is null
                    ? [$"attempt:{attemptId}", $"git-branch:{branchName}"]
                    : [$"attempt:{attemptId}", $"git-branch:{branchName}", $"git-commit:{sourceCommit}"],
                fencingToken,
                now),
            cancellationToken);
        LogCheckpointCaptured(logger, taskId, attemptId, record.Origin, changed.Count);
        return record;
    }

    /// <summary>
    /// Antes de projetar a lista de arquivos, transforma alterações rastreadas e não rastreadas da
    /// worktree órfã em um commit de colheita. Sem isso a branch apontava para a base, o checkpoint
    /// dizia "0 arquivos" e o cleanup apagava o único lugar onde o trabalho parcial existia.
    /// </summary>
    private static async Task<string?> PreserveWorktreeAsync(
        string repositoryRoot,
        string controlledRoot,
        string? worktreePath,
        string attemptId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return null;
        }

        try
        {
            using var manager = await GitWorktreeManager.OpenAsync(
                Path.GetFullPath(repositoryRoot), Path.GetFullPath(controlledRoot), cancellationToken);
            return await manager.CommitWorktreeLeftoversAsync(
                Path.GetFullPath(worktreePath),
                $"chore(harness): checkpoint da tentativa {attemptId}",
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// O checkpoint disponível do card, quando a conta candidata pode retomá-lo. Devolve nulo
    /// quando não há checkpoint ou quando a política recusa — e nesse caso a nova tentativa começa
    /// do zero, o que é honesto: melhor recomeçar do que afirmar continuidade que não existe.
    /// </summary>
    public async Task<(ExecutionCheckpointRecord Checkpoint, CheckpointResumeVerdict Verdict)?> TryResumeAsync(
        string tenantId,
        string taskId,
        string candidateRole,
        string candidateAccount,
        bool executorChanged,
        CancellationToken cancellationToken)
    {
        var available = await _checkpoints.GetAvailableAsync(tenantId, taskId, cancellationToken);
        if (available is null)
        {
            return null;
        }

        var verdict = CheckpointResumePolicy.Evaluate(
            CheckpointResumePolicy.ParseOrigin(available.Origin),
            available.SourceRole,
            candidateRole,
            available.SourceAccountAlias,
            candidateAccount,
            executorChanged);
        if (!verdict.Allowed)
        {
            LogResumeRefused(logger, taskId, available.CheckpointId, verdict.ReasonCode);
            return null;
        }

        return (available, verdict);
    }

    /// <summary>Marca o checkpoint como consumido pela nova tentativa. Idempotente.</summary>
    public Task<bool> ConsumeAsync(
        string tenantId, string checkpointId, string attemptId, CancellationToken cancellationToken) =>
        _checkpoints.ConsumeAsync(tenantId, checkpointId, attemptId, _clock.UtcNow, cancellationToken);

    /// <summary>
    /// Arquivos que a branch da tentativa alterou. É o que torna o checkpoint transferível: o
    /// trabalho vive no Git, não na sessão do agente que o produziu.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadChangedFilesAsync(
        string repositoryRoot, string controlledRoot, string branchName, CancellationToken cancellationToken)
    {
        try
        {
            using var manager = await GitWorktreeManager.OpenAsync(
                Path.GetFullPath(repositoryRoot), Path.GetFullPath(controlledRoot), cancellationToken);
            return await manager.ListBranchChangedFilesAsync(branchName, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Repositório indisponível não pode derrubar a captura: sem a lista, o checkpoint
            // ainda vale pelo resumo e pela branch.
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Checkpoint do card {TaskId} gravado da tentativa {AttemptId} ({Origin}, {FileCount} arquivo(s)).")]
    private static partial void LogCheckpointCaptured(
        ILogger logger, string taskId, string attemptId, string origin, int fileCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Nada transferível na tentativa {AttemptId} (branch {Branch}); nenhum checkpoint gravado.")]
    private static partial void LogNothingToCheckpoint(ILogger logger, string attemptId, string branch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retomada do checkpoint {CheckpointId} do card {TaskId} recusada: {ReasonCode}.")]
    private static partial void LogResumeRefused(
        ILogger logger, string taskId, string checkpointId, string reasonCode);
}
