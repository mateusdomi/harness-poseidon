using Harness.Modules.Coordination.Application;
using Harness.Modules.Governance.Evaluation;
using Harness.Modules.Governance.Ledger;
using Harness.Modules.Governance.Memory;
using Harness.Modules.Providers.Application;
using Harness.Modules.Providers.Contracts;
using Harness.SharedKernel.Auditing;
using Harness.SharedKernel.Memory;

namespace Harness.UnitTests.EndToEnd;

public sealed class EndToEndHomologationTests
{
    [Fact]
    public async Task ValidateFullThirteenPhasePipelineHomologationFlow()
    {
        var tenantId = "tenant-homologation-13";

        // Phase 3: Capacity Manager & Model Router
        var capacityManager = new CapacityManager();
        var account = new SimpleAccountSpec("acc-1", "openai", ["worker"], [], 5, 0, 1);
        var req = new ModelRoutingRequest("worker", "chat", "gpt-4o", "low", "acc-1", false, [], DateTimeOffset.UtcNow);
        var selection = new ScheduledAccountSelection(
            "acc-1",
            "scheduler.selected",
            [new ScheduledAccountCandidate("acc-1", true, "account.eligible", 1)],
            []);
        var decision = ModelRouter.Route([account], selection, req);
        Assert.NotNull(decision);
        Assert.Equal("acc-1", decision.SelectedAlias);

        // Phase 5: Evaluation Service
        var evalService = new EvaluationService();
        var score = evalService.CalculateCompositeScore(1.0, 0, 0, 0, 0, true);
        Assert.Equal(1.0, score);
        var (ciLow, ciHigh) = evalService.CalculateConfidenceInterval(10, 10);
        Assert.True(ciLow > 0.60 && ciHigh <= 1.0);

        // Phase 6: Semantic Memory & Context Builder
        var contextBuilder = new ContextBuilder();
        var snapshot = contextBuilder.BuildSnapshot(tenantId, "task-13", "checksum-13", [
            new("doc-1", "ADR 001", "Architecture ADR content", "ADR-001#L1-10", 50)
        ], maxTokens: 4000);
        Assert.NotNull(snapshot);
        Assert.NotEmpty(snapshot.Hash);

        // Phase 8: Multimodal Intake
        var intake = new MultimodalIntakeService();
        var intakeResult = await intake.ProcessAttachmentAsync(tenantId, "solicitation-13", "arch.png", "image/png", [1, 2, 3, 4]);
        Assert.True(intakeResult.IsAllowedType);
        Assert.Equal("passed", intakeResult.SecurityScanStatus);

        // Phase 10: Scale Dispatcher
        var buffer = new CardPrioritizedBuffer();
        buffer.Enqueue("card-13", "worker", CardPriority.High, DateTimeOffset.UtcNow);
        var dispatcher = new ScaleDispatcher();
        var dispatchResult = dispatcher.Dispatch(buffer, globalMaxConcurrency: 5, currentRunningCount: 0);
        Assert.Single(dispatchResult.DispatchedWorkerCards);

        // Phase 12: Ledger Reconciliation
        var reconciliation = new LedgerReconciliationService();
        var occurredAt = DateTimeOffset.UtcNow;
        const string payload = """{"turnId":"turn-1","cardId":"card-13"}""";
        var eventHash = AuditChainHash.Compute(
            AuditChainHash.Genesis,
            tenantId,
            1,
            "card.created",
            payload,
            occurredAt);
        var ledgerResult = reconciliation.Reconcile(tenantId, [
            new(
                1,
                tenantId,
                "card.created",
                payload,
                AuditChainHash.Genesis,
                eventHash,
                occurredAt)
        ]);
        Assert.True(ledgerResult.IsChainValid);
    }
}
