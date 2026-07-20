using System.Text.Json.Serialization;
using Harness.Host.Profiles;
using Harness.Host.Execution;
using Harness.Modules.Agents.Infrastructure.OmpRpc;
using Harness.Modules.Governance.Coordination;
using Harness.Modules.Governance.Documentation;
using Harness.Modules.Governance.Evaluation;
using Harness.Modules.Governance.Patching;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.AttemptWorkspaces;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Governance;

public static class GovernanceRuntimeEndpoints
{
    public static IEndpointRouteBuilder MapGovernanceRuntime(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/governance-runtime").WithTags("governance-runtime");
        group.MapGet("/receipts", ListReceiptsAsync).Produces<IReadOnlyList<GovernanceReceiptContract>>().ProducesProblem(401);
        group.MapGet("/receipts/{turnId}", GetReceiptAsync).Produces<GovernanceReceiptContract>().ProducesProblem(401).ProducesProblem(404);
        group.MapGet("/receipts/{turnId}/metrics", ListMetricsAsync).Produces<IReadOnlyList<GovernanceMetricContract>>().ProducesProblem(401);
        group.MapPost("/evaluations", EvaluateAsync).Produces<EvaluationResultContract>().ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/stale-doc-findings", DetectStaleDocumentsAsync).Produces<IReadOnlyList<StaleDocumentFindingContract>>().ProducesProblem(401);
        group.MapPost("/projects/{projectId}/hashline-patches", ApplyHashlinePatchAsync).Produces<HashlinePatchContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        group.MapGet("/hashline-benchmark", () => Results.Ok(HashlinePatchBenchmark.Run(100).Select(ToContract).ToArray()))
            .Produces<IReadOnlyList<PatchBenchmarkContract>>();
        group.MapGet("/executors", (AgentExecutorCatalog catalog) => Results.Ok(catalog.List().Select(ToContract).ToArray()))
            .Produces<IReadOnlyList<AgentExecutorContract>>();
        return endpoints;
    }

    private static async Task<IResult> ListReceiptsAsync(
        string? projectId,
        string? cursor,
        int? limit,
        HttpRequest request,
        ILocalProfileStore profiles,
        IGovernanceRuntimeStore store,
        CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        var size = limit ?? 100;
        if (size is < 1 or > 500) return Invalid("invalid_limit", "limit must be between 1 and 500.");
        var values = await store.ListReceiptsAsync(session.TenantId, projectId, cursor, size, token);
        return Results.Ok(values.Select(ToContract).ToArray());
    }

    private static async Task<IResult> GetReceiptAsync(
        string turnId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IGovernanceRuntimeStore store,
        CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        var value = await store.GetReceiptAsync(session.TenantId, turnId, token);
        return value is null ? NotFound("receipt") : Results.Ok(ToContract(value));
    }

    private static async Task<IResult> ListMetricsAsync(
        string turnId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IGovernanceRuntimeStore store,
        CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        return Results.Ok((await store.ListMetricsAsync(session.TenantId, turnId, token)).Select(ToContract).ToArray());
    }

    private static async Task<IResult> EvaluateAsync(
        FreshContextEvaluationApiRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IFreshContextEvaluator evaluator,
        IGovernanceRuntimeStore store,
        IClock clock,
        CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        try
        {
            var value = evaluator.Evaluate(
                new FreshContextEvaluationRequest(
                    input.EvaluationId,
                    session.TenantId,
                    input.ProjectId,
                    input.TaskId,
                    input.AttemptId,
                    input.ActorAgentId,
                    input.EvaluatorAgentId,
                    input.RiskTier,
                    input.AcceptanceCriteria,
                    input.Diff,
                    input.Evidence,
                    input.TestResults.Select(test => new EvaluationTestResult(
                        test.Name, test.Passed, test.EvidenceReference)).ToArray()),
                clock.UtcNow);
            var receipt = await store.GetReceiptAsync(session.TenantId, input.TurnId, token);
            if (receipt is not null)
            {
                await store.AppendMetricAsync(
                    new GovernanceMetricAppendCommand(
                        session.TenantId,
                        input.ProjectId,
                        input.TurnId,
                        UlidValue.New(clock.UtcNow).ToString(),
                        GovernanceMetricKind.EvaluatorVerdict,
                        null,
                        null,
                        value.Verdict.ToString().ToLowerInvariant(),
                        null,
                        clock.UtcNow),
                    token);
            }

            return Results.Ok(ToContract(value));
        }
        catch (ArgumentException exception)
        {
            return Invalid("invalid_evaluation", exception.Message);
        }
    }

