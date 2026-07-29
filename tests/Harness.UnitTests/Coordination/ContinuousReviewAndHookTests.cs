using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class ContinuousReviewPolicyTests
{
    /// <summary>Gate da fase: bloqueio do revisor interrompe ANTES da submissão.</summary>
    [Fact]
    public void ABlockingRemarkStopsTheRoundBeforeSubmission()
    {
        var decision = ContinuousReviewPolicy.Evaluate(
            RiskTier.High, "larissa", "bruno",
            [new ReviewRemark(ReviewRemarkKind.Block, "A abordagem quebra o contrato público.")]);

        Assert.False(decision.MaySubmit);
        Assert.Equal(ReviewRemarkKind.Block, decision.BlockedBy);
        Assert.Equal(ContinuousReviewPolicy.ReasonBlockedByReviewer, decision.ReasonCode);
    }

    [Fact]
    public void AsidesAndConcernsAreRecordedWithoutInterrupting()
    {
        var decision = ContinuousReviewPolicy.Evaluate(
            RiskTier.High, "larissa", "bruno",
            [
                new ReviewRemark(ReviewRemarkKind.Aside, "Dá para simplificar depois."),
                new ReviewRemark(ReviewRemarkKind.Concern, "Isso pode ficar lento com volume.")
            ]);

        // Revisor que barra tudo é indistinguível de portão fechado, e o executor para de ler.
        Assert.True(decision.MaySubmit);
        Assert.Null(decision.BlockedBy);
        Assert.Equal(2, decision.Remarks.Count);
    }

    /// <summary>Gate da fase: o revisor tem conta distinta do executor.</summary>
    [Fact]
    public void AnAgentReviewingItselfIsNotAReviewer()
    {
        var decision = ContinuousReviewPolicy.Evaluate(RiskTier.High, "larissa", "larissa", []);

        Assert.False(decision.MaySubmit);
        Assert.Equal(ContinuousReviewPolicy.ReasonReviewerIsTheExecutor, decision.ReasonCode);
    }

    [Fact]
    public void SelfReviewIsRefusedEvenWhenPairingIsOnlyOptional()
    {
        var decision = ContinuousReviewPolicy.Evaluate(RiskTier.Medium, "larissa", "LARISSA", []);

        Assert.False(decision.MaySubmit);
        Assert.Equal(ContinuousReviewPolicy.ReasonReviewerIsTheExecutor, decision.ReasonCode);
    }

    [Theory]
    [InlineData(RiskTier.Critical, ContinuousReviewMode.Required)]
    [InlineData(RiskTier.High, ContinuousReviewMode.Required)]
    [InlineData(RiskTier.Medium, ContinuousReviewMode.Optional)]
    [InlineData(RiskTier.Low, ContinuousReviewMode.Off)]
    public void RigorFollowsRiskSoPairingDoesNotBecomeATax(RiskTier risk, ContinuousReviewMode expected)
    {
        Assert.Equal(expected, ContinuousReviewPolicy.ModeFor(risk));
    }

    [Fact]
    public void HighRiskWithoutAPairedReviewerCannotSubmit()
    {
        var decision = ContinuousReviewPolicy.Evaluate(RiskTier.Critical, "larissa", null, []);

        Assert.False(decision.MaySubmit);
        Assert.Equal(ContinuousReviewPolicy.ReasonReviewerMissing, decision.ReasonCode);
    }

    [Fact]
    public void LowRiskSubmitsWithoutAReviewerBecausePairingIsOff()
    {
        var decision = ContinuousReviewPolicy.Evaluate(RiskTier.Low, "larissa", null, []);

        Assert.True(decision.MaySubmit);
        Assert.Equal(ContinuousReviewPolicy.ReasonMaySubmit, decision.ReasonCode);
    }

    [Fact]
    public void ABlockWinsOverAnyNumberOfHarmlessRemarks()
    {
        var decision = ContinuousReviewPolicy.Evaluate(
            RiskTier.High, "larissa", "bruno",
            [
                new ReviewRemark(ReviewRemarkKind.Aside, "a"),
                new ReviewRemark(ReviewRemarkKind.Block, "b"),
                new ReviewRemark(ReviewRemarkKind.Concern, "c")
            ]);

        Assert.False(decision.MaySubmit);
    }
}

