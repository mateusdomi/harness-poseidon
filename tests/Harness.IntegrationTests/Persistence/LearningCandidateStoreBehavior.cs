using Harness.Persistence.Abstractions.Governance;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Persistence;

public static class LearningCandidateStoreBehavior
{
    public static async Task AssertAsync(ILearningCandidateStore store, string tenantId, string organizationId,
        string projectId, string adminProfileId, CancellationToken token)
    {
        var at = DateTimeOffset.UtcNow; var payload = new LearningCandidatePayload("Retry rule", "Retry only transient failures.",
            null, null, null, null, null, null, null, null, null, null);
        LearningEvidenceRecord[] evidence = [new("test", "test://retry", "sha256:" + new string('a', 64), "Repeated transient failures observed.")];
        var fingerprint = LearningCandidatePolicy.ComputeFingerprint(LearningCandidateType.Rule, "Retries repeat permanent failures", evidence, payload);
        LearningCandidateCreateCommand Create(string id, string key) => new(tenantId, organizationId, projectId, id,
            LearningCandidateType.Rule, fingerprint, "Retries repeat permanent failures", evidence, payload, "actor-agent", "fake", "actor-model",
            "rule/1", "rule/2", key, new string('b', 64), at);
        var candidateId = UlidValue.New(at).ToString(); var created = await store.CreateAsync(Create(candidateId, "learning-test-create"), token);
        Assert.False(created.Deduplicated); Assert.Equal(LearningCandidateState.Candidate, created.Candidate.State);
        var replay = await store.CreateAsync(Create(candidateId, "learning-test-create"), token); Assert.False(replay.Deduplicated);
        var duplicate = await store.CreateAsync(Create(UlidValue.New(at.AddMilliseconds(1)).ToString(), "learning-test-dedup"), token);
        Assert.True(duplicate.Deduplicated); Assert.Equal(candidateId, duplicate.Candidate.CandidateId);
        var current = created.Candidate;
        current = await Transition(LearningCandidateAction.RequestReview, current, "review");
        current = await Transition(LearningCandidateAction.RequestEvaluation, current, "evaluation-request");
        await Assert.ThrowsAsync<LearningCandidateAuthorizationException>(() => store.TransitionAsync(Command(
            LearningCandidateAction.CompleteEvaluation, current, "self-evaluation", evaluator: "actor-agent", provider: "fake", model: "actor-model", verdict: "pass"), token));
        current = await store.TransitionAsync(Command(LearningCandidateAction.CompleteEvaluation, current, "evaluation",
            evaluator: "critic-agent", provider: "independent", model: "critic-model", verdict: "pass"), token);
        current = await store.TransitionAsync(Command(LearningCandidateAction.StartShadow, current, "shadow", shadow: new LearningShadowResult(
            30, 0.12m, -0.08m, -120, -0.05m, 0, "evidence://shadow/1")), token);
        current = await Transition(LearningCandidateAction.Approve, current, "approved");
        await Assert.ThrowsAsync<LearningCandidateAuthorizationException>(() => store.TransitionAsync(Command(
            LearningCandidateAction.Promote, current, "member-promotion", admin: false), token));
        current = await Transition(LearningCandidateAction.Promote, current, "promoted");
        Assert.Equal("rule/2", current.ActiveVersion); Assert.Equal("rule/1", current.PreviousVersion);
        current = await Transition(LearningCandidateAction.Rollback, current, "rollback after regression");
        Assert.Equal("rule/1", current.ActiveVersion);
        current = await Transition(LearningCandidateAction.Deprecate, current, "superseded by safer candidate");
        Assert.Equal(LearningCandidateState.Deprecated, current.State);
        Assert.Equal(9, current.Version);
        Assert.Equal(9, (await store.ListHistoryAsync(tenantId, candidateId, token)).Count);
        var page = await store.ListAsync(tenantId, organizationId, projectId, null, null, null, 10, token);
        Assert.Single(page.Items); Assert.Equal(1, page.Total);
        var metrics = await store.GetMetricsAsync(tenantId, organizationId, projectId, token);
        Assert.Equal(1, metrics.Created); Assert.Equal(1, metrics.Deduplicated); Assert.Equal(1, metrics.Approved);
        Assert.Equal(1, metrics.Promoted); Assert.Equal(1, metrics.RolledBack); Assert.Equal(0, metrics.RegressionsAfterPromotion);

        Task<LearningCandidateRecord> Transition(LearningCandidateAction action, LearningCandidateRecord value, string note) =>
            store.TransitionAsync(Command(action, value, note), token);
        LearningCandidateTransitionCommand Command(LearningCandidateAction action, LearningCandidateRecord value, string key,
            bool admin = true, string? evaluator = null, string? provider = null, string? model = null, string? verdict = null, LearningShadowResult? shadow = null) =>
            new(tenantId, value.CandidateId, action, value.Version, adminProfileId, admin, key, evaluator, provider, model, verdict, shadow,
                $"learning-test-{key}", new string((char)('c' + value.Version), 64), at.AddSeconds(value.Version));
    }
}
