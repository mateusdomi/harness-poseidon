using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.IntegrationTests.Persistence;

/// <summary>
/// Fase 0A1: contrato provider-neutro do compromisso de materialização. Rodar o MESMO roteiro em
/// SQLite e PostgreSQL é o que impede a divergência semântica que o gate do bloco proíbe — um
/// banco aceitando uma transição que o outro recusa seria uma garantia falsa.
/// </summary>
public static class PlanMaterializationStoreBehavior
{
    public static async Task AssertAsync(
        IPlanMaterializationStore store,
        string tenantId,
        string projectId,
        string demandId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var now = new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);
        var request = new PlanMaterializationRequest(
            ["A tela lista o catálogo."],
            "backend-performance",
            new PlanMaterializationSurfaces(Frontend: true, Backend: false));

        // Pedido idempotente por (tenant, demanda): o segundo NÃO reabre nem reescreve a intenção.
        var created = await store.RequestAsync(
            new PlanMaterializationRequestCommand(
                tenantId, demandId, projectId, "01ARZ3NDEKTSV4RRFFQ69G5FC1", request, now),
            cancellationToken);
        Assert.Equal(PlanMaterializationStatus.Pending, created.Status);
        Assert.Equal(0, created.AttemptCount);
        Assert.Equal(["A tela lista o catálogo."], created.Request.AcceptanceCriteria);
        Assert.Equal("backend-performance", created.Request.Specialty);
        Assert.True(created.Request.Surfaces?.Frontend);
        Assert.False(created.Request.Surfaces?.Backend);
        Assert.Null(created.Request.Surfaces?.Decision);

        var replay = await store.RequestAsync(
            new PlanMaterializationRequestCommand(
                tenantId, demandId, projectId, null,
                new PlanMaterializationRequest(["Outra coisa."]), now.AddMinutes(1)),
            cancellationToken);
        Assert.Equal(PlanMaterializationStatus.Pending, replay.Status);
        Assert.Equal(["A tela lista o catálogo."], replay.Request.AcceptanceCriteria);

        // Aquisição: um dono por vez. O segundo pedido concorrente não adquire nada.
        var claimed = await store.TryBeginAsync(
            new PlanMaterializationBeginCommand(
                tenantId, demandId, "owner-a", now.AddMinutes(2), TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.NotNull(claimed);
        Assert.Equal(PlanMaterializationStatus.Processing, claimed!.Status);
        Assert.Equal(1, claimed.AttemptCount);
        Assert.Equal("owner-a", claimed.OwnerId);
        Assert.Null(await store.TryBeginAsync(
            new PlanMaterializationBeginCommand(
                tenantId, demandId, "owner-b", now.AddMinutes(3), TimeSpan.FromMinutes(5)),
            cancellationToken));

        // Fencing: um dono antigo (ou uma tentativa antiga) não conclui e não falha o trabalho.
        Assert.False(await store.TryCompleteAsync(
            new PlanMaterializationCompleteCommand(
                tenantId, demandId, "owner-b", 1, "01ARZ3NDEKTSV4RRFFQ69G5FC2", 3, 3,
                now.AddMinutes(4)),
            cancellationToken));
        Assert.False(await store.TryCompleteAsync(
            new PlanMaterializationCompleteCommand(
                tenantId, demandId, "owner-a", 99, "01ARZ3NDEKTSV4RRFFQ69G5FC2", 3, 3,
                now.AddMinutes(4)),
            cancellationToken));

        // Falha visível e retentável, com o código preservado.
        Assert.True(await store.TryFailAsync(
            new PlanMaterializationFailCommand(
                tenantId, demandId, "owner-a", 1, "TransientFailure", now.AddMinutes(5)),
            cancellationToken));
        var failed = await store.GetAsync(tenantId, demandId, cancellationToken);
        Assert.Equal(PlanMaterializationStatus.Failed, failed!.Status);
        Assert.Equal("TransientFailure", failed.LastError);

        // Lease vencido: só então outro dono assume o trabalho abandonado.
        var reclaimed = await store.TryBeginAsync(
            new PlanMaterializationBeginCommand(
                tenantId, demandId, "owner-c", now.AddMinutes(20), TimeSpan.FromMinutes(5)),
            cancellationToken);
        Assert.Equal(2, reclaimed!.AttemptCount);
        Assert.True(await store.TryCompleteAsync(
            new PlanMaterializationCompleteCommand(
                tenantId, demandId, "owner-c", 2, "01ARZ3NDEKTSV4RRFFQ69G5FC2", 3, 3,
                now.AddMinutes(21)),
            cancellationToken));
        var completed = await store.GetAsync(tenantId, demandId, cancellationToken);
        Assert.Equal(PlanMaterializationStatus.Completed, completed!.Status);
        Assert.Equal(3, completed.ExpectedCards);
        Assert.Equal(3, completed.MaterializedCards);
        Assert.Null(completed.LastError);
        Assert.NotNull(completed.CompletedAt);

        // Um compromisso concluído NÃO é reaberto por engano: só o reconciliador pode pedir isso,
        // e apenas depois de comprovar que o board divergiu.
        Assert.Null(await store.TryBeginAsync(
            new PlanMaterializationBeginCommand(
                tenantId, demandId, "owner-d", now.AddMinutes(30), TimeSpan.FromMinutes(5)),
            cancellationToken));
        var reopened = await store.TryBeginAsync(
            new PlanMaterializationBeginCommand(
                tenantId, demandId, "owner-d", now.AddMinutes(31), TimeSpan.FromMinutes(5),
                AllowCompleted: true),
            cancellationToken);
        Assert.Equal(PlanMaterializationStatus.Processing, reopened!.Status);
        Assert.True(await store.TryCompleteAsync(
            new PlanMaterializationCompleteCommand(
                tenantId, demandId, "owner-d", reopened.AttemptCount,
                "01ARZ3NDEKTSV4RRFFQ69G5FC2", 3, 3, now.AddMinutes(32)),
            cancellationToken));

        // Varredura do reconciliador: página estável e cursor exclusivo.
        var page = await store.ListForReconciliationAsync(null, 50, cancellationToken);
        Assert.Contains(page, record => record.DemandId == demandId);
        var after = await store.ListForReconciliationAsync(
            new PlanMaterializationCursor(tenantId, demandId), 50, cancellationToken);
        Assert.DoesNotContain(after, record => record.DemandId == demandId);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ListForReconciliationAsync(null, 0, cancellationToken));

        // Demanda desconhecida não inventa registro.
        Assert.Null(await store.GetAsync(
            tenantId, "01ARZ3NDEKTSV4RRFFQ69G5FC9", cancellationToken));
    }
}
