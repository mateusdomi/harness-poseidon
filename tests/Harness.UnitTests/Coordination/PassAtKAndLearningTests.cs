using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

public sealed class PassAtKPolicyTests
{
    private static readonly CapabilityPair Pair = new("larissa", "balanced", "tarefa");
    private static readonly CapabilityPair Other = new("outra", "balanced", "tarefa");

    /// <summary>Gate da fase: pass@k correto sobre histórico simulado.</summary>
    [Fact]
    public void PassAtKCountsCardsResolvedNotAttemptsSpent()
    {
        // card-1 resolve na 2a; card-2 nunca; card-3 resolve de primeira.
        var measurement = PassAtKPolicy.Measure(Pair,
        [
            new AttemptOutcome(Pair, "card-1", 1, false),
            new AttemptOutcome(Pair, "card-1", 2, true),
            new AttemptOutcome(Pair, "card-2", 1, false),
            new AttemptOutcome(Pair, "card-2", 2, false),
            new AttemptOutcome(Pair, "card-3", 1, true)
        ], k: 3);

        Assert.Equal(3, measurement.TasksObserved);
        Assert.Equal(2, measurement.TasksSolvedWithinK);
        Assert.Equal(1, measurement.TasksSolvedFirstTry);
        Assert.Equal(2d / 3d, measurement.PassAtK, 6);
        Assert.Equal(1d / 3d, measurement.PassAt1, 6);
    }

    [Fact]
    public void ASuccessBeyondKDoesNotCountForThatK()
    {
        var measurement = PassAtKPolicy.Measure(Pair,
        [
            new AttemptOutcome(Pair, "card-1", 1, false),
            new AttemptOutcome(Pair, "card-1", 2, false),
            new AttemptOutcome(Pair, "card-1", 3, true)
        ], k: 2);

        Assert.Equal(0, measurement.TasksSolvedWithinK);
        Assert.Equal(0d, measurement.PassAtK);
    }

    [Fact]
    public void OtherPairsHistoryIsIgnoredInsteadOfAveragedIn()
    {
        var measurement = PassAtKPolicy.Measure(Pair,
        [
            new AttemptOutcome(Pair, "card-1", 1, true),
            new AttemptOutcome(Other, "card-2", 1, false),
            new AttemptOutcome(Other, "card-3", 1, false)
        ], k: 3);

        Assert.Equal(1, measurement.TasksObserved);
        Assert.Equal(1d, measurement.PassAtK);
    }

    [Fact]
    public void SpendingManyRoundsDoesNotInflateTheScore()
    {
        // Um par que gasta 3 rodadas por card nao pode parecer melhor que quem resolve de primeira.
        var slow = PassAtKPolicy.Measure(Pair,
        [
            new AttemptOutcome(Pair, "c1", 1, false), new AttemptOutcome(Pair, "c1", 2, false),
            new AttemptOutcome(Pair, "c1", 3, true)
        ], k: 3);
        var fast = PassAtKPolicy.Measure(Pair, [new AttemptOutcome(Pair, "c1", 1, true)], k: 3);

        Assert.Equal(slow.PassAtK, fast.PassAtK);
        Assert.True(fast.PassAt1 > slow.PassAt1);
    }

    /// <summary>Par que não melhora com retentativa recebe uma rodada, não o teto.</summary>
    [Fact]
    public void APairThatDoesNotImproveWithRetriesGetsASingleRound()
    {
        var flat = PassAtKPolicy.Measure(Pair,
        [
            new AttemptOutcome(Pair, "c1", 1, true),
            new AttemptOutcome(Pair, "c2", 1, true),
            new AttemptOutcome(Pair, "c3", 1, false),
            new AttemptOutcome(Pair, "c3", 2, false)
        ], k: 3);

        Assert.Equal(0d, flat.RetryGain, 6);
        Assert.Equal(1, PassAtKPolicy.RecommendMaxRounds(flat, ceiling: 4));
    }

    [Fact]
    public void APairThatImprovesWithRetriesKeepsTheCeiling()
    {
        var improving = PassAtKPolicy.Measure(Pair,
        [
            new AttemptOutcome(Pair, "c1", 1, false), new AttemptOutcome(Pair, "c1", 2, true),
            new AttemptOutcome(Pair, "c2", 1, false), new AttemptOutcome(Pair, "c2", 2, true),
            new AttemptOutcome(Pair, "c3", 1, false), new AttemptOutcome(Pair, "c3", 2, true)
        ], k: 3);

        Assert.True(improving.RetryGain > 0.10d);
        Assert.Equal(4, PassAtKPolicy.RecommendMaxRounds(improving, ceiling: 4));
    }

    [Fact]
    public void ThinHistoryDoesNotProduceAnInventedRecommendation()
    {
        var thin = PassAtKPolicy.Measure(Pair, [new AttemptOutcome(Pair, "c1", 1, false)], k: 3);
        Assert.Equal(5, PassAtKPolicy.RecommendMaxRounds(thin, ceiling: 5));
    }

    [Fact]
    public void EmptyHistoryScoresZeroWithoutDividingByZero()
    {
        var empty = PassAtKPolicy.Measure(Pair, [], k: 3);
        Assert.Equal(0, empty.TasksObserved);
        Assert.Equal(0d, empty.PassAtK);
        Assert.Equal(0d, empty.PassAt1);
    }