    private static async Task<IResult> DetectStaleDocumentsAsync(
        HttpRequest request,
        ILocalProfileStore profiles,
        IGovernanceRuntimeStore store,
        StaleDocumentDetector detector,
        IClock clock,
        CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        var receipts = await store.ListReceiptsAsync(session.TenantId, null, null, 500, token);
        var usage = receipts.SelectMany(receipt => receipt.Documents.Select(document => (receipt, document)))
            .GroupBy(item => item.document.DocumentId, StringComparer.Ordinal)
            .Select(group => new DocumentUsageSnapshot(
                group.Key,
                group.Count(),
                group.Count(item => item.receipt.Truncated.Contains(item.document.DocumentId, StringComparer.Ordinal)),
                group.Max(item => (DateTimeOffset?)item.receipt.Timestamp)))
            .ToArray();
        IReadOnlySet<string> enforcement = new HashSet<string>(
            ["tools/backend/verify-governance.sh", "tools/backend/scan-secrets.sh", "ci:governance", "runtime:governance-policy"],
            StringComparer.Ordinal);
        var findings = detector.Detect(clock.UtcNow, usage, enforcement);
        return Results.Ok(findings.Select(ToContract).ToArray());
    }

    private static async Task<IResult> ApplyHashlinePatchAsync(
        string projectId,
        HashlinePatchApiRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IAgentCatalogStore agents,
        IAttemptWorkspaceStore workspaces,
        IGovernanceRuntimeStore governance,
        HashlinePatchOptions options,
        IsolatedExecutionSettings isolatedSettings,
        IClock clock,
        CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        var project = await projects.GetAsync(session.TenantId, projectId, token);
        if (project is null || string.IsNullOrWhiteSpace(project.RepositoryUrl)) return NotFound("project");
        var receipt = await governance.GetReceiptAsync(session.TenantId, input.TurnId, token);
        if (receipt is null || receipt.ProjectId != projectId)
        {
            return Results.Problem(statusCode: 409, title: "governance_receipt_required", detail: "Hashline writes require a turn receipt for the same project.");
        }

        if (string.IsNullOrWhiteSpace(isolatedSettings.ControlledRoot))
        {
            return Results.Problem(statusCode: 409, title: "controlled_root_required", detail: "Hashline writes require a configured controlled repository root.");
        }

        var repositoryRoot = Path.GetFullPath(project.RepositoryUrl);
        var controlledRoot = Path.GetFullPath(isolatedSettings.ControlledRoot);
        if (!Directory.Exists(repositoryRoot) || !repositoryRoot.StartsWith(
                $"{controlledRoot}{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            return Results.Problem(statusCode: 409, title: "repository_outside_controlled_root", detail: "The project repository must exist inside the configured controlled root.");
        }

        var agent = await agents.GetAgentAsync(session.TenantId, receipt.AgentId, token);
        var definition = agent is null
            ? null
            : await agents.GetDefinitionForTenantAsync(session.TenantId, agent.DefinitionId, token);
        if (definition is null)
        {
            return Results.Problem(statusCode: 409, title: "agent_scope_identity_missing", detail: "Hashline writes require a resolvable agent definition.");
        }

        var scopeKind = isolatedSettings.KimiAgentDefinitionKeys.Contains(
            definition.Key,
            StringComparer.OrdinalIgnoreCase)
            ? AgentPathScopeKind.Kimi
            : AgentPathScopeKind.Backend;
        var scope = AgentPathScopePolicy.Evaluate(scopeKind, [input.RelativePath]);
        if (!scope.Allowed)
        {
            return Results.Problem(statusCode: 409, title: scope.Code, detail: "The requested patch path is outside the agent scope.");
        }

        if (IsSharedPath(input.RelativePath))
        {
            var workspace = await workspaces.GetAsync(session.TenantId, receipt.AttemptId, token);
            if (workspace is null || workspace.ProjectId != projectId ||
                !workspace.ScopeClaims.Any(claim => claim.ReleasedAt is null && ClaimContains(claim.PathPattern, input.RelativePath)))
            {
                return Results.Problem(statusCode: 409, title: "shared_path_claim_required", detail: "Shared governance paths require a persisted active workspace claim.");
            }
        }

        try
        {
            var sink = new GovernanceHashlineAuditSink(governance);
            var service = new HashlinePatchService(repositoryRoot, options, sink);
            var result = await service.ApplyAsync(
                new HashlinePatchCommand(
                    session.TenantId,
                    projectId,
                    input.TurnId,
                    input.RelativePath,
                    input.ExpectedChecksum,
                    input.NewContent,
                    clock.UtcNow),
                token);
            return result.Status == HashlinePatchStatus.StaleRejected
                ? Results.Conflict(ToContract(result))
                : Results.Ok(ToContract(result));
        }
        catch (ArgumentException exception)
        {
            return Invalid("invalid_hashline_patch", exception.Message);
        }
    }

    private static bool IsSharedPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        return normalized is "AGENTS.md" or "CLAUDE.md" or "README.md" or "Harness.sln" or
            "Directory.Build.props" or "Directory.Build.targets" or "Directory.Packages.props" or "global.json" ||
            normalized == "governance" || normalized.StartsWith("governance/", StringComparison.Ordinal) ||
            normalized == ".github" || normalized.StartsWith(".github/", StringComparison.Ordinal);
    }

