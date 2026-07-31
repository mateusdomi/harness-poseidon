using Harness.Host.Agents;
using Harness.Modules.Execution.Infrastructure.Git;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.WorkBoard;

/// <summary>Desfecho tipado da integração de um card aprovado.</summary>
public sealed record TaskIntegrationOutcome(
    bool Integrated, string ReasonCode, string? Branch, string? AttemptId, string? Detail);

/// <summary>
/// A INTEGRAÇÃO de um card já aprovado pela revisão independente — o merge real na referência
/// publicada do repositório do projeto e as duas transições da cadeia durável.
///
/// Existe como serviço, e não mais só como endpoint, por uma decisão de produto: o merge de um
/// card cuja revisão independente JÁ passou não é uma decisão do stakeholder. Enquanto era um
/// clique humano obrigatório, toda entrega — em qualquer modo — parava esperando o dono, e a
/// "fábrica autônoma" dependia dele card a card. O gate humano continua existindo onde ele
/// significa alguma coisa: a TRANSIÇÃO DE FASE, conforme o modo do projeto.
///
/// O que NÃO mudou: só entra aqui card em `approved`, isto é, revisado por agente DISTINTO de quem
/// produziu. Ator≠revisor continua sendo invariante estrutural; o que se removeu foi o pedágio
/// humano depois dela.
/// </summary>
public sealed class TaskIntegrationService(
    IWorkBoardStore board,
    IWorkChainStore chain,
    IProjectStore projects,
    AgentRunSettings settings,
    IClock clock,
    IMergeIntentStore mergeIntents)
{
    /// <summary>Quanto tempo um merge pode segurar o repositório antes de ser dado por abandonado.</summary>
    private static readonly TimeSpan MergeLease = TimeSpan.FromMinutes(10);

    private readonly IMergeIntentStore _mergeIntents =
        mergeIntents ?? throw new ArgumentNullException(nameof(mergeIntents));

    private readonly IWorkBoardStore _board = board ?? throw new ArgumentNullException(nameof(board));
    private readonly IWorkChainStore _chain = chain ?? throw new ArgumentNullException(nameof(chain));
    private readonly IProjectStore _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly AgentRunSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <param name="actorId">
    /// Quem integra: o perfil humano, quando vem da tela, ou a chefe, quando vem do laço autônomo.
    /// O ator é registrado na cadeia — é o que permite distinguir depois as duas origens.
    /// </param>
    public async Task<TaskIntegrationOutcome> IntegrateAsync(
        string tenantId,
        string taskId,
        string actorId,
        CancellationToken cancellationToken)
    {
        var task = await _board.GetTaskAsync(tenantId, taskId, cancellationToken);
        if (task is null)
        {
            return new TaskIntegrationOutcome(false, "task_not_found", null, null, null);
        }

        if (!string.Equals(task.InternalState, "approved", StringComparison.Ordinal))
        {
            return new TaskIntegrationOutcome(
                false, "task_not_approved", null, null,
                $"Only tasks approved by the independent review can be merged (state: {task.InternalState}).");
        }

        var attempts = await _board.ListAttemptsAsync(tenantId, task.Id, null, 100, cancellationToken);
        var approved = attempts.LastOrDefault(attempt =>
            string.Equals(attempt.State, "completed", StringComparison.Ordinal));
        if (approved is null)
        {
            return new TaskIntegrationOutcome(
                false, "approved_attempt_missing", null, null, "The task has no approved attempt.");
        }

        var project = await _projects.GetAsync(tenantId, task.ProjectId, cancellationToken);
        if (project is null || string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            return new TaskIntegrationOutcome(
                false, "project_repository_missing", null, approved.Id,
                "The project has no local repository bound.");
        }

        if (string.IsNullOrWhiteSpace(_settings.ControlledRoot))
        {
            return new TaskIntegrationOutcome(
                false, "merge_unavailable", null, approved.Id,
                "Merging requires Harness:AgentRuns:ControlledRoot on this machine.");
        }

        // 1. INTENÇÃO antes do efeito (Fase 0C1/BR-003). Não existe transação distribuída entre Git
        //    e SQL; fingir que existe é pior do que não ter nenhuma. Registrando a intenção antes,
        //    todo desfecho — inclusive uma queda entre o merge e o banco — vira reconhecível.
        var branch = $"task/agent-run-{approved.Id.ToLowerInvariant()}";
        var repositoryRoot = Path.GetFullPath(project.RepositoryUrl);
        var now = _clock.UtcNow;
        var intent = await _mergeIntents.RequestAsync(
            new MergeIntentRequestCommand(
                tenantId, UlidValue.New(now).ToString(), repositoryRoot, task.ProjectId, task.Id,
                approved.Id, branch, project.DefaultBranch ?? "main", null, null, now),
            cancellationToken);
        if (string.Equals(intent.State, MergeIntentState.Merged, StringComparison.Ordinal) &&
            intent.BoardSettled)
        {
            // Já integrado e conciliado: repetir o merge criaria um segundo commit para o mesmo
            // trabalho aprovado.
            return new TaskIntegrationOutcome(true, "merged", branch, approved.Id, intent.ResultSha);
        }

        // Um único merge ATIVO por repositório, garantido pelo banco — o semáforo em memória
        // anterior protegia um processo, não o repositório.
        var claimed = await _mergeIntents.TryBeginAsync(
            new MergeIntentBeginCommand(
                tenantId, intent.MergeIntentId, actorId, _clock.UtcNow, MergeLease),
            cancellationToken);
        if (claimed is null)
        {
            return new TaskIntegrationOutcome(
                false, "merge_in_progress", branch, approved.Id,
                "Another merge holds this repository right now.");
        }

        string resultSha;
        try
        {
            using var manager = await GitWorktreeManager.OpenAsync(
                repositoryRoot,
                Path.GetFullPath(_settings.ControlledRoot),
                cancellationToken);
            await manager.MergeTaskBranchAsync(
                branch,
                $"merge(card {DisplayCode(task.Title)}): tentativa {approved.Id} aprovada em review",
                cancellationToken);
            // O SHA resultante é a evidência de que o efeito existiu. Sem ele, um crash logo
            // depois seria indistinguível de um merge que nunca aconteceu.
            resultSha = await manager.ResolveCommitAsync("HEAD", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _mergeIntents.TryFailAsync(
                new MergeIntentFailCommand(
                    tenantId, intent.MergeIntentId, actorId, claimed.FencingToken,
                    exception.GetType().Name, Aborted: true, _clock.UtcNow),
                cancellationToken);
            return new TaskIntegrationOutcome(false, "merge_failed", branch, approved.Id, exception.Message);
        }

        if (!await _mergeIntents.TryRecordMergedAsync(
                new MergeIntentResultCommand(
                    tenantId, intent.MergeIntentId, actorId, claimed.FencingToken, resultSha,
                    _clock.UtcNow),
                cancellationToken))
        {
            // Perdemos o fencing DEPOIS do efeito Git. O commit existe; quem detém a intenção
            // agora conclui o lado factual. O reconciliador fecha essa aresta.
            return new TaskIntegrationOutcome(
                false, "merge_fencing_lost", branch, approved.Id,
                "The merge completed but another owner holds the intent; reconciliation will settle it.");
        }

        // 2. Cadeia durável: approved → merged → completed (board `done`), idempotente por
        //    tentativa. A conclusão destrava a próxima onda do plano na triagem do chefe.
        var merged = await _chain.MergeApprovedTaskAsync(
            new WorkTaskMergeCommand(
                tenantId, task.BackingSolicitationId, task.Id, actorId,
                $"git-branch:{branch}", task.Version, $"task-merge:{approved.Id}", _clock.UtcNow),
            cancellationToken);
        if (merged.Status is not (WorkChainMutationStatus.Applied or WorkChainMutationStatus.IdempotentReplay))
        {
            return new TaskIntegrationOutcome(
                false, "merge_state_conflict", branch, approved.Id,
                $"The work chain rejected the merge: {merged.Status}.");
        }

        var completed = await _chain.CompleteMergedTaskAsync(
            new WorkTaskDeliveryCompleteCommand(
                tenantId, task.BackingSolicitationId, task.Id, actorId,
                $"git-merge:{branch}", merged.TaskVersion ?? task.Version + 1,
                $"task-merge-complete:{approved.Id}", _clock.UtcNow),
            cancellationToken);
        if (completed.Status is WorkChainMutationStatus.Applied or WorkChainMutationStatus.IdempotentReplay)
        {
            // Só agora o Git e o banco contam a MESMA história — e isso fica registrado.
            await _mergeIntents.TrySettleBoardAsync(
                tenantId, intent.MergeIntentId, _clock.UtcNow, cancellationToken);
        }

        return completed.Status is WorkChainMutationStatus.Applied or WorkChainMutationStatus.IdempotentReplay
            ? new TaskIntegrationOutcome(true, "merged", branch, approved.Id, resultSha)
            : new TaskIntegrationOutcome(
                false, "merge_completion_conflict", branch, approved.Id,
                $"The work chain rejected the completion: {completed.Status}.");
    }

    private static string DisplayCode(string title)
    {
        var space = title.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? title : title[..space];
    }
}
