using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Architecture.Domain;

namespace Harness.Modules.Architecture.Application;

/// <summary>
/// ARC-05 — edição humana da arquitetura proposta. Núcleo PURO e determinístico que mantém o modelo
/// PROPOSTO e o VIGENTE SEPARADOS e computa: (1) o DIFF entre eles; (2) o IMPACTO antes de aplicar
/// (dependentes afetados + documentos que viram rascunho); (3) o plano de APLICAÇÃO que RESPEITA os
/// LOCKS — um elemento travado NUNCA é sobrescrito por uma mudança de agente — e EXIGE justificativa
/// em mudança crítica. Nada é inventado: tudo deriva das linhas propostas e vigentes.
/// </summary>
public static class ArchitectureProposalPlanner
{
    // -----------------------------------------------------------------------------------------------
    // DIFF
    // -----------------------------------------------------------------------------------------------

    public static ArchitectureDiffContract Diff(ArchitectureModelInput model, string proposalId)
    {
        var changes = ComputeChanges(model, proposalId);
        return new ArchitectureDiffContract(
            proposalId,
            changes.Count(c => c.ChangeKind == "add"),
            changes.Count(c => c.ChangeKind == "modify"),
            changes.Count(c => c.ChangeKind == "remove"),
            changes.Count(c => c.TargetsLockedElement),
            changes.Any(c => c.Critical),
            changes);
    }

    // -----------------------------------------------------------------------------------------------
    // IMPACT (mostrado ANTES de aplicar)
    // -----------------------------------------------------------------------------------------------

    public static ArchitectureImpactContract Impact(ArchitectureModelInput model, string proposalId)
    {
        var changes = ComputeChanges(model, proposalId);

        // Dependentes afetados: quem depende de cada elemento vigente tocado por uma mudança.
        var affected = new Dictionary<string, ArchitectureDependentContract>(StringComparer.Ordinal);
        foreach (var change in changes.Where(c => c.CounterpartId is not null))
        {
            var query = DependencyAnalyzer.WhoDependsOn(model, change.CounterpartId!);
            foreach (var dependent in query.Dependents)
            {
                affected.TryAdd(dependent.ElementId, dependent);
            }
        }

        var documents = AffectedDocuments(model, changes)
            .Select(ArchitectureMapper.ToContract)
            .ToArray();

        return new ArchitectureImpactContract(
            proposalId,
            affected.Values
                .OrderByDescending(d => d.Direct)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            documents,
            changes.Where(c => c.TargetsLockedElement).ToArray(),
            changes.Any(c => c.Critical));
    }

