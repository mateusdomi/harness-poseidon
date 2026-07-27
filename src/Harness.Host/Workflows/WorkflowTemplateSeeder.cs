using System.Text.Json;
using Harness.Modules.Workflows.Application;
using Harness.Modules.Workflows.Contracts;
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
        var existingByName = existing.ToDictionary(template => template.Name, StringComparer.Ordinal);
        var seeded = 0;
        foreach (var canonical in CanonicalWorkflowTemplates.All)
        {
            var now = _clock.UtcNow;
            if (existingByName.TryGetValue(canonical.Name, out var existingTemplate))
            {
                var current = existingTemplate.CurrentVersionId is null
                    ? null
                    : await _catalog.GetVersionAsync(
                        tenantId, existingTemplate.CurrentVersionId, cancellationToken);
                if (current is not null &&
                    current.Phases.SequenceEqual(canonical.Phases) &&
                    TransitionsMatch(current.TransitionsJson, canonical.Transitions))
                {
                    continue;
                }

                // Evolui o template canônico existente por nova versão. Runs e
                // bindings ativos continuam apontando para a versão imutável
                // anterior; novos vínculos recebem a versão corrente.
                var versionId = UlidValue.New(now).ToString();
                var value = WorkflowCatalogApplicationService.CreateVersion(
                    existingTemplate.Id,
                    versionId,
                    new PublishWorkflowVersionRequest(
                        canonical.Phases,
                        canonical.GatesByPhase,
                        Transitions: canonical.Transitions,
                        Changelog: $"Reconcilia o workflow canônico {canonical.Key} com a definição vigente."),
                    now);
                var upgradedPhases = AddDocumentObjectives(
                    value.Hierarchy.Phases, canonical, now);
                await _catalog.PublishVersionAsync(
                    new WorkflowVersionPublishCommand(
                        tenantId,
                        existingTemplate.Id,
                        versionId,
                        upgradedPhases,
                        "{}",
                        null,
                        JsonSerializer.Serialize(canonical.Transitions),
                        value.Hierarchy.Changelog,
                        now),
                    cancellationToken);
                seeded++;
                continue;
            }

            var creation = WorkflowCatalogApplicationService.CreateTemplate(
                UlidValue.New(now).ToString(),
                UlidValue.New(now.AddTicks(1)).ToString(),
                canonical.ToRequest(),
                now);
            var phases = AddDocumentObjectives(creation.Phases, canonical, now);
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
                        creation.Description,
                        TransitionsJson: JsonSerializer.Serialize(canonical.Transitions)),
                    cancellationToken);
                seeded++;
            }
            catch (IdempotencyConflictException)
            {
            }
        }

        return seeded;
    }

    private static IReadOnlyList<WorkflowPhaseCreateInput> AddDocumentObjectives(
        IReadOnlyList<WorkflowApiPhaseCreation> source,
        CanonicalWorkflowTemplate canonical,
        DateTimeOffset now)
    {
        // Ticks altos e crescentes para os ids dos objetivos-documento,
        // fora da faixa usada pelo application service.
        var documentTick = 1_000L;
        return
        [
            .. source.Select(phase =>
            {
                var objectives = phase.Objectives
                    .Select(objective => new WorkflowObjectiveCreateInput(
                        objective.Id,
                        objective.Key,
                        objective.Name,
                        objective.Kind,
                        objective.Weight))
                    .ToList();
                var documentObjectiveIds = new List<string>();

                if (canonical.DocumentsByPhase.TryGetValue(phase.Name, out var documents))
                {
                    var index = 0;
                    foreach (var document in documents)
                    {
                        index++;
                        var objectiveId = UlidValue.New(now.AddTicks(documentTick++)).ToString();
                        documentObjectiveIds.Add(objectiveId);
                        objectives.Add(new WorkflowObjectiveCreateInput(
                            objectiveId,
                            $"document-{index}",
                            document,
                            "document",
                            1m));
                    }
                }

                return new WorkflowPhaseCreateInput(
                    phase.Id,
                    phase.Key,
                    phase.Name,
                    phase.Order,
                    objectives,
                    phase.Gates
                        .Select(gate => new WorkflowGateCreateInput(
                            gate.Id,
                            gate.ObjectiveId,
                            gate.Key,
                            gate.Name,
                            gate.MinimumRequiredState,
                            [
                                .. gate.RequiredObjectiveIds,
                                .. documentObjectiveIds,
                            ]))
                        .ToArray());
            }),
        ];
    }

    private static bool TransitionsMatch(
        string currentJson,
        IReadOnlyDictionary<string, IReadOnlyList<string>> expected)
    {
        try
        {
            var current = JsonSerializer.Deserialize<Dictionary<string, string[]>>(currentJson);
            return current is not null &&
                current.Count == expected.Count &&
                expected.All(pair =>
                    current.TryGetValue(pair.Key, out var targets) &&
                    targets.SequenceEqual(pair.Value, StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
