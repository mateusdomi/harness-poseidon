using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Workflows;
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
    IClock clock)
{
    private readonly IProjectStore _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly IWorkflowCatalogStore _workflows =
        workflows ?? throw new ArgumentNullException(nameof(workflows));
    private readonly WorkflowTemplateSeeder _workflowSeeder =
        workflowSeeder ?? throw new ArgumentNullException(nameof(workflowSeeder));
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

        var bound = 0;
        foreach (var project in projectPage)
        {
            var existing = await _workflows.ListBindingsAsync(
                tenantId, project.Id, null, 1, cancellationToken);
            if (existing.Count > 0)
            {
                continue;
            }

            var template = await ProjectWorkflowLinker.ResolveRecommendedAsync(
                _workflows, _workflowSeeder, tenantId, cancellationToken);
            if (template is null)
            {
                // Sem template publicável (nem após semear os canônicos): não quebra — o projeto
                // converge quando um template existir. RN-02 permanece a intenção; a exceção honesta
                // é a ausência de qualquer workflow para vincular.
                continue;
            }

            var result = await ProjectWorkflowLinker.LinkAsync(
                _workflows, tenantId, project.Id, template, versionId: null, actorProfileId,
                _clock, cancellationToken);
            if (result.Outcome is ProjectWorkflowLinker.LinkOutcome.Applied)
            {
                bound++;
            }
        }

        return bound;
    }
}