public sealed class WorktreeHookPolicyTests
{
    /// <summary>Gate da fase: o hook gerado corresponde ao card.</summary>
    [Fact]
    public void TheGeneratedPlanMatchesTheCardRiskAndBoundaries()
    {
        string[] denied = ["frontend/src/features/x", "src/Modules/Outro"];
        var plan = WorktreeHookPolicy.Generate(RiskTier.High, "csharp", ["src/Modules/Meu"], denied);

        Assert.True(WorktreeHookPolicy.MatchesCard(plan, RiskTier.High, denied));
        // Hook que não cobre a fronteira declarada é falsa sensação de proteção.
        Assert.False(WorktreeHookPolicy.MatchesCard(plan, RiskTier.Low, denied));
        Assert.False(WorktreeHookPolicy.MatchesCard(plan, RiskTier.High, ["outro/escopo"]));
    }

    [Fact]
    public void SecretScanningIsPresentAtEveryRigorBecauseHistoryDoesNotForget()
    {
        foreach (var risk in Enum.GetValues<RiskTier>())
        {
            var plan = WorktreeHookPolicy.Generate(risk, "csharp", ["src/X"], []);
            Assert.Contains(plan.Hooks, hook => hook.Command.Contains("scan-secrets", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheBoundaryBecomesAnEditTimeGuardNotARecommendation()
    {
        var plan = WorktreeHookPolicy.Generate(
            RiskTier.Low, "csharp", ["src/Meu"], ["src/Alheio"]);

        var guard = Assert.Single(plan.Hooks, hook => hook.Event == "pre-edit");
        Assert.Contains("src/Alheio", guard.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutBoundariesThereIsNoScopeGuardInvented()
    {
        var plan = WorktreeHookPolicy.Generate(RiskTier.Low, "csharp", ["src/Meu"], []);
        Assert.DoesNotContain(plan.Hooks, hook => hook.Event == "pre-edit");
    }

    [Theory]
    [InlineData(RiskTier.Critical, HookRigor.Strict)]
    [InlineData(RiskTier.High, HookRigor.Strict)]
    [InlineData(RiskTier.Medium, HookRigor.Standard)]
    [InlineData(RiskTier.Low, HookRigor.Minimal)]
    public void RigorFollowsRiskSoAgentsDoNotLearnToBypassHooks(RiskTier risk, HookRigor expected)
    {
        Assert.Equal(expected, WorktreeHookPolicy.RigorFor(risk));
    }

    [Fact]
    public void MinimalRigorDoesNotRunTheFullVerification()
    {
        var plan = WorktreeHookPolicy.Generate(RiskTier.Low, "csharp", ["src/X"], []);

        // Hook estrito numa mudança visual transforma cada salvamento numa espera.
        Assert.DoesNotContain(plan.Hooks, hook => hook.Command.Contains("verify.sh", StringComparison.Ordinal));
    }

    [Fact]
    public void StrictRigorRequiresTestsAlongsideBehaviourChanges()
    {
        var plan = WorktreeHookPolicy.Generate(RiskTier.Critical, "csharp", ["src/X"], []);

        Assert.Contains(plan.Hooks, hook => hook.Command.Contains("verify.sh", StringComparison.Ordinal));
        Assert.Contains(plan.Hooks, hook => hook.Command.Contains("guard-test-coverage", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLanguageDecidesTheFormatterAndTheTestCommand()
    {
        var csharp = WorktreeHookPolicy.Generate(RiskTier.Medium, "csharp", ["src/X"], []);
        var typescript = WorktreeHookPolicy.Generate(RiskTier.Medium, "typescript", ["frontend/src"], []);

        Assert.Contains(csharp.Hooks, hook => hook.Command.Contains("dotnet.sh format", StringComparison.Ordinal));
        Assert.Contains(typescript.Hooks, hook => hook.Command.Contains("npm run lint", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryHookExplainsWhyItExists()
    {
        var plan = WorktreeHookPolicy.Generate(RiskTier.Critical, "csharp", ["src/X"], ["src/Y"]);

        // Hook sem justificativa é obstáculo; com justificativa é regra que o agente entende.
        Assert.All(plan.Hooks, hook => Assert.False(string.IsNullOrWhiteSpace(hook.Rationale)));
    }
}
