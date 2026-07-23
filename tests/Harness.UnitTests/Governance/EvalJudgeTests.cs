using Harness.Modules.Governance.Judging;

namespace Harness.UnitTests.Governance;

public sealed class EvalJudgeTests
{
    private static EvalJudgeRequest Request(
        bool criteria = true, bool diff = true, bool evidence = true, bool testsPass = true) =>
        new(
            criteria ? ["Endpoint returns 200"] : [],
            diff ? "diff --git a b" : string.Empty,
            evidence ? ["evidence://run/1"] : [],
            [new EvalJudgeTestResult("gate", testsPass, "evidence://gate")]);

    [Fact]
    public async Task DeterministicJudgePassesWhenAllChecksSatisfied()
    {
        var verdict = await ((IEvalJudge)new DeterministicEvalJudge()).JudgeAsync(Request());
        Assert.True(verdict.Passed);
        Assert.Equal(1m, verdict.Score);
        Assert.Equal(DeterministicEvalJudge.ProviderName, verdict.Provider);
    }

    [Fact]
    public void DeterministicJudgeFailsAndScoresProportionallyOnMissingChecks()
    {
        var verdict = DeterministicEvalJudge.Judge(Request(evidence: false, testsPass: false));
        Assert.False(verdict.Passed);
        Assert.Equal(0.5m, verdict.Score);
        Assert.Contains("evidence", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("failing tests", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FactoryDefaultsToDeterministicWhenLlmDisabled()
    {
        var factory = new EvalJudgeFactory(new EvalJudgeOptions());
        Assert.False(factory.LlmJudgeAvailable);
        Assert.IsType<DeterministicEvalJudge>(factory.Create());
    }

    [Fact]
    public void FactoryFallsBackToDeterministicWhenTransportMissingEvenIfEnabled()
    {
        var factory = new EvalJudgeFactory(new EvalJudgeOptions { UseLlmJudge = true });
        Assert.False(factory.LlmJudgeAvailable);
        Assert.IsType<DeterministicEvalJudge>(factory.Create());
    }

    [Fact]
    public async Task FactorySelectsLlmJudgeWhenEnabledWithTransport()
    {
        var factory = new EvalJudgeFactory(
            new EvalJudgeOptions { UseLlmJudge = true, Provider = "fake-llm" },
            (_, _) => Task.FromResult("{\"passed\":true,\"score\":0.9,\"reason\":\"ok\"}"));
        Assert.True(factory.LlmJudgeAvailable);
        var judge = factory.Create();
        Assert.IsType<LlmEvalJudge>(judge);

        var verdict = await judge.JudgeAsync(Request());
        Assert.True(verdict.Passed);
        Assert.Equal(0.9m, verdict.Score);
        Assert.Equal("fake-llm", verdict.Provider);
    }

    [Fact]
    public async Task LlmJudgeRejectsEmptyTransportResponse()
    {
        var judge = new LlmEvalJudge("fake-llm", (_, _) => Task.FromResult(string.Empty));
        await Assert.ThrowsAsync<EvalJudgeUnavailableException>(() => judge.JudgeAsync(Request()));
    }
}
