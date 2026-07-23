using System.Text.Json;
using Harness.Modules.Architecture.Application;
using Harness.Modules.Architecture.Contracts;
using Harness.Modules.Architecture.Domain;
using Harness.Persistence.Abstractions.Architecture;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Architecture;

/// <summary>
/// Mutações das áreas estendidas do Architecture Hub: registra DESCOBERTAS de sistemas existentes com
/// confiança + evidência + perguntas (ARC-06), grava ADRs/padrões reutilizáveis (ARC-08) e congela a
/// arquitetura aprovada como BASELINE da entrega, atualiza o AS-IS de produção e compara proposta ×
/// implementação no encerramento (ARC-10). Nenhuma mutação decide sozinha: descobertas e insights são
/// propostas para um humano confirmar.
/// </summary>
public sealed class ArchitectureHubCommandService(
    IArchitectureStore store, ArchitectureReadModelService readModel, IClock clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IArchitectureStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ArchitectureReadModelService _readModel =
        readModel ?? throw new ArgumentNullException(nameof(readModel));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    // ARC-06 -----------------------------------------------------------------------------------------

    public async Task<ArchitectureDiscoveryRecord> CreateDiscoveryAsync(
        string tenantId, string? projectId, string? systemId, string subjectName, string sourceKind,
        string field, string value, string confidence, string evidence,
        IReadOnlyList<string> pendingQuestions, CancellationToken token)
    {
        var now = _clock.UtcNow;
        var record = new ArchitectureDiscoveryRecord(
            tenantId, NewId(now), projectId, systemId, subjectName, sourceKind, field, value,
            confidence, evidence, pendingQuestions, ArchitectureHubKinds.StatusOpen, now, now);
        await _store.CreateDiscoveryAsync(record, token);
        return record;
    }

    public async Task<ArchitectureDiscoveryRecord?> SetDiscoveryStatusAsync(
        string tenantId, string id, string status, CancellationToken token)
    {
        var current = await _store.GetDiscoveryAsync(tenantId, id, token);
        if (current is null)
        {
            return null;
        }

        var updated = current with { Status = status, UpdatedAt = _clock.UtcNow };
        await _store.ReplaceDiscoveryAsync(updated, token);
        return updated;
    }

    // ARC-08 -----------------------------------------------------------------------------------------

    public async Task<ArchitecturePatternRecord> CreatePatternAsync(
        string tenantId, string? projectId, string kind, string title, string status, string context,
        string body, string? problem, string consequences, IReadOnlyList<string> tags,
        string? supersedesId, string? documentId, CancellationToken token)
    {
        var now = _clock.UtcNow;
        var record = new ArchitecturePatternRecord(
            tenantId, NewId(now), projectId, kind, title, status, context, body, problem, consequences,
            tags, supersedesId, documentId, now, now);
        await _store.UpsertPatternAsync(record, token);
        return record;
    }

    // ARC-10 -----------------------------------------------------------------------------------------

    /// <summary>
    /// Congela o modelo VIGENTE do projeto (a arquitetura APROVADA, já aplicada) como BASELINE da entrega.
    /// Opcionalmente referencia a proposta aprovada que a originou. Nada é inventado: a foto é do que existe.
    /// </summary>
    public async Task<ArchitectureBaselineRecord> CreateBaselineAsync(
        string tenantId, string projectId, string title, string? proposalId, CancellationToken token)
    {
        var now = _clock.UtcNow;
        var model = await _readModel.BuildImplementedAsync(tenantId, projectId, token);
        var snapshot = BaselineComparator.Snapshot(model);
        var record = new ArchitectureBaselineRecord(
            tenantId, NewId(now), projectId, ArchitectureHubKinds.StatusBaseline, title, proposalId,
            JsonSerializer.Serialize(snapshot, JsonOptions), null, now, now);
        await _store.UpsertBaselineAsync(record, token);
        return record;
    }

    /// <summary>Produção atualizou o AS-IS: congela o modelo vigente atual como as-built da baseline.</summary>
    public async Task<ArchitectureBaselineRecord?> UpdateAsBuiltAsync(
        string tenantId, string id, CancellationToken token)
    {
        var current = await _store.GetBaselineAsync(tenantId, id, token);
        if (current is null)
        {
            return null;
        }

        var model = await _readModel.BuildImplementedAsync(tenantId, current.ProjectId, token);
        var snapshot = BaselineComparator.Snapshot(model);
        var updated = current with
        {
            Status = ArchitectureHubKinds.StatusAsBuilt,
            AsBuiltSnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            UpdatedAt = _clock.UtcNow,
        };
        await _store.UpsertBaselineAsync(updated, token);
        return updated;
    }

    /// <summary>Encerramento: marca a baseline como fechada (a comparação proposta × implementação é lida).</summary>
    public async Task<ArchitectureBaselineRecord?> CloseBaselineAsync(
        string tenantId, string id, CancellationToken token)
    {
        var current = await _store.GetBaselineAsync(tenantId, id, token);
        if (current is null)
        {
            return null;
        }

        var updated = current with { Status = ArchitectureHubKinds.StatusClosed, UpdatedAt = _clock.UtcNow };
        await _store.UpsertBaselineAsync(updated, token);
        return updated;
    }

    private static string NewId(DateTimeOffset now) => UlidValue.New(now).ToString();
}