    [Fact]
    public void RankingPrefersFewerRoundsForTheSameResult()
    {
        var slow = new PassAtKMeasurement(Pair, 3, 10, 8, 2);
        var fast = new PassAtKMeasurement(Other, 3, 10, 8, 7);

        Assert.Equal([Other, Pair], PassAtKPolicy.Rank([slow, fast]).Select(m => m.Pair));
    }

    [Fact]
    public void InvalidKIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PassAtKPolicy.Measure(Pair, [], 0));
    }
}

public sealed class LearningPromotionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static LearningCandidate Fresh() =>
        new("cand-1", "src/Modules/Billing", "Migrations desta area exigem espelho no Postgres.");

    private static LearningCandidate WithSupport(int times)
    {
        var candidate = Fresh();
        for (var index = 0; index < times; index++)
        {
            candidate = LearningPromotionPolicy.Observe(
                candidate, new LearningEvidence($"card-{index}", true, Now.AddMinutes(index)));
        }

        return candidate;
    }

    [Fact]
    public void OneObservationIsStillAnAnecdote()
    {
        var candidate = WithSupport(1);
        Assert.Equal(LearningStage.Candidate, candidate.Stage);
    }

    [Fact]
    public void EnoughSupportingEvidenceEarnsTheOwnersAttention()
    {
        var candidate = WithSupport(LearningPromotionPolicy.CorroborationThreshold);
        Assert.Equal(LearningStage.AwaitingApproval, candidate.Stage);
        Assert.Equal(1d, candidate.Confidence);
    }

    [Fact]
    public void AnIntermittentLessonIsNotReadyForTheOwner()
    {
        var candidate = WithSupport(3);
        candidate = LearningPromotionPolicy.Observe(
            candidate, new LearningEvidence("card-x", false, Now));
        candidate = LearningPromotionPolicy.Observe(
            candidate, new LearningEvidence("card-y", false, Now));

        // Licao que falha as vezes e pior que nenhuma: ela e aplicada com conviccao onde nao vale.
        Assert.True(candidate.Confidence < LearningPromotionPolicy.MinimumConfidence);
        Assert.Equal(LearningStage.Corroborated, candidate.Stage);
    }

    /// <summary>Gate da fase: promoção SÓ com aprovação humana.</summary>
    [Fact]
    public void ThereIsNoSilentPromotionPathNotEvenForConvenience()
    {
        var ready = WithSupport(5);
        Assert.Equal(LearningStage.AwaitingApproval, ready.Stage);

        Assert.Throws<InvalidOperationException>(
            () => LearningPromotionPolicy.Promote(ready, "", Now));
        Assert.Throws<InvalidOperationException>(
            () => LearningPromotionPolicy.Promote(ready, "   ", Now));

        var promoted = LearningPromotionPolicy.Promote(ready, "mateus", Now);
        Assert.Equal(LearningStage.Promoted, promoted.Stage);
        Assert.Equal("mateus", promoted.ApprovedBy);
        Assert.Equal(Now, promoted.ApprovedAt);
    }

    [Fact]
    public void ACandidateThatNeverEarnedAttentionCannotBePromoted()
    {
        Assert.Throws<InvalidOperationException>(
            () => LearningPromotionPolicy.Promote(WithSupport(1), "mateus", Now));
    }

    [Fact]
    public void RejectionIsFinalForThatEvidenceSoTheOwnerIsNotAskedAgain()
    {
        var rejected = LearningPromotionPolicy.Reject(WithSupport(4), "mateus");
        Assert.Equal(LearningStage.Rejected, rejected.Stage);

        // Pedir aprovação do mesmo item de novo transforma o pedido em ruído que ele ignora.
        var afterMore = LearningPromotionPolicy.Observe(
            rejected, new LearningEvidence("card-z", true, Now));
        Assert.Equal(LearningStage.Rejected, afterMore.Stage);
        Assert.Equal(rejected.Supporting, afterMore.Supporting);
    }

    [Fact]
    public void APromotedSkillIsNotDemotedByNewEvidenceAlone()
    {
        var promoted = LearningPromotionPolicy.Promote(WithSupport(3), "mateus", Now);
        var after = LearningPromotionPolicy.Observe(
            promoted, new LearningEvidence("card-z", false, Now));

        Assert.Equal(LearningStage.Promoted, after.Stage);
    }

    [Fact]
    public void OnlyPromotedSkillsOfTheScopeAreLoadable()
    {
        var promoted = LearningPromotionPolicy.Promote(WithSupport(3), "mateus", Now);
        var otherScope = promoted with { Id = "cand-2", Scope = "frontend/src" };
        var pending = WithSupport(3) with { Id = "cand-3" };

        var loadable = LearningPromotionPolicy.LoadableFor(
            "src/Modules/Billing", [promoted, otherScope, pending]);

        Assert.Equal(["cand-1"], loadable.Select(candidate => candidate.Id));
    }

    [Fact]
    public void ConfidenceOfAnUnobservedCandidateIsZeroNotOne()
    {
        Assert.Equal(0d, Fresh().Confidence);
    }
}
