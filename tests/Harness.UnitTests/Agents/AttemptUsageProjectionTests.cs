using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.SharedKernel.Providers;

namespace Harness.UnitTests.Agents;

/// <summary>
/// O consumo da tentativa precisa chegar ao quadro, à Central de Entregas e à auditoria. A regra
/// difícil não é somar: é NÃO inventar número. Uma CLI que não expõe uso grava zero no ledger e
/// marca o desfecho com `usage_unknown`; somar esse zero produziria um custo falso de US$ 0,00,
/// que é indistinguível de uma execução realmente gratuita.
/// </summary>
public sealed class AttemptUsageProjectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-02T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void LiveSessionUsageWins()
    {
        var usage = ChiefBacklogLoopService.ToAttemptUsage(
            Execution(durationMs: 4_000, new ExternalAgentUsage(120, null, 45, 0.5m, 1)),
            [Invocation(inputTokens: 999, outputTokens: 999, costUsd: 9m, durationMs: 99, "completed")]);

        Assert.NotNull(usage);
        Assert.Equal(4_000, usage.DurationMs);
        Assert.Equal(120, usage.TokensInput);
        Assert.Equal(45, usage.TokensOutput);
        Assert.Equal(0.5m, usage.CostUsd);
    }

    [Fact]
    public void DurableLedgerCoversTheHarvestAfterTheSessionIsGone()
    {
        // O caso comum: a colheita acontece num ciclo posterior ao fim do processo (ou depois de
        // um reinício do Host), quando a sessão viva já não existe.
        var usage = ChiefBacklogLoopService.ToAttemptUsage(
            execution: null,
            [
                Invocation(1_000, 400, 0.75m, 12_000, "completed"),
                Invocation(500, 100, 0.25m, 3_000, "completed"),
            ]);

        Assert.NotNull(usage);
        Assert.Equal(15_000, usage.DurationMs);
        Assert.Equal(1_500, usage.TokensInput);
        Assert.Equal(500, usage.TokensOutput);
        Assert.Equal(1.00m, usage.CostUsd);
    }

    [Fact]
    public void UsageUnknownNeverBecomesAMeasuredZero()
    {
        var usage = ChiefBacklogLoopService.ToAttemptUsage(
            execution: null,
            [Invocation(0, 0, 0m, 8_000, "completed|usage_unknown")]);

        Assert.NotNull(usage);
        // A duração é cronometrada pelo adapter e vale mesmo sem uso exposto.
        Assert.Equal(8_000, usage.DurationMs);
        // Tokens e custo continuam DESCONHECIDOS: o store preserva o valor anterior em vez de
        // gravar zero.
        Assert.Null(usage.TokensInput);
        Assert.Null(usage.TokensOutput);
        Assert.Null(usage.CostUsd);
    }

    [Fact]
    public void KnownAndUnknownInvocationsInTheSameAttemptOnlySumTheKnownOnes()
    {
        var usage = ChiefBacklogLoopService.ToAttemptUsage(
            execution: null,
            [
                Invocation(700, 300, 0.40m, 5_000, "completed"),
                Invocation(0, 0, 0m, 2_000, "completed|usage_unknown"),
            ]);

        Assert.NotNull(usage);
        Assert.Equal(7_000, usage.DurationMs);
        Assert.Equal(700, usage.TokensInput);
        Assert.Equal(300, usage.TokensOutput);
        Assert.Equal(0.40m, usage.CostUsd);
    }

    [Fact]
    public void NothingMeasuredProducesNoProjectionAtAll()
    {
        Assert.Null(ChiefBacklogLoopService.ToAttemptUsage(execution: null, []));
    }

    private static ExternalAgentRunResult Execution(long durationMs, ExternalAgentUsage? usage) =>
        new(
            "claude-code",
            "worker-alias",
            SessionId: null,
            ExternalAgentRunStatus.Completed,
            FinalMessage: "done",
            Deltas: [],
            usage,
            ExitCode: 0,
            FailureCode: null,
            durationMs);

    private static ModelInvocationRecord Invocation(
        int inputTokens, int outputTokens, decimal costUsd, long durationMs, string outcome) =>
        new(
            "01ARZ3NDEKTSV4RRFFQ69G5FA1",
            "01ARZ3NDEKTSV4RRFFQ69G5FA2",
            "01ARZ3NDEKTSV4RRFFQ69G5FA3",
            "01ARZ3NDEKTSV4RRFFQ69G5FA4",
            "01ARZ3NDEKTSV4RRFFQ69G5FA5",
            "anthropic",
            "opus",
            "worker-alias",
            inputTokens,
            outputTokens,
            costUsd,
            durationMs,
            outcome,
            Now);
}