    private static bool ClaimContains(string claim, string path)
    {
        var normalizedClaim = claim.Replace('\\', '/').Trim('/');
        var normalizedPath = path.Replace('\\', '/').Trim('/');
        return normalizedClaim.EndsWith("/**", StringComparison.Ordinal)
            ? normalizedPath.StartsWith(normalizedClaim[..^3].TrimEnd('/') + "/", StringComparison.Ordinal) ||
              string.Equals(normalizedPath, normalizedClaim[..^3].TrimEnd('/'), StringComparison.Ordinal)
            : string.Equals(normalizedClaim, normalizedPath, StringComparison.Ordinal);
    }

    private static GovernanceReceiptContract ToContract(GovernanceTurnReceiptRecord value) => new(
        value.ProjectId, value.TaskId, value.AttemptId, value.TurnId, value.AgentId,
        value.ManifestVersion,
        value.Documents.Select(document => new GovernanceReceiptDocumentContract(
            document.DocumentId, document.Checksum, document.SelectionReason,
            document.LoadPolicy, document.EstimatedTokens)).ToArray(),
        value.EstimatedTokens, value.ActualPromptTokens, value.Truncated, value.Conflicts,
        value.CacheHits, value.Provider, value.Model, value.Timestamp, value.BundleChecksum,
        value.State.ToString().ToLowerInvariant(), value.GateResult, value.Version);

    private static GovernanceMetricContract ToContract(GovernanceMetricRecord value) => new(
        value.ProjectId, value.TurnId, value.EventId, value.Kind.ToString(), value.DocumentId,
        value.RuleId, value.DetailCode, value.TokenCount, value.OccurredAt);

    private static EvaluationResultContract ToContract(FreshContextEvaluationResult value) => new(
        value.SchemaVersion, value.EvaluationId, value.Verdict.ToString().ToLowerInvariant(),
        value.Findings.Select(finding => new EvaluationFindingContract(
            finding.Priority.ToString(), finding.Confidence, finding.Evidence, finding.Path,
            finding.Range, finding.RuleId, finding.RecommendedAction)).ToArray(),
        value.Provider, value.Model, value.ReadOnly, value.CleanContext, value.EvaluatedAt);

    private static StaleDocumentFindingContract ToContract(StaleDocumentFinding value) => new(
        value.FindingId, value.DocumentId, value.Kind.ToString(), value.Detail,
        value.RecommendedTask, value.DetectedAt);

    private static HashlinePatchContract ToContract(HashlinePatchResult value) => new(
        value.Status.ToString(), value.RelativePath, value.ExpectedChecksum, value.ActualChecksum,
        value.AppliedChecksum, value.Action);

