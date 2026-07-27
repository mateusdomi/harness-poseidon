using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workflows;

/// <summary>
/// RN-02: é impossível conversar com o Chefe sem um workflow vinculado, então NENHUM projeto pode
/// ficar sem workflow. Este seeder converge, de forma idempotente, todo projeto SEM binding para o
/// template recomendado publicado (o "Software Delivery Standard"), reusando o
/// <see cref="ProjectWorkflowLinker"/> — os mesmos efeitos do endpoint explícito e da pré-seleção na
/// criação (GP-09). É a única garantia da invariante para projetos que nasceram antes da regra
/// (por exemplo o próprio projeto "Poseidon"), e roda no startup gateada apenas por
/// <c>AgentRuns.Enabled</c>, independente de haver conta do Chefe.
///
/// Idempotente: um projeto que já tem binding é ignorado; se nenhum template publicável existir
/// (mesmo após semear os canônicos), o projeto é deixado como está e converge no próximo restart —
/// nunca quebra o boot nem a criação.
/// </summary>
public sealed class ProjectWorkflowConvergenceSeeder(
    IProjectStore projects,
    IWorkflowCatalogStore workflows,
    WorkflowTemplateSeeder workflowSeeder,
    IWorkflowStore runAuthority,
    IClock clock)
{
    private readonly IProjectStore _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly IWorkflowCatalogStore _workflows =
        workflows ?? throw new ArgumentNullException(nameof(workflows));
    private readonly WorkflowTemplateSeeder _workflowSeeder =
        workflowSeeder ?? throw new ArgumentNullException(nameof(workflowSeeder));
    private readonly IWorkflowStore _runAuthority =
        runAuthority ?? throw new ArgumentNullException(nameof(runAuthority));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Vincula o template recomendado a cada projeto do tenant que ainda não tem workflow. Lista os
    /// projetos internamente e delega ao overload que recebe a página, para reuso.
    /// </summary>
    /// <returns>Quantos projetos foram efetivamente vinculados nesta chamada (0 quando já convergido).</returns>
    public async Task<int> EnsureBoundAsync(
        string tenantId, string actorProfileId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorProfileId);
        var page = await _projects.ListAsync(tenantId, null, 200, cancellationToken);
        return await EnsureBoundAsync(tenantId, actorProfileId, page, cancellationToken);
    }

    /// <summary>
    /// Overload que recebe uma página de projetos já lida (por exemplo pelo seeder de catálogo do
    /// Chefe), evitando uma segunda listagem quando o chamador já a tem em mãos.
    /// </summary>
    public async Task<int> EnsureBoundAsync(
        string tenantId,
        string actorProfileId,
        IReadOnlyList<ProjectRecord> projectPage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorProfileId);
        ArgumentNullException.ThrowIfNull(projectPage);

        // Nomes dos templates canônicos LEGADOS (todo canônico que não é a esteira do
        // playbook). Um binding para um deles é pré-playbook e converge para o recomendado;
        // um template CUSTOMIZADO pelo usuário nunca é tocado.
        var legacyNames = new HashSet<string>(
            CanonicalWorkflowTemplates.All
                .Where(template => template.Key != CanonicalWorkflowTemplates.RecommendedKey)
                .Select(template => template.Name),
            StringComparer.Ordinal);

        var bound = 0;
        foreach (var project in projectPage)
        {
            var existing = await _workflows.ListBindingsAsync(
                tenantId, project.Id, null, 1, cancellationToken);

            var template = await ProjectWorkflowLinker.ResolveRecommendedAsync(
                _workflows, _workflowSeeder, tenantId, cancellationToken);
            if (template is null)
            {
                // Sem template publicável (nem após semear os canônicos): não quebra — o projeto
                // converge quando um template existir. RN-02 permanece a intenção; a exceção honesta
                // é a ausência de qualquer workflow para vincular.
                continue;
            }

            if (existing.Count > 0)
            {
                // O playbook é a 2ª fonte da verdade: um binding preso a um template canônico
                // LEGADO converge para a esteira de 9 fases, preservando modo de operação e
                // aceitações de risco, com o rebind auditado no ledger. Idempotente: quem já
                // aponta para o recomendado (ou para template customizado) é ignorado.
                var binding = existing[0];
                if (template.CurrentVersionId is null)
                {
                    continue;
                }

                if (binding.TemplateId == template.Id)
                {
                    // O template já é o correto, mas a versão pode ter sido sucedida por uma
                    // reconciliação canônica (por exemplo, transições/evidências da Fase 9).
                    // Atualiza o binding antes de migrar o run: sem isso, o run corrente usaria a
                    // versão nova, mas o próximo run voltaria a nascer na versão obsoleta.
                    if (binding.ActiveVersionId != template.CurrentVersionId)
                    {
                        _ = await _workflows.RebindTemplateAsync(
                            new WorkflowTemplateRebindCommand(
                                tenantId, binding.Id, project.Id, template.Id,
                                template.CurrentVersionId, actorProfileId, _clock.UtcNow),
                            cancellationToken);
                        bound++;
                    }

                    // Um run ativo pode ter ficado numa versão anterior. A migração é
                    // idempotente: só age quando há run ativo divergente.
                    await MigrateActiveRunAsync(
                        tenantId, project.Id, binding.Id, template.CurrentVersionId,
                        cancellationToken);
                    await EnsureStartedRunAsync(
                        tenantId, project.Id, binding.Id, template.CurrentVersionId,
                        cancellationToken);
                    continue;
                }

                var current = await _workflows.GetTemplateAsync(
                    tenantId, binding.TemplateId, cancellationToken);
                if (current is null || !legacyNames.Contains(current.Name))
                {
                    continue;
                }

                _ = await _workflows.RebindTemplateAsync(
                    new WorkflowTemplateRebindCommand(
                        tenantId, binding.Id, project.Id, template.Id,
                        template.CurrentVersionId, actorProfileId, _clock.UtcNow),
                    cancellationToken);

                // O run ATIVO da versão legada migra junto: sem isto o chat continuaria
                // mostrando a fase do template antigo. O run velho é CANCELADO (história
                // preservada, nada de progresso fabricado) e um run novo nasce na fase 1 da
                // esteira do playbook — ambos auditados pelas mutações do motor de workflow.
                await MigrateActiveRunAsync(
                    tenantId, project.Id, binding.Id, template.CurrentVersionId, cancellationToken);
                await EnsureStartedRunAsync(
                    tenantId, project.Id, binding.Id, template.CurrentVersionId, cancellationToken);
                bound++;
                continue;
            }

            var result = await ProjectWorkflowLinker.LinkAsync(
                _workflows, tenantId, project.Id, template, versionId: null, actorProfileId,
                _clock, cancellationToken);
            if (result.Outcome is ProjectWorkflowLinker.LinkOutcome.Applied)
            {
                bound++;
            }

            // Vincular não basta: sem RUN o projeto não tem fase ativa, nem gate, nem artefato
            // esperado — a esteira existe no papel e o produto se comporta como se não houvesse
            // workflow nenhum. O único código que iniciava run era um seeder preso ao projeto
            // legado (id de run FIXO), então TODO projeto criado depois nascia sem fase.
            var linked = await _workflows.ListBindingsAsync(tenantId, project.Id, null, 1, cancellationToken);
            if (linked.Count > 0 && linked[0].ActiveVersionId is { Length: > 0 } activeVersion)
            {
                await EnsureStartedRunAsync(
                    tenantId, project.Id, linked[0].Id, activeVersion, cancellationToken);
            }
        }

        return bound;
    }

    /// <summary>
    /// Garante que o binding tenha um RUN ATIVO na versão corrente: sem run não existe fase ativa,
    /// e sem fase o projeto não tem gate, artefato esperado nem posição na esteira — o chat não
    /// consegue nem dizer em que fase o trabalho está.
    ///
    /// Idempotente por leitura: só cria quando não há run `running`/`paused`. O id é um ULID por
    /// run — um id fixo limitaria o produto a um único projeto com fase no sistema inteiro.
    /// </summary>
    private async Task EnsureStartedRunAsync(
        string tenantId,
        string projectId,
        string workflowId,
        string versionId,
        CancellationToken cancellationToken)
    {
        var runs = await _workflows.ListRunsAsync(tenantId, workflowId, null, 20, cancellationToken);
        if (runs.Any(run => run.State is "running" or "paused"))
        {
            return;
        }

        var now = _clock.UtcNow;
        var runId = UlidValue.New(now).ToString();
        var created = await _runAuthority.CreateRunAsync(
            new WorkflowRunCreateCommand(
                tenantId, projectId, versionId, runId,
                $"workflow-convergence:create:{runId}", now, workflowId),
            cancellationToken);
        _ = await _runAuthority.TransitionRunAsync(
            new WorkflowRunTransitionCommand(
                tenantId, runId, WorkflowRunTransition.Start, created.RunVersion,
                $"workflow-convergence:start:{runId}", now.AddTicks(1)),
            cancellationToken);
    }

    private async Task MigrateActiveRunAsync(
        string tenantId,
        string projectId,
        string workflowId,
        string newVersionId,
        CancellationToken cancellationToken)
    {
        var runs = await _workflows.ListRunsAsync(tenantId, workflowId, null, 20, cancellationToken);
        var activeOnNewVersion = false;
        var cancelledLegacyRun = false;
        foreach (var run in runs.Where(run => run.State is "running" or "paused"))
        {
            if (run.VersionId == newVersionId)
            {
                // Já existe run ativo na versão nova: nada a criar (idempotência).
                activeOnNewVersion = true;
                continue;
            }

            _ = await _runAuthority.TransitionRunAsync(
                new WorkflowRunTransitionCommand(
                    tenantId, run.Id, WorkflowRunTransition.Cancel, run.Version,
                    $"playbook-convergence:cancel:{run.Id}", _clock.UtcNow),
                cancellationToken);
            cancelledLegacyRun = true;
        }

        if (!cancelledLegacyRun || activeOnNewVersion)
        {
            // Sem run legado ativo não há o que substituir; e com run já na versão nova,
            // criar outro seria duplicar — o próximo run nasce da versão nova naturalmente.
            return;
        }

        var now = _clock.UtcNow;
        var runId = UlidValue.New(now).ToString();
        _ = await _runAuthority.CreateRunAsync(
            new WorkflowRunCreateCommand(
                tenantId, projectId, newVersionId, runId,
                $"playbook-convergence:create:{runId}", now, workflowId),
            cancellationToken);
        _ = await _runAuthority.TransitionRunAsync(
            new WorkflowRunTransitionCommand(
                tenantId, runId, WorkflowRunTransition.Start, 1,
                $"playbook-convergence:start:{runId}", now.AddTicks(1)),
            cancellationToken);
    }
}
