using System.Text.Json;
using Harness.Modules.Architecture.Application;
using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Architecture.Domain;
using Harness.Persistence.Abstractions.Architecture;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Architecture;

/// <summary>
/// Mutações do Architecture Hub. Cria elementos/relacionamentos/views/metadados, TRAVA elementos,
/// registra PROPOSTAS de agente (state='proposed', separadas do vigente), APLICA uma proposta
/// respeitando os locks e exigindo justificativa em mudança crítica, e faz ROLLBACK a uma versão
/// histórica. Cada mutação estrutural registra uma linha no histórico append-only (auditável).
/// </summary>
public sealed class ArchitectureCommandService(
    IArchitectureStore store, ArchitectureReadModelService readModel, IClock clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IArchitectureStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ArchitectureReadModelService _readModel =
        readModel ?? throw new ArgumentNullException(nameof(readModel));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    // Criação ----------------------------------------------------------------------------------------

    public async Task<ArchitectureElementRecord> CreateElementAsync(
        string tenantId, string? projectId, string kind, string name, string description,
        IReadOnlyDictionary<string, string> properties, CancellationToken token)
    {
        var now = _clock.UtcNow;
        var record = new ArchitectureElementRecord(
            tenantId, NewId(now), projectId, kind, name, description, properties,
            ArchitectureKinds.Implemented, false, 1, null, null, null, now, now);
        await _store.CreateElementAsync(record, token);
        await AppendElementHistoryAsync(record, "add", null, null, now, token);
        return record;
    }

    public async Task<ArchitectureRelationshipRecord> CreateRelationshipAsync(
        string tenantId, string? projectId, string sourceId, string targetId, string kind,
        IReadOnlyDictionary<string, string> properties, CancellationToken token)
    {
        var now = _clock.UtcNow;
        var record = new ArchitectureRelationshipRecord(
            tenantId, NewId(now), projectId, sourceId, targetId, kind, properties,
            ArchitectureKinds.Implemented, 1, null, null, null, now, now);
        await _store.CreateRelationshipAsync(record, token);
        await AppendRelationshipHistoryAsync(record, "add", null, null, now, token);
        return record;
    }

    public async Task<ArchitectureViewRecord> CreateViewAsync(
        string tenantId, string? projectId, string name, string description, string notation,
        IReadOnlyList<string> elementIds, IReadOnlyList<string> relationshipIds,
        IReadOnlyList<string> filterKinds, IReadOnlyList<string> filterTags, CancellationToken token)
    {
        var now = _clock.UtcNow;
        var record = new ArchitectureViewRecord(
            tenantId, NewId(now), projectId, name, description, notation, elementIds, relationshipIds,
            filterKinds, filterTags, now, now);
        await _store.CreateViewAsync(record, token);
        return record;
    }

    public Task UpsertSystemMetadataAsync(ArchitectureSystemMetadataRecord metadata, CancellationToken token) =>
        _store.UpsertSystemMetadataAsync(metadata, token);

    // Lock -------------------------------------------------------------------------------------------

    public async Task<ArchitectureElementRecord?> SetLockAsync(
        string tenantId, string id, bool locked, CancellationToken token)
    {
        var current = await _store.GetElementAsync(tenantId, id, token);
        if (current is null || current.State != ArchitectureKinds.Implemented)
        {
            return null;
        }

        var updated = current with { Locked = locked, UpdatedAt = _clock.UtcNow };
        await _store.ReplaceElementAsync(updated, token);
        return updated;
    }

    // Propostas --------------------------------------------------------------------------------------

    public async Task<ArchitectureProposalRecord> CreateProposalAsync(
        string tenantId, string? projectId, string title,
        IReadOnlyList<ProposedElementInput> elements,
        IReadOnlyList<ProposedRelationshipInput> relationships, CancellationToken token)
    {
        var now = _clock.UtcNow;
        var proposal = new ArchitectureProposalRecord(
            tenantId, NewId(now), projectId, title, "open", null, now, null);
        await _store.CreateProposalAsync(proposal, token);

        foreach (var input in elements)
        {
            var counterpart = input.CounterpartId is null
                ? null
                : await _store.GetElementAsync(tenantId, input.CounterpartId, token);
            var record = new ArchitectureElementRecord(
                tenantId, NewId(now), projectId ?? counterpart?.ProjectId,
                input.Kind ?? counterpart?.Kind ?? "system",
                input.Name ?? counterpart?.Name ?? "(proposed)",
                input.Description ?? counterpart?.Description ?? string.Empty,
                input.Properties ?? counterpart?.Properties ?? new Dictionary<string, string>(),
                ArchitectureKinds.Proposed, false, 1, proposal.Id, input.CounterpartId,
                input.ChangeKind, now, now);
            await _store.CreateElementAsync(record, token);
        }

        foreach (var input in relationships)
        {
            var counterpart = input.CounterpartId is null
                ? null
                : await _store.GetRelationshipAsync(tenantId, input.CounterpartId, token);
            var record = new ArchitectureRelationshipRecord(
                tenantId, NewId(now), projectId ?? counterpart?.ProjectId,
                input.SourceId ?? counterpart?.SourceId ?? string.Empty,
                input.TargetId ?? counterpart?.TargetId ?? string.Empty,
                input.Kind ?? counterpart?.Kind ?? "uses",
                input.Properties ?? counterpart?.Properties ?? new Dictionary<string, string>(),
                ArchitectureKinds.Proposed, 1, proposal.Id, input.CounterpartId, input.ChangeKind, now, now);
            await _store.CreateRelationshipAsync(record, token);
        }

        return proposal;
    }

    // Aplicar ----------------------------------------------------------------------------------------

    public async Task<ArchitectureApplyOutcome> ApplyAsync(
        string tenantId, string proposalId, string? justification, string? actor, CancellationToken token)
    {
        var proposal = await _store.GetProposalAsync(tenantId, proposalId, token);
        if (proposal is null)
        {
            return ArchitectureApplyOutcome.NotFound();
        }

        if (proposal.Status != "open")
        {
            return ArchitectureApplyOutcome.Conflict(proposal.Status);
        }

        var now = _clock.UtcNow;
        var model = await _readModel.BuildProposalAsync(tenantId, proposal.ProjectId, proposalId, token);
        var plan = ArchitectureProposalPlanner.PlanApply(model, proposalId, justification, now);
        if (!plan.ShouldApply)
        {
            return ArchitectureApplyOutcome.JustificationRequired(plan.Result);
        }

        // Elementos: 'add' transiciona a própria linha proposta para vigente; 'modify' atualiza o
        // vigente. 'remove' apaga o vigente. Elementos travados nunca chegam aqui (o planner os pula).
        foreach (var element in plan.ElementUpserts)
        {
            var record = new ArchitectureElementRecord(
                tenantId, element.Id, element.ProjectId, element.Kind, element.Name, element.Description,
                element.Properties, ArchitectureKinds.Implemented, element.Locked, element.Version,
                null, null, null, now, now);
            await _store.ReplaceElementAsync(record, token);
            await AppendElementHistoryAsync(record, element.ChangeKind ?? "modify", actor, justification, now, token);
        }

        foreach (var id in plan.ElementRemovals)
        {
            var snapshot = await _store.GetElementAsync(tenantId, id, token);
            await _store.DeleteElementAsync(tenantId, id, token);
            if (snapshot is not null)
            {
                await AppendElementHistoryAsync(snapshot with { Version = snapshot.Version + 1 }, "remove", actor, justification, now, token);
            }
        }

        foreach (var relationship in plan.RelationshipUpserts)
        {
            var record = new ArchitectureRelationshipRecord(
                tenantId, relationship.Id, relationship.ProjectId, relationship.SourceId,
                relationship.TargetId, relationship.Kind, relationship.Properties,
                ArchitectureKinds.Implemented, relationship.Version, null, null, null, now, now);
            await _store.ReplaceRelationshipAsync(record, token);
            await AppendRelationshipHistoryAsync(record, relationship.ChangeKind ?? "modify", actor, justification, now, token);
        }

        foreach (var id in plan.RelationshipRemovals)
        {
            await _store.DeleteRelationshipAsync(tenantId, id, token);
        }

        // Documentos afetados dos sistemas aplicados viram RASCUNHO (persistência do metadado).
        var appliedCounterparts = plan.Result.AppliedChanges
            .Where(c => c.CounterpartId is not null)
            .Select(c => c.CounterpartId!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var systemId in appliedCounterparts)
        {
            var meta = await _store.GetSystemMetadataAsync(tenantId, systemId, token);
            if (meta is null || meta.Documents.All(d => d.State == "draft"))
            {
                continue;
            }

            var drafted = meta.Documents
                .Select(d => d.State == "draft" ? d : d with { State = "draft" })
                .ToArray();
            await _store.UpsertSystemMetadataAsync(meta with { Documents = drafted, UpdatedAt = now }, token);
        }

        await CleanupProposedRowsAsync(tenantId, proposal.ProjectId, proposalId, token);
        await _store.ReplaceProposalAsync(
            proposal with { Status = "applied", Justification = justification, AppliedAt = now }, token);

        return ArchitectureApplyOutcome.Applied(plan.Result);
    }

    // Rollback ---------------------------------------------------------------------------------------

    public async Task<ArchitectureRollbackOutcome> RollbackElementAsync(
        string tenantId, string entityId, int targetVersion, string? actor, string? justification,
        CancellationToken token)
    {
        var history = await _store.ListHistoryAsync(tenantId, entityId, 500, token);
        var target = history.FirstOrDefault(h =>
            h.EntityType == "element" && h.Version == targetVersion);
        if (target is null)
        {
            return ArchitectureRollbackOutcome.NotFound();
        }

        var snapshot = JsonSerializer.Deserialize<ArchitectureElementRecord>(target.SnapshotJson, JsonOptions);
        if (snapshot is null)
        {
            return ArchitectureRollbackOutcome.NotFound();
        }

        var now = _clock.UtcNow;
        var current = await _store.GetElementAsync(tenantId, entityId, token);
        var newVersion = (current?.Version ?? snapshot.Version) + 1;
        var restored = snapshot with
        {
            TenantId = tenantId,
            Id = entityId,
            State = ArchitectureKinds.Implemented,
            Locked = current?.Locked ?? snapshot.Locked,
            Version = newVersion,
            ProposalId = null,
            CounterpartId = null,
            ChangeKind = null,
            UpdatedAt = now,
        };

        if (current is null)
        {
            await _store.CreateElementAsync(restored with { CreatedAt = now }, token);
        }
        else
        {
            await _store.ReplaceElementAsync(restored, token);
        }

        await AppendElementHistoryAsync(restored, "rollback", actor, justification, now, token);
        return ArchitectureRollbackOutcome.RolledBack(restored);
    }

    // Internos ---------------------------------------------------------------------------------------

    private async Task CleanupProposedRowsAsync(
        string tenantId, string? projectId, string proposalId, CancellationToken token)
    {
        var elements = await _store.ListElementsAsync(tenantId, projectId, ArchitectureKinds.Proposed, null, 500, token);
        foreach (var element in elements.Where(e => e.ProposalId == proposalId))
        {
            await _store.DeleteElementAsync(tenantId, element.Id, token);
        }

        var relationships = await _store.ListRelationshipsAsync(tenantId, projectId, ArchitectureKinds.Proposed, null, 500, token);
        foreach (var relationship in relationships.Where(r => r.ProposalId == proposalId))
        {
            await _store.DeleteRelationshipAsync(tenantId, relationship.Id, token);
        }
    }

    private Task AppendElementHistoryAsync(
        ArchitectureElementRecord element, string changeKind, string? actor, string? justification,
        DateTimeOffset now, CancellationToken token) =>
        _store.AppendHistoryAsync(new ArchitectureHistoryRecord(
            element.TenantId, NewId(now), "element", element.Id, element.Version,
            JsonSerializer.Serialize(element, JsonOptions), changeKind, actor, justification, now), token);

    private Task AppendRelationshipHistoryAsync(
        ArchitectureRelationshipRecord relationship, string changeKind, string? actor, string? justification,
        DateTimeOffset now, CancellationToken token) =>
        _store.AppendHistoryAsync(new ArchitectureHistoryRecord(
            relationship.TenantId, NewId(now), "relationship", relationship.Id, relationship.Version,
            JsonSerializer.Serialize(relationship, JsonOptions), changeKind, actor, justification, now), token);

    private static string NewId(DateTimeOffset now) => UlidValue.New(now).ToString();
}