    private static PatchBenchmarkContract ToContract(PatchBenchmarkResult value) => new(
        value.Strategy, value.EditSuccesses, value.StaleRejections, value.Retries,
        value.EstimatedTokens, value.DurationMicroseconds, value.Regressions);

    private static AgentExecutorContract ToContract(AgentExecutorDescriptor value) => new(
        value.Id, value.Available, value.Enabled, value.ExecutablePath is null ? null : Path.GetFileName(value.ExecutablePath),
        value.License, value.AvailabilityReason);

    private static IResult Unauthorized() => Results.Problem(statusCode: 401, title: "local_session_required", detail: "A local profile session is required.");
    private static IResult Invalid(string title, string detail) => Results.Problem(statusCode: 400, title: title, detail: detail);
    private static IResult NotFound(string resource) => Results.Problem(statusCode: 404, title: $"{resource}_not_found", detail: "The requested resource does not exist.");
}

public sealed class GovernanceHashlineAuditSink(IGovernanceRuntimeStore store) : IHashlinePatchAuditSink
{
    private readonly IGovernanceRuntimeStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task AppendAsync(HashlinePatchAuditRecord record, CancellationToken cancellationToken = default) =>
        _store.AppendMetricAsync(
            new GovernanceMetricAppendCommand(
                record.TenantId,
                record.ProjectId,
                record.TurnId,
                UlidValue.New(record.OccurredAt).ToString(),
                record.Status == HashlinePatchStatus.Applied
                    ? GovernanceMetricKind.PatchApplied
                    : GovernanceMetricKind.PatchRejected,
                null,
                "hashline_expected_checksum",
                record.Status.ToString().ToLowerInvariant(),
                null,
                record.OccurredAt),
            cancellationToken);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FreshContextEvaluationApiRequest(
    string EvaluationId,
    string ProjectId,
    string TaskId,
    string AttemptId,
    string TurnId,
    string ActorAgentId,
    string EvaluatorAgentId,
    string RiskTier,
    IReadOnlyList<string> AcceptanceCriteria,
    string Diff,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<EvaluationTestContract> TestResults);

public sealed record EvaluationTestContract(string Name, bool Passed, string EvidenceReference);
public sealed record EvaluationFindingContract(string Priority, decimal Confidence, string Evidence, string? Path, string? Range, string RuleId, string RecommendedAction);
public sealed record EvaluationResultContract(string SchemaVersion, string EvaluationId, string Verdict, IReadOnlyList<EvaluationFindingContract> Findings, string Provider, string? Model, bool ReadOnly, bool CleanContext, DateTimeOffset EvaluatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HashlinePatchApiRequest(string TurnId, string RelativePath, string ExpectedChecksum, string NewContent);

public sealed record GovernanceReceiptDocumentContract(string DocumentId, string Checksum, string SelectionReason, string LoadPolicy, int EstimatedTokens);
public sealed record GovernanceReceiptContract(string ProjectId, string TaskId, string AttemptId, string TurnId, string AgentId, string ManifestVersion, IReadOnlyList<GovernanceReceiptDocumentContract> Documents, int EstimatedTokens, int? ActualPromptTokens, IReadOnlyList<string> Truncated, IReadOnlyList<string> Conflicts, int CacheHits, string Provider, string? Model, DateTimeOffset Timestamp, string BundleChecksum, string State, string? GateResult, long Version);
public sealed record GovernanceMetricContract(string ProjectId, string TurnId, string EventId, string Kind, string? DocumentId, string? RuleId, string? DetailCode, int? TokenCount, DateTimeOffset OccurredAt);
public sealed record StaleDocumentFindingContract(string FindingId, string DocumentId, string Kind, string Detail, string RecommendedTask, DateTimeOffset DetectedAt);
public sealed record HashlinePatchContract(string Status, string RelativePath, string ExpectedChecksum, string ActualChecksum, string? AppliedChecksum, string Action);
public sealed record PatchBenchmarkContract(string Strategy, int EditSuccesses, int StaleRejections, int Retries, int EstimatedTokens, long DurationMicroseconds, int Regressions);
public sealed record AgentExecutorContract(string Id, bool Available, bool Enabled, string? ExecutableName, string License, string AvailabilityReason);
