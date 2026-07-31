using Harness.Modules.Execution.Infrastructure.Git;
using Harness.Host.Agents;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Time;

namespace Harness.Host.WorkBoard;

public sealed record MergeReconciliationOptions(TimeSpan PollInterval, int PageSize, string Owner)
{
    public void Validate()
    {
        if (PollInterval < TimeSpan.FromSeconds(10) || PollInterval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        }

        if (PageSize is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Owner);
    }
}

public sealed record MergeReconciliationResult(int Scanned, int Settled, int Reopened, int Blocked);

/// <summary>
/// Fase 0C2 (BR-003): a reconciliação explícita entre Git e banco.
///
/// Não se tenta uma transação distribuída — ela não existe entre um repositório e um SQL. O que
/// existe é a INTENÇÃO registrada antes do efeito, e este serviço é quem a lê para descobrir o que
/// realmente aconteceu:
///
/// * intent pendente sem efeito Git → volta a ficar disponível para merge;
/// * merge concluído sem confirmação no banco → o SHA existe no repositório, então o lado factual
///   é fechado aqui em vez de o card ficar `approved` para sempre com o código já integrado;
/// * banco atualizado sem SHA encontrado → o commit não existe: a intenção é reaberta, porque
///   afirmar integração sem commit é pior do que refazer o merge;
/// * lease expirado, processo encerrado no meio, retry do próprio reconciliador → convergem.
/// </summary>
public sealed class MergeReconciliationBackgroundService : BackgroundService
{
    private static readonly Action<ILogger, string, string, Exception?> Reconciled =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2130, nameof(Reconciled)),
            "Merge intent {MergeIntentId} reconciled as {Outcome}.");

    private static readonly Action<ILogger, string, Exception?> CycleFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2131, nameof(CycleFailure)),
            "Merge reconciliation cycle failed with {ExceptionType}.");

    private readonly IMergeIntentStore _intents;
    private readonly IWorkChainStore _chain;
    private readonly IWorkBoardStore _board;
    private readonly IProjectStore _projects;
    private readonly AgentRunSettings _settings;
    private readonly IClock _clock;
    private readonly MergeReconciliationOptions _options;
    private readonly ILogger<MergeReconciliationBackgroundService> _logger;

    public MergeReconciliationBackgroundService(
        IMergeIntentStore intents,
        IWorkChainStore chain,
        IWorkBoardStore board,
        IProjectStore projects,
        AgentRunSettings settings,
        IClock clock,
        MergeReconciliationOptions options,
        ILogger<MergeReconciliationBackgroundService> logger)
    {
        _intents = intents ?? throw new ArgumentNullException(nameof(intents));
        _chain = chain ?? throw new ArgumentNullException(nameof(chain));
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options.Validate();
    }

    public async Task<MergeReconciliationResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var scanned = 0;
        var settled = 0;
        var reopened = 0;
        var blocked = 0;
        foreach (var intent in await _intents.ListUnsettledAsync(_options.PageSize, cancellationToken))
        {
            scanned++;
            // Um intent problemático não pode parar a varredura: os outros repositórios continuam
            // precisando de reconciliação.
            try
            {
                var outcome = await ReconcileAsync(intent, cancellationToken);
                switch (outcome)
                {
                    case "settled":
                        settled++;
                        break;
                    case "reopened":
                        reopened++;
                        break;
                    default:
                        blocked++;
                        break;
                }

                Reconciled(_logger, intent.MergeIntentId, outcome, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                blocked++;
                CycleFailure(_logger, exception.GetType().Name, exception);
            }
        }

        return new MergeReconciliationResult(scanned, settled, reopened, blocked);
    }

    private async Task<string> ReconcileAsync(
        MergeIntentRecord intent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ControlledRoot))
        {
            return "blocked";
        }

        var project = await _projects.GetAsync(intent.TenantId, intent.ProjectId, cancellationToken);
        if (project is null || string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            return "blocked";
        }

        using var manager = await GitWorktreeManager.OpenAsync(
            Path.GetFullPath(project.RepositoryUrl),
            Path.GetFullPath(_settings.ControlledRoot),
            cancellationToken);

        // A pergunta que só o repositório responde: o commit existe?
        var mergedInGit = intent.ResultSha is { Length: > 0 } sha && await ExistsAsync(manager, sha, cancellationToken);

        if (string.Equals(intent.State, MergeIntentState.Merged, StringComparison.Ordinal))
        {
            if (!mergedInGit)
            {
                // Banco diz integrado e o commit NÃO existe. Afirmar integração sem commit é pior
                // do que refazer: a intenção volta a ficar disponível.
                await _intents.TryFailAsync(
                    new MergeIntentFailCommand(
                        intent.TenantId, intent.MergeIntentId, intent.OwnerId ?? _options.Owner,
                        intent.FencingToken, "result_sha_missing_in_git", Aborted: false,
                        _clock.UtcNow),
                    cancellationToken);
                return "reopened";
            }

            if (!intent.BoardSettled)
            {
                // O código JÁ está integrado e o card continuaria `approved` para sempre. Fechar o
                // lado factual aqui é o que impede a divergência permanente do BR-003.
                return await SettleBoardAsync(intent, cancellationToken) ? "settled" : "blocked";
            }

            return "blocked";
        }

        // `merging` cujo dono morreu: o lease vencido devolve o repositório. Se o commit existir,
        // o efeito aconteceu e só falta o lado factual.
        if (string.Equals(intent.State, MergeIntentState.Merging, StringComparison.Ordinal) &&
            intent.LeaseExpiresAt is { } expires && expires > _clock.UtcNow)
        {
            return "blocked";
        }

        return mergedInGit && await SettleBoardAsync(intent, cancellationToken) ? "settled" : "blocked";
    }

    private async Task<bool> SettleBoardAsync(
        MergeIntentRecord intent, CancellationToken cancellationToken)
    {
        var task = await _board.GetTaskAsync(intent.TenantId, intent.CardId, cancellationToken);
        if (task is null)
        {
            return false;
        }

        var merged = await _chain.MergeApprovedTaskAsync(
            new WorkTaskMergeCommand(
                intent.TenantId, task.BackingSolicitationId, task.Id, _options.Owner,
                $"git-branch:{intent.SourceBranch}", task.Version,
                $"task-merge:{intent.AttemptId}", _clock.UtcNow),
            cancellationToken);
        if (merged.Status is not (WorkChainMutationStatus.Applied or WorkChainMutationStatus.IdempotentReplay))
        {
            return false;
        }

        var completed = await _chain.CompleteMergedTaskAsync(
            new WorkTaskDeliveryCompleteCommand(
                intent.TenantId, task.BackingSolicitationId, task.Id, _options.Owner,
                $"git-merge:{intent.SourceBranch}", merged.TaskVersion ?? task.Version + 1,
                $"task-merge-complete:{intent.AttemptId}", _clock.UtcNow),
            cancellationToken);
        if (completed.Status is not (WorkChainMutationStatus.Applied or WorkChainMutationStatus.IdempotentReplay))
        {
            return false;
        }

        return await _intents.TrySettleBoardAsync(
            intent.TenantId, intent.MergeIntentId, _clock.UtcNow, cancellationToken);
    }

    private static async Task<bool> ExistsAsync(
        GitWorktreeManager manager, string sha, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await manager.ResolveCommitAsync(sha, cancellationToken);
            return string.Equals(resolved, sha, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                CycleFailure(_logger, exception.GetType().Name, exception);
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