/// <summary>Um item de elemento proposto por um agente (ARC-05).</summary>
public sealed record ProposedElementInput(
    string ChangeKind, string? CounterpartId, string? Kind, string? Name, string? Description,
    IReadOnlyDictionary<string, string>? Properties);

/// <summary>Um item de relacionamento proposto por um agente (ARC-05).</summary>
public sealed record ProposedRelationshipInput(
    string ChangeKind, string? CounterpartId, string? SourceId, string? TargetId, string? Kind,
    IReadOnlyDictionary<string, string>? Properties);

public sealed record ArchitectureApplyOutcome(
    string Status, ArchitectureApplyResultContract? Result, string? ConflictStatus)
{
    public static ArchitectureApplyOutcome Applied(ArchitectureApplyResultContract result) =>
        new("applied", result, null);
    public static ArchitectureApplyOutcome JustificationRequired(ArchitectureApplyResultContract result) =>
        new("justification_required", result, null);
    public static ArchitectureApplyOutcome NotFound() => new("not_found", null, null);
    public static ArchitectureApplyOutcome Conflict(string currentStatus) =>
        new("conflict", null, currentStatus);
}

public sealed record ArchitectureRollbackOutcome(string Status, ArchitectureElementRecord? Element)
{
    public static ArchitectureRollbackOutcome RolledBack(ArchitectureElementRecord element) =>
        new("rolled_back", element);
    public static ArchitectureRollbackOutcome NotFound() => new("not_found", null);
}