    // -----------------------------------------------------------------------------------------------
    // APPLY (respeita locks; exige justificativa em mudança crítica)
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Computa o plano de aplicação. Se houver mudança crítica sem justificativa, devolve status
    /// 'justification_required' e NÃO aplica nada. Mudanças que tocam elemento TRAVADO são puladas
    /// (o travado nunca é sobrescrito). O restante compõe as operações a executar no store.
    /// </summary>
    public static ArchApplyPlan PlanApply(
        ArchitectureModelInput model, string proposalId, string? justification, DateTimeOffset occurredAt)
    {
        var changes = ComputeChanges(model, proposalId);
        var requiresJustification = changes.Any(c => c.Critical);
        var hasJustification = !string.IsNullOrWhiteSpace(justification);

        if (requiresJustification && !hasJustification)
        {
            return new ArchApplyPlan(
                new ArchitectureApplyResultContract(
                    proposalId, "justification_required", 0,
                    changes.Count(c => c.TargetsLockedElement), [],
                    changes, []),
                [], [], [], [], [], false);
        }

        var proposedElements = model.Elements
            .Where(e => e.State == ArchitectureKinds.Proposed && e.ProposalId == proposalId)
            .ToDictionary(e => e.Id, StringComparer.Ordinal);
        var proposedRelationships = model.Relationships
            .Where(r => r.State == ArchitectureKinds.Proposed && r.ProposalId == proposalId)
            .ToDictionary(r => r.Id, StringComparer.Ordinal);
        var implementedElements = model.Elements
            .Where(e => e.State == ArchitectureKinds.Implemented)
            .ToDictionary(e => e.Id, StringComparer.Ordinal);
        var implementedRelationships = model.Relationships
            .Where(r => r.State == ArchitectureKinds.Implemented)
            .ToDictionary(r => r.Id, StringComparer.Ordinal);

        var elementUpserts = new List<ArchElement>();
        var elementRemovals = new List<string>();
        var relationshipUpserts = new List<ArchRelationship>();
        var relationshipRemovals = new List<string>();
        var applied = new List<ArchitectureChangeContract>();
        var skipped = new List<ArchitectureChangeContract>();

        foreach (var change in changes)
        {
            // ENFORCE lock: um elemento travado nunca é sobrescrito por mudança proposta por agente.
            if (change.TargetsLockedElement)
            {
                skipped.Add(change);
                continue;
            }

            if (change.EntityType == "element")
            {
                if (change.ChangeKind == "remove")
                {
                    elementRemovals.Add(change.CounterpartId!);
                }
                else if (proposedElements.TryGetValue(change.ProposedId, out var proposed))
                {
                    var counterpart = change.CounterpartId is null
                        ? null
                        : implementedElements.GetValueOrDefault(change.CounterpartId);
                    elementUpserts.Add(proposed with
                    {
                        Id = counterpart?.Id ?? proposed.Id,
                        State = ArchitectureKinds.Implemented,
                        Locked = counterpart?.Locked ?? false,
                        Version = (counterpart?.Version ?? 0) + 1,
                        ProposalId = null,
                        CounterpartId = null,
                        ChangeKind = null,
                    });
                }
            }
            else
            {
                if (change.ChangeKind == "remove")
                {
                    relationshipRemovals.Add(change.CounterpartId!);
                }
                else if (proposedRelationships.TryGetValue(change.ProposedId, out var proposed))
                {
                    var counterpart = change.CounterpartId is null
                        ? null
                        : implementedRelationships.GetValueOrDefault(change.CounterpartId);
                    relationshipUpserts.Add(proposed with
                    {
                        Id = counterpart?.Id ?? proposed.Id,
                        State = ArchitectureKinds.Implemented,
                        Version = (counterpart?.Version ?? 0) + 1,
                        ProposalId = null,
                        CounterpartId = null,
                        ChangeKind = null,
                    });
                }
            }

            applied.Add(change);
        }

        // Documentos afetados (dos sistemas efetivamente aplicados) viram RASCUNHO.
        var appliedCounterparts = applied
            .Where(c => c.CounterpartId is not null)
            .Select(c => c.CounterpartId!)
            .ToHashSet(StringComparer.Ordinal);
        var systemUpdates = new List<ArchSystemMetadata>();
        var draftedDocs = new List<ArchDocumentLink>();
        foreach (var system in model.Systems.Where(s => appliedCounterparts.Contains(s.ElementId)))
        {
            if (system.Documents.All(d => d.State == "draft"))
            {
                continue;
            }

            var drafted = system.Documents
                .Select(d => d.State == "draft" ? d : d with { State = "draft" })
                .ToArray();
            draftedDocs.AddRange(drafted);
            systemUpdates.Add(system with { Documents = drafted });
        }

        var result = new ArchitectureApplyResultContract(
            proposalId,
            skipped.Count == 0 ? "applied" : "applied_with_skips",
            applied.Count,
            skipped.Count,
            applied,
            skipped,
            draftedDocs.Select(ArchitectureMapper.ToContract).ToArray());

        return new ArchApplyPlan(
            result, elementUpserts, elementRemovals, relationshipUpserts, relationshipRemovals,
            systemUpdates, true);
    }

    // -----------------------------------------------------------------------------------------------
    // Núcleo compartilhado: computa a lista de mudanças proposta-vs-vigente.
    // -----------------------------------------------------------------------------------------------

