using Harness.Modules.Architecture.Application;
using Harness.Modules.Architecture.Domain;
using Harness.Persistence.Abstractions.Architecture;

namespace Harness.Host.Architecture;

/// <summary>
/// Camada de leitura do Architecture Hub. NÃO tem dados próprios: materializa o
/// <see cref="ArchitectureModelInput"/> puro a partir do <see cref="IArchitectureStore"/> (paginando
/// para escalar a ~200 sistemas) e delega toda a lógica aos projetores/analisadores puros do módulo.
/// O modelo VIGENTE e o PROPOSTO são carregados separados conforme o caso de uso (ARC-05).
/// </summary>
public sealed class ArchitectureReadModelService(IArchitectureStore store)
{
    private const int PageSize = 200;
    private const int MaxPages = 25;

    private readonly IArchitectureStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Materializa APENAS o modelo vigente (implemented) + metadados de sistema.</summary>
    public async Task<ArchitectureModelInput> BuildImplementedAsync(
        string tenantId, string? projectId, CancellationToken token)
    {
        var elements = await PageElementsAsync(tenantId, projectId, ArchitectureKinds.Implemented, token);
        var relationships = await PageRelationshipsAsync(tenantId, projectId, ArchitectureKinds.Implemented, token);
        var systems = await PageSystemsAsync(tenantId, projectId, token);
        return new ArchitectureModelInput(
            elements.Select(ToInput).ToArray(),
            relationships.Select(ToInput).ToArray(),
            systems.Select(ToInput).ToArray());
    }

    /// <summary>
    /// Materializa o modelo vigente + os itens PROPOSTOS de uma proposta (para diff/impacto/aplicar).
    /// Os dois estados coexistem no mesmo <see cref="ArchitectureModelInput"/>, separados por State.
    /// </summary>
    public async Task<ArchitectureModelInput> BuildProposalAsync(
        string tenantId, string? projectId, string proposalId, CancellationToken token)
    {
        var implementedElements = await PageElementsAsync(tenantId, projectId, ArchitectureKinds.Implemented, token);
        var implementedRelationships = await PageRelationshipsAsync(tenantId, projectId, ArchitectureKinds.Implemented, token);
        var proposedElements = (await PageElementsAsync(tenantId, projectId, ArchitectureKinds.Proposed, token))
            .Where(e => e.ProposalId == proposalId);
        var proposedRelationships = (await PageRelationshipsAsync(tenantId, projectId, ArchitectureKinds.Proposed, token))
            .Where(r => r.ProposalId == proposalId);
        var systems = await PageSystemsAsync(tenantId, projectId, token);

        return new ArchitectureModelInput(
            implementedElements.Concat(proposedElements).Select(ToInput).ToArray(),
            implementedRelationships.Concat(proposedRelationships).Select(ToInput).ToArray(),
            systems.Select(ToInput).ToArray());
    }

    private async Task<List<ArchitectureElementRecord>> PageElementsAsync(
        string tenantId, string? projectId, string state, CancellationToken token)
    {
        var result = new List<ArchitectureElementRecord>();
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _store.ListElementsAsync(tenantId, projectId, state, afterId, PageSize, token);
            result.AddRange(batch);
            if (batch.Count < PageSize) break;
            afterId = batch[^1].Id;
        }

        return result;
    }

    private async Task<List<ArchitectureRelationshipRecord>> PageRelationshipsAsync(
        string tenantId, string? projectId, string state, CancellationToken token)
    {
        var result = new List<ArchitectureRelationshipRecord>();
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _store.ListRelationshipsAsync(tenantId, projectId, state, afterId, PageSize, token);
            result.AddRange(batch);
            if (batch.Count < PageSize) break;
            afterId = batch[^1].Id;
        }

        return result;
    }

    private async Task<List<ArchitectureSystemMetadataRecord>> PageSystemsAsync(
        string tenantId, string? projectId, CancellationToken token)
    {
        var result = new List<ArchitectureSystemMetadataRecord>();
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await _store.ListSystemMetadataAsync(tenantId, projectId, afterId, PageSize, token);
            result.AddRange(batch);
            if (batch.Count < PageSize) break;
            afterId = batch[^1].ElementId;
        }

        return result;
    }

    public static ArchElement ToInput(ArchitectureElementRecord e) => new(
        e.Id, e.ProjectId, e.Kind, e.Name, e.Description, e.Properties, e.State, e.Locked, e.Version,
        e.ProposalId, e.CounterpartId, e.ChangeKind);

    public static ArchRelationship ToInput(ArchitectureRelationshipRecord r) => new(
        r.Id, r.ProjectId, r.SourceId, r.TargetId, r.Kind, r.Properties, r.State, r.Version,
        r.ProposalId, r.CounterpartId, r.ChangeKind);

    public static ArchSystemMetadata ToInput(ArchitectureSystemMetadataRecord m) => new(
        m.ElementId, m.Domain, m.Capabilities, m.Owner, m.Criticality, m.TechStack, m.LifecycleStatus,
        m.CostMonthlyUsd, m.IncidentCount, m.BusFactor, m.DuplicateOfId, m.RiskLevel, m.Sla,
        m.BackupPolicy, m.DrPolicy, m.LastIncidentAt, m.Pii, m.Sensitive, m.Retention, m.DataClasses,
        m.Documents.Select(d => new ArchDocumentLink(d.Id, d.Title, d.Kind, d.State)).ToArray(),
        m.AdrCount, m.Risks, m.LastReviewAt, m.ReviewConfidence);
}
