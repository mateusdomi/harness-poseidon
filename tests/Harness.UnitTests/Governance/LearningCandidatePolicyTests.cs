using Harness.Persistence.Abstractions.Governance;

namespace Harness.UnitTests.Governance;

public sealed class LearningCandidatePolicyTests
{
    [Theory]
    [InlineData("rule")]
    [InlineData("skill")]
    [InlineData("persona_refinement")]
    [InlineData("workflow_refinement")]
    [InlineData("tool_routing_recommendation")]
    [InlineData("documentation_correction")]
    [InlineData("provider_model_routing_recommendation")]
    public void CandidateTypesHaveClosedValidatedPayloads(string typeName)
    {
        var type = LearningCandidatePolicy.ParseType(typeName);
        var payload = type switch
        {
            LearningCandidateType.Rule => Payload(statement: "rule"),
            LearningCandidateType.Skill => Payload(instructions: "instructions"),
            LearningCandidateType.PersonaRefinement => Payload(persona: "chief", refinement: "refinement"),
            LearningCandidateType.WorkflowRefinement => Payload(workflow: "delivery", refinement: "refinement"),
            LearningCandidateType.ToolRoutingRecommendation => Payload(tool: "git", recommendation: "recommendation"),
            LearningCandidateType.DocumentationCorrection => Payload(document: "core", correction: "correction"),
            LearningCandidateType.ProviderModelRoutingRecommendation => Payload(provider: "openai", model: "gpt", recommendation: "recommendation"),
            _ => throw new InvalidOperationException(),
        };
        LearningCandidatePolicy.ValidatePayload(type, payload);
        var invalid = type == LearningCandidateType.DocumentationCorrection
            ? payload with { Instructions = "unexpected" }
            : payload with { Correction = "unexpected" };
        Assert.Throws<ArgumentException>(() => LearningCandidatePolicy.ValidatePayload(type, invalid));
    }

    [Fact]
    public void FingerprintIsDeterministicAcrossObservationWhitespaceAndEvidenceOrder()
    {
        var payload = Payload(statement: "rule");
        LearningEvidenceRecord[] first = [Evidence("b"), Evidence("a")];
        LearningEvidenceRecord[] second = [Evidence("a"), Evidence("b")];
        Assert.Equal(LearningCandidatePolicy.ComputeFingerprint(LearningCandidateType.Rule, "  Retry   transient ", first, payload),
            LearningCandidatePolicy.ComputeFingerprint(LearningCandidateType.Rule, "retry transient", second, payload));
    }

    [Fact]
    public void PromotionRequiresExplicitAdminAfterApproval()
    {
        var candidate = Record() with { State = LearningCandidateState.Approved, Version = 6 };
        var command = new LearningCandidateTransitionCommand("tenant", candidate.CandidateId, LearningCandidateAction.Promote, 6,
            "member", false, null, null, null, null, null, null, "key", new string('a', 64), DateTimeOffset.UtcNow);
        Assert.Throws<LearningCandidateAuthorizationException>(() => LearningCandidatePolicy.Next(candidate, command));
    }

    private static LearningCandidatePayload Payload(string? statement = null, string? instructions = null, string? persona = null,
        string? workflow = null, string? tool = null, string? document = null, string? provider = null, string? model = null,
        string? refinement = null, string? recommendation = null, string? correction = null) =>
        new("title", statement, instructions, persona, workflow, tool, document, provider, model, refinement, recommendation, correction);
    private static LearningEvidenceRecord Evidence(string value) => new("test", $"test://{value}", new string(value[0], 64), value);
    private static LearningCandidateRecord Record() => new("tenant", "organization", "project", "candidate", LearningCandidateType.Rule,
        LearningCandidateState.Candidate, new string('a', 64), "observation", [Evidence("a")], Payload(statement: "rule"),
        "actor", "fake", "actor-model", "v1", "v2", null, null, null, null, null, null, null, null, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
}