    private static List<ArchitectureChangeContract> ComputeChanges(
        ArchitectureModelInput model, string proposalId)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);

        var implementedElements = model.Elements
            .Where(e => e.State == ArchitectureKinds.Implemented)
            .ToDictionary(e => e.Id, StringComparer.Ordinal);
        var implementedRelationships = model.Relationships
            .Where(r => r.State == ArchitectureKinds.Implemented)
            .ToDictionary(r => r.Id, StringComparer.Ordinal);
        var criticalSystems = model.Systems
            .Where(s => string.Equals(s.Criticality, "critical", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.ElementId)
            .ToHashSet(StringComparer.Ordinal);

        var changes = new List<ArchitectureChangeContract>();

        foreach (var proposed in model.Elements
            .Where(e => e.State == ArchitectureKinds.Proposed && e.ProposalId == proposalId)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var kind = proposed.ChangeKind ?? "add";
            var counterpart = proposed.CounterpartId is null
                ? null
                : implementedElements.GetValueOrDefault(proposed.CounterpartId);
            var fields = kind == "modify" && counterpart is not null
                ? ElementFieldChanges(counterpart, proposed)
                : Array.Empty<string>();
            var locked = counterpart?.Locked ?? false;
            var critical = kind == "remove" ||
                (proposed.CounterpartId is not null && criticalSystems.Contains(proposed.CounterpartId));
            changes.Add(new ArchitectureChangeContract(
                "element", kind, proposed.CounterpartId, proposed.Id, proposed.Name,
                locked, critical, fields));
        }

        foreach (var proposed in model.Relationships
            .Where(r => r.State == ArchitectureKinds.Proposed && r.ProposalId == proposalId)
            .OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var kind = proposed.ChangeKind ?? "add";
            var counterpart = proposed.CounterpartId is null
                ? null
                : implementedRelationships.GetValueOrDefault(proposed.CounterpartId);
            var fields = kind == "modify" && counterpart is not null
                ? RelationshipFieldChanges(counterpart, proposed)
                : Array.Empty<string>();
            // Um relacionamento cujo elemento de origem/destino está TRAVADO não pode ser reescrito.
            var locked = (implementedElements.GetValueOrDefault(proposed.SourceId)?.Locked ?? false) ||
                (implementedElements.GetValueOrDefault(proposed.TargetId)?.Locked ?? false);
            var critical = kind == "remove" ||
                criticalSystems.Contains(proposed.SourceId) || criticalSystems.Contains(proposed.TargetId);
            changes.Add(new ArchitectureChangeContract(
                "relationship", kind, proposed.CounterpartId, proposed.Id,
                $"{proposed.SourceId}-[{proposed.Kind}]->{proposed.TargetId}", locked, critical, fields));
        }

        return changes;
    }

    private static string[] ElementFieldChanges(ArchElement current, ArchElement proposed)
    {
        var fields = new List<string>();
        if (!string.Equals(current.Name, proposed.Name, StringComparison.Ordinal)) fields.Add("name");
        if (!string.Equals(current.Description, proposed.Description, StringComparison.Ordinal)) fields.Add("description");
        if (!string.Equals(current.Kind, proposed.Kind, StringComparison.Ordinal)) fields.Add("kind");
        if (!PropertiesEqual(current.Properties, proposed.Properties)) fields.Add("properties");
        return fields.ToArray();
    }

    private static string[] RelationshipFieldChanges(ArchRelationship current, ArchRelationship proposed)
    {
        var fields = new List<string>();
        if (!string.Equals(current.SourceId, proposed.SourceId, StringComparison.Ordinal)) fields.Add("sourceId");
        if (!string.Equals(current.TargetId, proposed.TargetId, StringComparison.Ordinal)) fields.Add("targetId");
        if (!string.Equals(current.Kind, proposed.Kind, StringComparison.Ordinal)) fields.Add("kind");
        if (!PropertiesEqual(current.Properties, proposed.Properties)) fields.Add("properties");
        return fields.ToArray();
    }

    private static bool PropertiesEqual(
        IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static ArchDocumentLink[] AffectedDocuments(
        ArchitectureModelInput model, IReadOnlyList<ArchitectureChangeContract> changes)
    {
        var counterparts = changes
            .Where(c => c.CounterpartId is not null)
            .Select(c => c.CounterpartId!)
            .ToHashSet(StringComparer.Ordinal);
        return model.Systems
            .Where(s => counterparts.Contains(s.ElementId))
            .SelectMany(s => s.Documents)
            .GroupBy(d => d.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

/// <summary>
/// Plano de aplicação PURO devolvido ao Host: as operações a executar no store, os documentos que
/// viram rascunho e o resultado. <see cref="ShouldApply"/> é falso quando falta justificativa crítica.
/// </summary>
public sealed record ArchApplyPlan(
    ArchitectureApplyResultContract Result,
    IReadOnlyList<ArchElement> ElementUpserts,
    IReadOnlyList<string> ElementRemovals,
    IReadOnlyList<ArchRelationship> RelationshipUpserts,
    IReadOnlyList<string> RelationshipRemovals,
    IReadOnlyList<ArchSystemMetadata> SystemDocumentUpdates,
    bool ShouldApply);
