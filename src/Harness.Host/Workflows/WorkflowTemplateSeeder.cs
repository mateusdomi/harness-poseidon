using Harness.Modules.Workflows.Application;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workflows;

public sealed class WorkflowTemplateSeeder(
    IWorkflowStore authority,
    IWorkflowCatalogStore catalog,
    IClock clock)
{
    private readonly IWorkflowStore _authority =
        authority ?? throw new ArgumentNullException(nameof(authority));
    private readonly IWorkflowCatalogStore _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<int> EnsureSeededAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var existing = await _catalog.ListTemplatesAsync(tenantId, null, 200, cancellationToken);
        var existingNames = existing.Select(template => template.Name)
            .ToHashSet(StringComparer.Ordinal);
        var seeded = 0;
        foreach (var canonical in CanonicalWorkflowTemplates.All)
        {
            if (existingNames.Contains(canonical.Name))
            {
                continue;
            }

            var now = _clock.UtcNow;
            var creation = WorkflowCatalogApplicationService.CreateTemplate(
                UlidValue.New(now).ToString(),
                UlidValue.New(now.AddTicks(1)).ToString(),
                canonical.ToRequest(),
                now);
            var phases = creation.Phases
                .Select(phase => new WorkflowPhaseCreateInput(
                    phase.Id,
                    phase.Key,
                    phase.Name,
                    phase.Order,
                    phase.Objectives
                        .Select(objective => new WorkflowObjectiveCreateInput(
                            objective.Id,
                            objective.Key,
                            objective.Name,
                            objective.Kind,
                            objective.Weight))
                        .ToArray(),
                    phase.Gates
                        .Select(gate => new WorkflowGateCreateInput(
                            gate.Id,
                            gate.ObjectiveId,
                            gate.Key,
                            gate.Name,
                            gate.MinimumRequiredState,
                            gate.RequiredObjectiveIds))
                        .ToArray()))
                .ToArray();
            try
            {
                await _authority.CreatePublishedDefinitionAsync(
                    new(
                        tenantId,
                        creation.TemplateId,
                        creation.Name,
                        creation.VersionId,
                        1,
                        WorkflowDefinitionContentHash.Compute(phases),
                        phases,
                        $"seed:workflow-template:{tenantId}:{canonical.Key}",
                        now,
                        creation.Description),
                    cancellationToken);
                seeded++;
            }
            catch (IdempotencyConflictException)
            {
            }
        }

        return seeded;
    }
}
