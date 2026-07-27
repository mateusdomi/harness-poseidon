namespace Harness.Modules.Governance.Evaluation;

public enum ReviewPriority
{
    P0,
    P1,
    P2,
    P3,
}

public enum EvaluationVerdict
{
    Pass,
    Fail,
}

public sealed record EvaluationTestResult(
    string Name,
    bool Passed,
    string EvidenceReference);

public sealed record EvaluationFinding(
    ReviewPriority Priority,
    decimal Confidence,
    string Evidence,
    string? Path,
    string? Range,
    string RuleId,
    string RecommendedAction);

public sealed record FreshContextEvaluationRequest(
    string EvaluationId,
    string TenantId,
    string ProjectId,
    string TaskId,
    string AttemptId,
    string ActorAgentId,
    string EvaluatorAgentId,
    string RiskTier,
    IReadOnlyList<string> AcceptanceCriteria,
    string Diff,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<EvaluationTestResult> TestResults,
    string? AccountAlias = null,
    string? TaskSignature = null);

public sealed record FreshContextEvaluationResult(
    string SchemaVersion,
    string EvaluationId,
    EvaluationVerdict Verdict,
    IReadOnlyList<EvaluationFinding> Findings,
    string Provider,
    string? Model,
    bool ReadOnly,
    bool CleanContext,
    DateTimeOffset EvaluatedAt,
    string? ProducerAgentId = null,
    string? AccountAlias = null,
    string? TaskSignature = null);

public sealed record FreshContextEvaluatorOptions
{
    public bool Enabled { get; init; } = true;

    public string Provider { get; init; } = "deterministic-independent";

    public string? Model { get; init; }

    public IReadOnlyList<string> ActiveRiskTiers { get; init; } = ["medium", "high", "critical"];
}

public interface IFreshContextEvaluator
{
    FreshContextEvaluationResult Evaluate(
        FreshContextEvaluationRequest request,
        DateTimeOffset evaluatedAt);
}

public sealed class FreshContextEvaluator(FreshContextEvaluatorOptions options)
    : IFreshContextEvaluator
{
    private readonly FreshContextEvaluatorOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public FreshContextEvaluationResult Evaluate(
        FreshContextEvaluationRequest request,
        DateTimeOffset evaluatedAt)
    {
        Validate(request);
        var findings = new List<EvaluationFinding>();
        if (!_options.Enabled || !_options.ActiveRiskTiers.Contains(request.RiskTier, StringComparer.Ordinal))
        {
            findings.Add(Finding(
                ReviewPriority.P0,
                "evaluator_required",
                "Independent evaluation is not active for a risk tier that requires it.",
                "Enable the evaluator before continuing."));
        }

        if (request.RiskTier is "medium" or "high" or "critical" &&
            string.Equals(request.ActorAgentId, request.EvaluatorAgentId, StringComparison.Ordinal))
        {
            findings.Add(Finding(
                ReviewPriority.P0,
                "actor_self_approval",
                "Actor and evaluator identities are equal.",
                "Assign a distinct evaluator instance."));
        }

        if (request.AcceptanceCriteria.Count == 0)
        {
            findings.Add(Finding(
                ReviewPriority.P0,
                "acceptance_criteria_missing",
                "No acceptance criteria were supplied.",
                "Define verifiable acceptance criteria."));
        }

        if (string.IsNullOrWhiteSpace(request.Diff))
        {
            findings.Add(Finding(
                ReviewPriority.P1,
                "diff_missing",
                "No diff was supplied to the clean evaluator context.",
                "Supply the immutable task diff."));
        }

        if (request.Evidence.Count == 0)
        {
            findings.Add(Finding(
                ReviewPriority.P1,
                "evidence_missing",
                "No execution evidence was supplied.",
                "Run the required gates and attach sanitized evidence."));
        }

        foreach (var test in request.TestResults.Where(test => !test.Passed))
        {
            findings.Add(new EvaluationFinding(
                ReviewPriority.P1,
                1m,
                $"Test failed: {test.Name}; evidence={test.EvidenceReference}",
                null,
                null,
                "test_gate_failed",
                "Fix the failure and rerun the gate in a fresh context."));
        }

        if (request.TestResults.Count == 0)
        {
            findings.Add(Finding(
                ReviewPriority.P1,
                "test_results_missing",
                "No test results were supplied.",
                "Execute and attach the task test plan."));
        }

        return new FreshContextEvaluationResult(
            "1.0.0",
            request.EvaluationId,
            findings.Count == 0 ? EvaluationVerdict.Pass : EvaluationVerdict.Fail,
            findings,
            _options.Provider,
            _options.Model,
            true,
            true,
            evaluatedAt,
            request.ActorAgentId,
            request.AccountAlias,
            request.TaskSignature);
    }

    private static EvaluationFinding Finding(
        ReviewPriority priority,
        string ruleId,
        string evidence,
        string action) => new(priority, 1m, evidence, null, null, ruleId, action);

    private static void Validate(FreshContextEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EvaluationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EvaluatorAgentId);
        ArgumentNullException.ThrowIfNull(request.AcceptanceCriteria);
        ArgumentNullException.ThrowIfNull(request.Evidence);
        ArgumentNullException.ThrowIfNull(request.TestResults);
    }
}
