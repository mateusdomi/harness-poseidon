using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Architecture.Domain;

namespace Harness.Modules.Architecture.Application;

/// <summary>
/// ARC-03 — Sistema 360. Agrega, para um sistema, as seções TIPADAS Negócio, Tecnologia, Integrações,
/// Operação, Dados e Governança. PURO e determinístico; cada seção deriva estritamente dos fatos
/// gravados (elemento, relacionamentos e metadados do sistema). Sem sistema correspondente, retorna nulo.
/// </summary>
public static class System360Projector
{
    public static System360Contract? Project(ArchitectureModelInput model, string systemId)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId);

        var element = model.Elements.FirstOrDefault(e =>
            e.Id == systemId && e.State == ArchitectureKinds.Implemented &&
            e.Kind == ArchitectureKinds.SystemKind);
        if (element is null)
        {
            return null;
        }

        var meta = model.Systems.FirstOrDefault(s => s.ElementId == systemId);
        var elementsById = model.Elements
            .Where(e => e.State == ArchitectureKinds.Implemented)
            .ToDictionary(e => e.Id, StringComparer.Ordinal);

        var containers = model.Relationships
            .Where(r => r.State == ArchitectureKinds.Implemented && r.Kind == "contains" &&
                r.SourceId == systemId && elementsById.ContainsKey(r.TargetId))
            .Select(r => ArchitectureMapper.ToContract(elementsById[r.TargetId]))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var integrationEdges = model.Relationships
            .Where(r => r.State == ArchitectureKinds.Implemented &&
                ArchitectureKinds.IsDependencyEdge(r.Kind));
        var outgoing = integrationEdges
            .Where(r => r.SourceId == systemId && elementsById.ContainsKey(r.TargetId))
            .Select(r => Edge(elementsById, r))
            .OrderBy(e => e.TargetName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var incoming = integrationEdges
            .Where(r => r.TargetId == systemId && elementsById.ContainsKey(r.SourceId))
            .Select(r => Edge(elementsById, r))
            .OrderBy(e => e.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var business = new System360BusinessContract(
            element.Name, element.Description, meta?.Domain, meta?.Capabilities ?? [],
            meta?.Criticality ?? "medium", meta?.Owner);
        var technology = new System360TechnologyContract(
            meta?.TechStack ?? [], meta?.LifecycleStatus, containers);
        var integrations = new System360IntegrationsContract(outgoing, incoming);
        var operation = new System360OperationContract(
            meta?.Sla, meta?.IncidentCount ?? 0, meta?.LastIncidentAt, meta?.BackupPolicy,
            meta?.DrPolicy, meta?.CostMonthlyUsd);
        var data = new System360DataContract(
            meta?.Pii ?? false, meta?.Sensitive ?? false, meta?.Retention, meta?.DataClasses ?? []);
        var governance = new System360GovernanceContract(
            (meta?.Documents ?? []).Select(ArchitectureMapper.ToContract).ToArray(),
            meta?.AdrCount ?? 0, meta?.Risks ?? [], meta?.LastReviewAt, meta?.ReviewConfidence,
            meta?.BusFactor);

        return new System360Contract(
            systemId, business, technology, integrations, operation, data, governance);
    }

    private static IntegrationEdgeContract Edge(
        IReadOnlyDictionary<string, ArchElement> elements, ArchRelationship relationship) => new(
        relationship.SourceId, elements.GetValueOrDefault(relationship.SourceId)?.Name ?? relationship.SourceId,
        relationship.TargetId, elements.GetValueOrDefault(relationship.TargetId)?.Name ?? relationship.TargetId,
        relationship.Kind);
}
