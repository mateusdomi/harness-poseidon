using System.Text.Json.Serialization;
using Harness.Host.Profiles;
using Harness.Host.Execution;
using Harness.Modules.Agents.Infrastructure.OmpRpc;
using Harness.Modules.Governance.Coordination;
using Harness.Modules.Governance.Documentation;
using Harness.Modules.Governance.Evaluation;
using Harness.Modules.Governance.Judging;
using Harness.Modules.Governance.Metrics;
using Harness.Modules.Governance.Patching;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.AttemptWorkspaces;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
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
        // PLAT-04: camada de medição (read-only, não muta o board).
        group.MapGet("/feature-metrics", ListFeatureMetricsAsync).Produces<FeatureMetricsResponse>().ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/stuck-tasks", ListStuckTasksAsync).Produces<StuckTasksResponse>().ProducesProblem(400).ProducesProblem(401);
        group.MapPost("/eval-judge", JudgeAttemptAsync).Produces<EvalJudgeVerdictContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(503);
        // Fase 5: recomendações estatísticas derivadas ESTRITAMENTE das tentativas gravadas.
        group.MapGet("/evaluation-recommendations", ListEvaluationRecommendationsAsync)
            .Produces<EvaluationRecommendationsResponse>().ProducesProblem(400).ProducesProblem(401);
        // Fase 6: memória semântica consultável com citações, montada server-side pelo
        // Context Builder — slices vêm do índice vetorial derivado, nunca de fonte inventada.
        group.MapGet("/memory-search", SearchMemoryAsync)
            .Produces<MemorySearchResponse>().ProducesProblem(400).ProducesProblem(401);
        // Fase 10: contenção MEDIDA da fila única de merge — o gatilho decidido em arquitetura
        // para reavaliar infraestrutura de fila externa é este número, não intuição.
        group.MapGet("/merge-contention", (
            Harness.Modules.Coordination.Application.ISerializedMergeCoordinator merges) =>
        {
            var snapshot = merges.Snapshot();
            return Results.Ok(new MergeContentionContract(
                snapshot.Enqueued, snapshot.Serialized, snapshot.Contended, snapshot.Waiting,
                snapshot.Active, snapshot.TotalWait.TotalMilliseconds,
                snapshot.MaximumWait.TotalMilliseconds, snapshot.ContentionRatio));
        }).Produces<MergeContentionContract>();
        return endpoints;
    }

    // PLAT-04: métricas por feature derivadas ESTRITAMENTE das tentativas gravadas. O id da feature
    // vem do título da tarefa (token estável como CAT-04/PLAT-04); nada é inventado.
    private static async Task<IResult> ListFeatureMetricsAsync(
        string? projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IWorkBoardStore board,
        CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(projectId) || !UlidValue.TryParse(projectId, out _))
            return Invalid("invalid_project", "projectId must be a ULID.");
        var rows = await board.ListFeatureAttemptRowsAsync(session.TenantId, projectId, token);
        var snapshot = FeatureMetricsAggregator.Aggregate(
            projectId,
            rows.Select(row => new FeatureAttemptInput(
                row.TaskId, row.TaskTitle, row.State, row.OperationalState,
                row.CostUsd, row.TokensInput, row.TokensOutput, row.DurationMs)).ToArray());
        return Results.Ok(new FeatureMetricsResponse(
            snapshot.ProjectId,
            snapshot.Features.Select(feature => new FeatureMetricContract(
                feature.FeatureId, feature.TaskCount, feature.AttemptCount, feature.SuccessCount,
                feature.FailureCount, feature.InProgressCount, feature.TotalCostUsd,
                feature.TotalTokensInput, feature.TotalTokensOutput, feature.TotalDurationMs)).ToArray()));
    }

    // Fase 5 — Evaluation Service no caminho REAL: agrega as tentativas duráveis do board por
    // (agente produtor, provedor, modelo) — provedor/modelo vêm do espelho `model_invocations`
    // quando a tentativa tem invocação registrada — e devolve score composto, intervalo de
    // confiança de Wilson e a recomendação tipada. Sem findings estruturados por tentativa, o
    // composto deriva apenas do desfecho aprovado/rejeitado — nada é inventado; tentativas em
    // andamento ficam FORA da amostra.
    private static async Task<IResult> ListEvaluationRecommendationsAsync(
        string? projectId,
        int? minSampleSize,
        HttpRequest request,
        ILocalProfileStore profiles,
        IWorkBoardStore board,
        Harness.Persistence.Abstractions.Providers.IModelInvocationStore invocations,
        IEvaluationService evaluations,
        IClock clock,
        CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(projectId) || !UlidValue.TryParse(projectId, out _))
            return Invalid("invalid_project", "projectId must be a ULID.");
        var minimumSample = minSampleSize is > 0 ? minSampleSize.Value : 5;

        var rows = await board.ListFeatureAttemptRowsAsync(session.TenantId, projectId, token);
        var terminal = rows
            .Where(row => FeatureMetricsAggregator.Classify(row.State, row.OperationalState)
                != AttemptOutcome.InProgress)
            .ToArray();

        // Provedor/modelo por tentativa a partir do fato durável de invocação (Fase 3).
        var providerByAttempt = new Dictionary<string, (string Provider, string Model)>(StringComparer.Ordinal);
        foreach (var taskId in terminal.Select(row => row.TaskId).Distinct(StringComparer.Ordinal))
        {
            foreach (var invocation in await invocations.GetTaskInvocationsAsync(session.TenantId, taskId, token))
            {
                providerByAttempt[invocation.AttemptId] = (invocation.Provider, invocation.Model);
            }
        }

        var aggregates = terminal
            .GroupBy(row =>
            {
                var invocation = providerByAttempt.TryGetValue(row.AttemptId, out var value)
                    ? value
                    : ((string?)null, (string?)null);
                return (Agent: row.ProducerAgentId, invocation.Item1, invocation.Item2);
            })
            .Select(group =>
            {
                var total = group.Count();
                var successes = group.Count(row =>
                    FeatureMetricsAggregator.Classify(row.State, row.OperationalState)
                        == AttemptOutcome.Succeeded);
                var passRate = Math.Round((double)successes / total, 4);
                var compositeScore = Math.Round(
                    group.Average(row => evaluations.CalculateCompositeScore(
                        passRate,
                        0,
                        0,
                        0,
                        0,
                        FeatureMetricsAggregator.Classify(row.State, row.OperationalState)
                            == AttemptOutcome.Succeeded)),
                    4);
                var (lower, upper) = evaluations.CalculateConfidenceInterval(total, successes);
                return new PerformanceAggregate(
                    group.Key.Agent,
                    group.Key.Item3,
                    group.Key.Item2,
                    null,
                    total,
                    successes,
                    total - successes,
                    passRate,
                    compositeScore,
                    lower,
                    upper,
                    total >= minimumSample);
            })
            .OrderBy(aggregate => aggregate.TargetId, StringComparer.Ordinal)
            .ThenBy(aggregate => aggregate.Provider, StringComparer.Ordinal)
            .ToArray();

        var recommendations = evaluations.GenerateRecommendations(aggregates, clock.UtcNow);
        return Results.Ok(new EvaluationRecommendationsResponse(
            projectId,
            aggregates.Select(aggregate => new PerformanceAggregateContract(
                aggregate.TargetId, aggregate.Model, aggregate.Provider, aggregate.SampleSize,
                aggregate.SuccessCount, aggregate.FailureCount, aggregate.PassRate,
                aggregate.CompositeScore, aggregate.ConfidenceIntervalLower,
                aggregate.ConfidenceIntervalUpper, aggregate.SampleSizeQualified)).ToArray(),
            recommendations.Select(recommendation => new EvaluationRecommendationContract(
                recommendation.TargetId, recommendation.Model, recommendation.Provider,
                recommendation.Action, recommendation.Score,
                recommendation.ConfidenceIntervalLower, recommendation.ConfidenceIntervalUpper,
                recommendation.SampleSize, recommendation.RecommendationReason,
                recommendation.GeneratedAt)).ToArray()));
    }

    // Fase 6 — memória semântica no caminho REAL: a query vira embedding local determinístico,
    // o índice vetorial derivado devolve os slices com proveniência (metadados gravados na
    // ingestão) e o Context Builder monta o bundle server-side com orçamento de tokens e hash
    // auditável. O índice nunca é fonte da verdade: cada slice cita o registro durável de origem.
    private static async Task<IResult> SearchMemoryAsync(
        string? query,
        string? projectId,
        int? topK,
        int? maxTokens,
        HttpRequest request,
        ILocalProfileStore profiles,
        Harness.SharedKernel.Memory.IVectorIndex vectors,
        Harness.Modules.Governance.Memory.IContextBuilder contextBuilder,
        IClock clock,
        CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(query))
            return Invalid("invalid_query", "query is required.");

        var results = await vectors.SearchAsync(
            session.TenantId,
            Harness.Modules.Governance.Memory.DeterministicLocalEmbedding.Embed(query),
            topK is > 0 and <= 50 ? topK.Value : 5,
            minScore: 0.0,
            token);
        var scoped = results
            .Where(result => string.IsNullOrWhiteSpace(projectId) ||
                string.Equals(result.Document.ProjectId, projectId, StringComparison.Ordinal))
            .ToArray();

        var snapshot = contextBuilder.BuildSnapshot(
            session.TenantId,
            projectId ?? "memory-search",
            "vector-index-derived",
            scoped.Select(result => new Harness.Modules.Governance.Memory.ContextBundleDocument(
                result.Document.Id,
                result.Document.Metadata.TryGetValue("fileName", out var fileName)
                    ? fileName
                    : result.Document.DocumentType,
                result.Document.Content,
                $"{result.Document.DocumentType}:{result.Document.Id}",
                Math.Max(1, result.Document.Content.Length / 4))).ToArray(),
            maxTokens is > 0 ? maxTokens.Value : 4000,
            clock.UtcNow);

        return Results.Ok(new MemorySearchResponse(
            snapshot.SnapshotId,
            snapshot.Hash,
            snapshot.TotalTokens,
            scoped.Select(result => new MemorySliceContract(
                result.Document.Id,
                result.Document.DocumentType,
                result.Document.ProjectId,
                result.Document.Content,
                Math.Round(result.Score, 6),
                $"{result.Document.DocumentType}:{result.Document.Id}",
                result.Document.Metadata)).ToArray()));
    }

    // PLAT-04: detecção pura de travamento semântico. Read-only: surface as tarefas travadas com a
    // razão tipada; NÃO muta o board destrutivamente.
    private static async Task<IResult> ListStuckTasksAsync(
        string? projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IWorkBoardStore board,
        SemanticStuckDetector detector,
        CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(projectId) || !UlidValue.TryParse(projectId, out _))
            return Invalid("invalid_project", "projectId must be a ULID.");
        var rows = await board.ListFeatureAttemptRowsAsync(session.TenantId, projectId, token);
        var stuck = new List<StuckTaskContract>();
        foreach (var taskGroup in rows.GroupBy(row => row.TaskId, StringComparer.Ordinal))
        {
            var ordered = taskGroup.OrderBy(row => row.AttemptNumber).ToArray();
            var history = ordered.Select(row => new StuckAttemptSignal(
                row.AttemptNumber,
                FeatureMetricsAggregator.Classify(row.State, row.OperationalState) switch
                {
                    AttemptOutcome.Succeeded => StuckOutcome.Succeeded,
                    AttemptOutcome.Failed => StuckOutcome.Failed,
                    _ => StuckOutcome.Pending,
                },
                row.FailureReason,
                row.InstructionContentHash)).ToArray();
            var verdict = detector.Evaluate(history);
            if (verdict.IsStuck)
            {
                stuck.Add(new StuckTaskContract(
                    taskGroup.Key, FeatureIdParser.Parse(ordered[0].TaskTitle), ordered[0].TaskTitle,
                    verdict.Reason.ToString(), verdict.NoProgressStreak, verdict.Detail,
                    ordered.Length));
            }
        }

        return Results.Ok(new StuckTasksResponse(
            projectId, stuck.OrderBy(item => item.TaskId, StringComparer.Ordinal).ToArray()));
    }

    // PLAT-04: LLM-as-judge. Default determinístico e sempre disponível; o juiz real (se configurado)
    // entra pela mesma interface via EvalJudgeFactory.
    private static async Task<IResult> JudgeAttemptAsync(
        EvalJudgeApiRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IEvalJudge judge,
        CancellationToken token)
    {
        var session = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (session is null) return Unauthorized();
        try
        {
            var verdict = await judge.JudgeAsync(
                new EvalJudgeRequest(
                    input.AcceptanceCriteria ?? [],
                    input.Diff ?? string.Empty,
                    input.Evidence ?? [],
                    (input.TestResults ?? []).Select(test => new EvalJudgeTestResult(
                        test.Name, test.Passed, test.EvidenceReference)).ToArray()),
                token);
            return Results.Ok(new EvalJudgeVerdictContract(
                verdict.Passed, verdict.Reason, verdict.Score, verdict.Provider));
        }
        catch (EvalJudgeUnavailableException exception)
        {
            return Results.Problem(statusCode: 503, title: "eval_judge_unavailable", detail: exception.Reason);
        }
        catch (ArgumentException exception)
        {
            return Invalid("invalid_eval_judge", exception.Message);
        }
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

        var scopeKind = isolatedSettings.FrontendRoleDefinitionKeys.Contains(
            definition.Key,
            StringComparer.OrdinalIgnoreCase)
            ? AgentPathScopeKind.FrontendSpecialist
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

// PLAT-04: novos contratos de resposta/requisição (endpoints novos — não alteram contratos existentes).
public sealed record FeatureMetricContract(
    string FeatureId, int TaskCount, int AttemptCount, int SuccessCount, int FailureCount,
    int InProgressCount, decimal TotalCostUsd, long TotalTokensInput, long TotalTokensOutput,
    long TotalDurationMs);
public sealed record FeatureMetricsResponse(string ProjectId, IReadOnlyList<FeatureMetricContract> Features);
public sealed record StuckTaskContract(
    string TaskId, string FeatureId, string Title, string Reason, int NoProgressStreak,
    string Detail, int AttemptCount);
public sealed record StuckTasksResponse(string ProjectId, IReadOnlyList<StuckTaskContract> Tasks);

// Fase 5: contratos das recomendações estatísticas (endpoint novo — não altera contratos
// existentes). Cada número deriva das tentativas gravadas e do espelho `model_invocations`.
public sealed record PerformanceAggregateContract(
    string TargetId, string? Model, string? Provider, int SampleSize, int SuccessCount,
    int FailureCount, double PassRate, double CompositeScore, double ConfidenceIntervalLower,
    double ConfidenceIntervalUpper, bool SampleSizeQualified);
public sealed record EvaluationRecommendationContract(
    string TargetId, string? Model, string? Provider, string Action, double Score,
    double ConfidenceIntervalLower, double ConfidenceIntervalUpper, int SampleSize,
    string RecommendationReason, DateTimeOffset GeneratedAt);
public sealed record EvaluationRecommendationsResponse(
    string ProjectId,
    IReadOnlyList<PerformanceAggregateContract> Aggregates,
    IReadOnlyList<EvaluationRecommendationContract> Recommendations);

// Fase 6: contratos da memória semântica consultável (endpoint novo). Cada slice carrega a
// citação e a proveniência gravadas na ingestão; o hash do snapshot torna a carga auditável.
public sealed record MemorySliceContract(
    string DocumentId, string DocumentType, string ProjectId, string Content, double Score,
    string CitationReference, IReadOnlyDictionary<string, string> Provenance);
public sealed record MemorySearchResponse(
    string SnapshotId, string SnapshotHash, int TotalTokens,
    IReadOnlyList<MemorySliceContract> Slices);

// Fase 10: contrato da medição de contenção do merge serializado.
public sealed record MergeContentionContract(
    long Enqueued, long Serialized, long Contended, int Waiting, int Active,
    double TotalWaitMs, double MaximumWaitMs, double ContentionRatio);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EvalJudgeApiRequest(
    IReadOnlyList<string>? AcceptanceCriteria,
    string? Diff,
    IReadOnlyList<string>? Evidence,
    IReadOnlyList<EvalJudgeTestApiContract>? TestResults);
public sealed record EvalJudgeTestApiContract(string Name, bool Passed, string EvidenceReference);
public sealed record EvalJudgeVerdictContract(bool Passed, string Reason, decimal Score, string Provider);
