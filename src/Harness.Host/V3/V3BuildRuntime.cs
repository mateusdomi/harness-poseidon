using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Host.Profiles;
using Harness.Host.WorkBoard;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Readiness.Contracts;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Providers;
using Harness.Persistence.Abstractions.Prototyping;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Providers;
using Harness.SharedKernel.Time;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Host.V3;

public static class V3BuildRuntimeEndpoints
{
    public static IEndpointRouteBuilder MapV3BuildRuntime(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v1/v3/projects/{projectId}/build").WithTags("v3-build-runtime");
        group.MapPost("/dispatch", DispatchAsync)
            .Produces<V3BuildExecutionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapGet("/status", GetStatusAsync)
            .Produces<V3BuildExecutionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);

        var validate = endpoints.MapGroup("/api/v1/v3/projects/{projectId}/validate").WithTags("v3-validation-runtime");
        validate.MapPost("/dispatch", DispatchValidationAsync)
            .Produces<V3BuildExecutionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);

        endpoints.MapGet("/api/v1/v3/build-executions/{executionId}", GetExecutionAsync)
            .WithTags("v3-build-runtime")
            .Produces<V3BuildExecutionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        endpoints.MapPost("/api/v1/v3/build-executions/{executionId}/human-answer", ContinueWithHumanAnswerAsync)
            .WithTags("v3-build-runtime")
            .Produces<V3BuildExecutionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        endpoints.MapPost("/api/v1/v3/build-executions/{executionId}/resume", ResumeExecutionAsync)
            .WithTags("v3-build-runtime")
            .Produces<V3BuildExecutionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);

        return endpoints;
    }

    private static async Task<IResult> DispatchAsync(
        string projectId,
        V3BuildDispatchRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IWorkBoardStore board,
        ISolicitationAttachmentStore attachments,
        SolicitationAttachmentStorage attachmentStorage,
        IDocumentCatalogStore documents,
        IDocumentContentCatalog documentContent,
        IPrototypeStore prototypes,
        Readiness.ProjectReadinessService readiness,
        [FromServices] AgentAccountRegistry accounts,
        IChannelLinkStore channelLinks,
        IConfiguration configuration,
        [FromServices] V3BuildRuntimeService runtime,
        CancellationToken token)
    {
        var resolved = await ResolveAsync(projectId, request, profiles, projects, token);
        if (resolved.Result is not null) return resolved.Result;

        var understandStore = V3UnderstandStore.ForConfiguration(configuration);
        var context = await V3ProjectContextBuilder.BuildAsync(
            resolved.Profile!, resolved.Project!, board, attachments, documents, documentContent, prototypes,
            attachmentStorage, readiness, accounts, channelLinks, understandStore, token);
        var mission = ResolveMission(understandStore, context.ProjectId, input.MissionId, "BUILD");
        if (mission is null) return Problem(404, "mission_not_found", "The requested BUILD mission does not exist.");

        var readinessOverall = context.Readiness.All(item => item.Status is "PASS" or "NOT_APPLICABLE")
            ? "READY"
            : "NOT_READY";
        var result = await runtime.DispatchAsync(
            new V3BuildDispatchCommand(context.ProjectId, context.State, mission, readinessOverall, accounts.List(), resolved.Profile!.TenantId),
            token);
        return result.Result is not null ? result.Result : Results.Ok(V3BuildExecutionResponse.From(result.Execution!));
    }

    private static async Task<IResult> DispatchValidationAsync(
        string projectId,
        V3BuildDispatchRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IWorkBoardStore board,
        ISolicitationAttachmentStore attachments,
        SolicitationAttachmentStorage attachmentStorage,
        IDocumentCatalogStore documents,
        IDocumentContentCatalog documentContent,
        IPrototypeStore prototypes,
        Readiness.ProjectReadinessService readiness,
        [FromServices] AgentAccountRegistry accounts,
        IChannelLinkStore channelLinks,
        IConfiguration configuration,
        [FromServices] V3BuildRuntimeService runtime,
        CancellationToken token)
    {
        var resolved = await ResolveAsync(projectId, request, profiles, projects, token);
        if (resolved.Result is not null) return resolved.Result;

        var understandStore = V3UnderstandStore.ForConfiguration(configuration);
        var context = await V3ProjectContextBuilder.BuildAsync(
            resolved.Profile!, resolved.Project!, board, attachments, documents, documentContent, prototypes,
            attachmentStorage, readiness, accounts, channelLinks, understandStore, token);
        var mission = ResolveMission(understandStore, context.ProjectId, input.MissionId, "VALIDATE");
        if (mission is null) return Problem(404, "mission_not_found", "The requested VALIDATE mission does not exist.");

        var result = await runtime.DispatchAsync(
            new V3BuildDispatchCommand(context.ProjectId, context.State, mission, "READY", accounts.List(), resolved.Profile!.TenantId),
            token);
        return result.Result is not null ? result.Result : Results.Ok(V3BuildExecutionResponse.From(result.Execution!));
    }

    private static async Task<IResult> GetStatusAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IConfiguration configuration,
        CancellationToken token)
    {
        var resolved = await ResolveAsync(projectId, request, profiles, projects, token);
        if (resolved.Result is not null) return resolved.Result;
        var execution = V3BuildRuntimeStore.ForConfiguration(configuration).LatestForProject(projectId);
        return execution is null ? Problem(404, "build_execution_not_found", "No BUILD execution exists for this project.") : Results.Ok(V3BuildExecutionResponse.From(execution));
    }

    private static async Task<IResult> GetExecutionAsync(
        string executionId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IConfiguration configuration,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(executionId, out _)) return Problem(400, "invalid_execution_id", "Execution ID must be a ULID.");
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        var execution = V3BuildRuntimeStore.ForConfiguration(configuration).ReadExecution(executionId);
        return execution is null ? Problem(404, "build_execution_not_found", "The requested BUILD execution does not exist.") : Results.Ok(V3BuildExecutionResponse.From(execution));
    }

    private static async Task<IResult> ContinueWithHumanAnswerAsync(
        string executionId,
        V3BuildHumanAnswerRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        [FromServices] AgentAccountRegistry accounts,
        IConfiguration configuration,
        [FromServices] V3BuildRuntimeService runtime,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(executionId, out _)) return Problem(400, "invalid_execution_id", "Execution ID must be a ULID.");
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        var execution = V3BuildRuntimeStore.ForConfiguration(configuration).ReadExecution(executionId);
        if (execution is null) return Problem(404, "build_execution_not_found", "The requested BUILD execution does not exist.");
        var result = await runtime.ContinueWithHumanAnswerAsync(execution, input.Answer, accounts.List(), token);
        return result.Result is not null ? result.Result : Results.Ok(V3BuildExecutionResponse.From(result.Execution!));
    }

    private static async Task<IResult> ResumeExecutionAsync(
        string executionId,
        HttpRequest request,
        ILocalProfileStore profiles,
        [FromServices] AgentAccountRegistry accounts,
        IConfiguration configuration,
        [FromServices] V3BuildRuntimeService runtime,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(executionId, out _)) return Problem(400, "invalid_execution_id", "Execution ID must be a ULID.");
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        var execution = V3BuildRuntimeStore.ForConfiguration(configuration).ReadExecution(executionId);
        if (execution is null) return Problem(404, "build_execution_not_found", "The requested BUILD execution does not exist.");
        var result = await runtime.ResumeExecutionAsync(execution, accounts.List(), "local", token);
        return result.Result is not null ? result.Result : Results.Ok(V3BuildExecutionResponse.From(result.Execution!));
    }

    private static V3BuildMissionRecord? ResolveMission(
        V3UnderstandStore store,
        string projectId,
        string? missionId,
        string missionType)
    {
        if (!string.IsNullOrWhiteSpace(missionId))
        {
            var mission = store.ReadMission(missionId.Trim());
            return string.Equals(mission?.MissionType, missionType, StringComparison.OrdinalIgnoreCase)
                ? mission
                : null;
        }

        return store.ListMissions(projectId).FirstOrDefault(mission =>
            string.Equals(mission.MissionType, missionType, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(IResult? Result, LocalProfileRecord? Profile, ProjectRecord? Project)> ResolveAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _))
        {
            return (Problem(400, "invalid_project_id", "Project ID must be a ULID."), null, null);
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return (SessionRequired(), null, null);
        var project = await projects.GetAsync(profile.TenantId, projectId, token);
        return project is null
            ? (Problem(404, "project_not_found", "The requested project does not exist."), profile, null)
            : (null, profile, project);
    }

    private static IResult SessionRequired() =>
        Problem(401, "local_session_required", "A local profile session is required.");

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail);
}

public sealed class V3BuildRuntimeService(
    V3BuildRuntimeStore store,
    V3UnderstandStore understandStore,
    IV3BuildExecutor executor,
    IClock clock,
    IModelInvocationStore? invocations = null)
{
    private const int MaxContinueWithoutProgress = 2;
    private const int MaxTransientAttemptsPerExecutor = 3;
    private const int MaxTransientContinuationsPerExecution = 8;

    public async Task<V3BuildRuntimeResult> DispatchAsync(V3BuildDispatchCommand command, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        var missionType = command.Mission.MissionType.ToUpperInvariant();
        if (missionType is not "BUILD" and not "VALIDATE" and not "PLATFORM_MAINTENANCE")
        {
            return Conflict("mission_type_unsupported", "Mission execution supports BUILD, VALIDATE and PLATFORM_MAINTENANCE only.");
        }

        if (missionType == "BUILD" && command.State?.AuthorizedAt is null)
        {
            return Conflict("build_not_authorized", "BUILD dispatch requires explicit authorization.");
        }

        if (missionType == "BUILD" &&
            !string.Equals(command.ReadinessOverall, "READY", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict("build_readiness_not_ready", "BUILD dispatch requires V3 readiness READY.");
        }

        if (missionType == "VALIDATE" &&
            !string.Equals(command.State?.LifecycleState, "VALIDATING", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict("validation_not_reached", "VALIDATION dispatch requires lifecycle VALIDATING.");
        }

        if (!string.Equals(command.Mission.Status, "COMPILED", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict("mission_not_compiled", $"{missionType} dispatch requires a COMPILED mission.");
        }

        if (missionType is "BUILD" or "VALIDATE" &&
            command.Mission.PrimaryRequirementsCoverage.Any(source => !source.Complete))
        {
            return Conflict("primary_requirements_incomplete", "BUILD dispatch requires 100% primary requirement coverage.");
        }

        if (string.IsNullOrWhiteSpace(command.Mission.Repository) ||
            !Directory.Exists(command.Mission.Repository))
        {
            return Conflict("repository_unreachable", "BUILD dispatch requires a local reachable repository.");
        }

        var selected = V3BuildExecutorSelector.Select(
            command.Accounts,
            command.Mission.RecommendedExecutor.AccountAlias,
            requiredRole: RequiredRoleForMission(missionType));
        if (selected.Account is null)
        {
            UpdateLifecycle(command.State, "PAUSED_QUOTA", "NO_EXECUTOR");
            return Conflict("executor_not_available", selected.Reason);
        }

        var now = clock.UtcNow;
        var initialSnapshot = V3GitSnapshot.Capture(command.Mission.Repository);
        var execution = new V3BuildExecutionRecord(
            UlidValue.New(now).ToString(),
            command.Mission.MissionId,
            command.ProjectId,
            missionType,
            selected.Account.Alias,
            selected.Account.ProviderKind,
            "DISPATCHED",
            now,
            now,
            null,
            initialSnapshot.Head,
            initialSnapshot.Head,
            initialSnapshot.CommitCount,
            0,
            null,
            0,
            0,
            null,
            null,
            null,
            null,
            [],
            [
                new V3BuildExecutionEvent($"{missionType}_DISPATCHED", now, selected.Account.Alias, $"Initial {missionType} mission dispatch."),
            ]);
        store.WriteExecution(execution);
        UpdateLifecycle(command.State, RunningLifecycleFor(missionType), $"{missionType}_RUNNING");
        var running = execution with
        {
            Status = "RUNNING",
            Events = Append(execution.Events, $"{missionType}_STARTED", selected.Account.Alias, "Executor started."),
        };
        store.WriteExecution(running);
        understandStore.WriteMission(command.Mission with { Status = "RUNNING" });
        return await RunLoopAsync(running, command.Mission, selected.Account, command.Accounts, command.State, null, command.TenantId, CancellationToken.None);
    }

    public async Task<IReadOnlyList<V3BuildExecutionRecord>> RecoverRunningExecutionsAsync(
        IReadOnlyList<AgentAccountContract> accounts,
        CancellationToken token)
    {
        var recovered = new List<V3BuildExecutionRecord>();
        foreach (var execution in store.ListExecutions()
                     .Where(item => string.Equals(item.Status, "RUNNING", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            var mission = understandStore.ReadMission(execution.MissionId);
            var state = understandStore.ReadProject(execution.ProjectId);
            var now = clock.UtcNow;
            if (mission is null ||
                string.IsNullOrWhiteSpace(mission.Repository) ||
                !Directory.Exists(mission.Repository))
            {
                var blocked = execution with
                {
                    Status = "BLOCKED",
                    CompletedAt = now,
                    LastFailureCode = mission is null ? "mission_not_found" : "repository_unreachable",
                    Events = Append(execution.Events, "BUILD_RECOVERY_BLOCKED", null,
                        mission is null
                            ? "Startup recovery found a RUNNING execution without its original mission."
                            : "Startup recovery found a RUNNING execution with an unreachable repository."),
                };
                store.WriteExecution(blocked);
                UpdateLifecycle(state, "BLOCKED", "RECOVERY_BLOCKED");
                recovered.Add(blocked);
                continue;
            }

            var selected = V3BuildExecutorSelector.Select(
                accounts,
                execution.ExecutorAccountId,
                requiredRole: RequiredRoleForMission(execution.MissionType));
            if (selected.Account is null)
            {
                var paused = execution with
                {
                    Status = "PAUSED_QUOTA",
                    LastActivityAt = now,
                    LastFailureCode = "executor_not_available",
                    QuotaState = "NO_EXECUTOR",
                    Events = Append(execution.Events, "BUILD_RECOVERY_PAUSED", null,
                        "Startup recovery found a RUNNING execution but no compatible executor is available."),
                };
                store.WriteExecution(paused);
                UpdateLifecycle(state, "PAUSED_QUOTA", "RECOVERY_PAUSED");
                recovered.Add(paused);
                continue;
            }

            var stalled = execution with
            {
                Status = "STALLED",
                LastActivityAt = now,
                LastFailureCode = "startup_recovery_requires_explicit_resume",
                Events = Append(execution.Events, "BUILD_RECOVERY_STALLED", selected.Account.Alias,
                    "Startup recovery found a persisted RUNNING execution without a live process. It was reconciled as STALLED to avoid phantom RUNNING state during Host boot."),
            };
            store.WriteExecution(stalled);
            UpdateLifecycle(state, "BLOCKED", "RECOVERY_STALLED");
            recovered.Add(stalled);
        }

        return recovered;
    }

    public Task<V3BuildRuntimeResult> ContinueWithHumanAnswerAsync(
        V3BuildExecutionRecord execution,
        string answer,
        IReadOnlyList<AgentAccountContract> accounts,
        CancellationToken token)
    {
        if (!string.Equals(execution.Status, "BLOCKED", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Conflict("build_not_human_blocked", "Human answer can only resume a BLOCKED BUILD execution."));
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            return Task.FromResult(Conflict("human_answer_required", "A human answer is required to continue."));
        }

        var mission = understandStore.ReadMission(execution.MissionId);
        if (mission is null)
        {
            return Task.FromResult(Conflict("mission_not_found", "Original BUILD mission was not found."));
        }

        var state = understandStore.ReadProject(execution.ProjectId) ??
            V3ProjectUnderstandState.Create(execution.ProjectId, clock.UtcNow);
        var account = accounts.FirstOrDefault(value =>
            string.Equals(value.Alias, execution.ExecutorAccountId, StringComparison.OrdinalIgnoreCase)) ??
            V3BuildExecutorSelector.Select(accounts, requiredRole: RequiredRoleForMission(execution.MissionType)).Account;
        if (account is null)
        {
            UpdateLifecycle(state, "PAUSED_QUOTA", "PAUSED_QUOTA");
            return Task.FromResult(Conflict("executor_not_available", "No compatible executor is available to resume after the human answer."));
        }

        var resumed = execution with
        {
            Status = "RUNNING",
            HumanBlocker = null,
            LastActivityAt = clock.UtcNow,
            Events = Append(execution.Events, $"{execution.MissionType.ToUpperInvariant()}_CONTINUED", account.Alias, "Human answer registered; execution continued."),
        };
        store.WriteExecution(resumed);
        UpdateLifecycle(state, RunningLifecycleFor(execution.MissionType), $"{execution.MissionType.ToUpperInvariant()}_RUNNING");
        return RunLoopAsync(resumed, mission, account, accounts, state, BuildHumanAnswerPrompt(answer, mission.MissionType), "local", CancellationToken.None);
    }

    public Task<V3BuildRuntimeResult> ResumeExecutionAsync(
        V3BuildExecutionRecord execution,
        IReadOnlyList<AgentAccountContract> accounts,
        string tenantId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!string.Equals(execution.Status, "RUNNING", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(execution.Status, "STALLED", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(execution.Status, "PAUSED_PROVIDER", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Conflict("build_resume_not_allowed", "Only RUNNING, STALLED or PAUSED_PROVIDER executions can be resumed by recovery."));
        }

        var mission = understandStore.ReadMission(execution.MissionId);
        if (mission is null)
        {
            return Task.FromResult(Conflict("mission_not_found", "Original mission was not found."));
        }

        if (string.IsNullOrWhiteSpace(mission.Repository) ||
            !Directory.Exists(mission.Repository))
        {
            return Task.FromResult(Conflict("repository_unreachable", "Mission repository is unreachable."));
        }

        var state = understandStore.ReadProject(execution.ProjectId) ??
            V3ProjectUnderstandState.Create(execution.ProjectId, clock.UtcNow);
        var account = SelectResumeAccount(execution, accounts);
        if (account is null)
        {
            var paused = execution with
            {
                Status = "PAUSED_QUOTA",
                LastActivityAt = clock.UtcNow,
                LastFailureCode = "executor_not_available",
                QuotaState = "NO_EXECUTOR",
                Events = Append(execution.Events, $"{MissionPrefix(execution)}_RECOVERY_PAUSED", null,
                    "Recovery resume requested but no compatible executor is available."),
            };
            store.WriteExecution(paused);
            UpdateLifecycle(state, "PAUSED_QUOTA", "RECOVERY_PAUSED");
            return Task.FromResult(new V3BuildRuntimeResult(paused, null));
        }

        var resumed = execution with
        {
            Status = "RUNNING",
            ExecutorAccountId = account.Alias,
            Provider = account.ProviderKind,
            LastActivityAt = clock.UtcNow,
            CompletedAt = null,
            QuotaState = null,
            Events = Append(execution.Events, $"{MissionPrefix(execution)}_RECOVERY_RESUMED", account.Alias,
                "Execution resumed from persisted state after orphan/stall/provider pause."),
        };
        store.WriteExecution(resumed);
        UpdateLifecycle(state, RunningLifecycleFor(execution.MissionType), $"{execution.MissionType.ToUpperInvariant()}_RUNNING");
        return RunLoopAsync(
            resumed,
            mission,
            account,
            accounts,
            state,
            BuildRecoveryPrompt(execution, execution.MissionType),
            tenantId,
            CancellationToken.None);
    }

    private async Task<V3BuildRuntimeResult> RunLoopAsync(
        V3BuildExecutionRecord start,
        V3BuildMissionRecord mission,
        AgentAccountContract account,
        IReadOnlyList<AgentAccountContract> accounts,
        V3ProjectUnderstandState? state,
        string? oneShotContinuationPrompt,
        string tenantId,
        CancellationToken token)
    {
        var execution = start;
        var active = account;
        var previousSnapshot = V3GitSnapshot.Capture(mission.Repository!);
        var continuationPrompt = oneShotContinuationPrompt;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var prompt = continuationPrompt ?? (execution.ContinueCount == 0
                ? BuildInitialPrompt(mission)
                : BuildContinuePrompt(mission.MissionType));
            continuationPrompt = null;
            var outcome = await executor.RunAsync(
                active,
                new V3BuildExecutionPrompt(prompt, execution.ContinueCount > 0, null),
                mission.Repository!,
                execution.SessionId,
                token);
            var now = clock.UtcNow;
            var currentSnapshot = V3GitSnapshot.Capture(mission.Repository!);
            var lastOutput = SanitizeOutput(outcome.Output);
            execution = execution with
            {
                LastActivityAt = now,
                CurrentHead = currentSnapshot.Head,
                CommitDelta = Math.Max(0, currentSnapshot.CommitCount - execution.InitialCommitCount),
                LastOutput = lastOutput,
                SessionId = outcome.SessionId ?? execution.SessionId,
                LastCheckpointSummary = ExtractCheckpoint(lastOutput) ?? execution.LastCheckpointSummary,
            };
            var statusForClassification = outcome.FailureKind == ExternalFailureKind.Unknown
                ? outcome.Status
                : ExternalAgentRunStatus.Failed;
            var classifiedOutcome = AgentRunOutcomeClassifier.Classify(
                statusForClassification,
                outcome.FailureKind,
                outcome.FailureCode,
                lastOutput,
                now);
            await RecordV3InvocationAsync(tenantId, active, execution, outcome, classifiedOutcome, now, token);

            if (classifiedOutcome.Kind == AgentRunOutcomeKind.QuotaExhausted)
            {
                var failover = V3BuildExecutorSelector.Select(
                    accounts,
                    excludeAliases: [active.Alias],
                    requiredRole: RequiredRoleForMission(mission.MissionType));
                if (failover.Account is null)
                {
                    execution = execution with
                    {
                        Status = "PAUSED_QUOTA",
                        LastFailureCode = outcome.FailureCode ?? classifiedOutcome.ReasonCode,
                        QuotaState = "EXHAUSTED",
                        Events = Append(execution.Events, $"{MissionPrefix(mission)}_QUOTA_PAUSED", active.Alias, "Quota exhausted; no alternate executor available."),
                    };
                    store.WriteExecution(execution);
                    if (state is not null) UpdateLifecycle(state, "PAUSED_QUOTA", "PAUSED_QUOTA");
                    return new V3BuildRuntimeResult(execution, null);
                }

                var continuation = new V3BuildContinuationRecord(active.Alias, failover.Account.Alias, "QUOTA_FAILOVER", now);
                active = failover.Account;
                execution = execution with
                {
                    ExecutorAccountId = active.Alias,
                    Provider = active.ProviderKind,
                    SessionId = null,
                    Status = "RUNNING",
                    ContinueCount = execution.ContinueCount + 1,
                    ConsecutiveNoProgressCount = 0,
                    Continuations = [.. execution.Continuations, continuation],
                    Events = Append(execution.Events, $"{MissionPrefix(mission)}_FAILOVER", active.Alias, "Quota failover continued on another executor."),
                };
                store.WriteExecution(execution);
                continuationPrompt = BuildQuotaFailoverPrompt(mission, execution.LastOutput, previousSnapshot, currentSnapshot);
                previousSnapshot = currentSnapshot;
                continue;
            }

            if (classifiedOutcome.Kind == AgentRunOutcomeKind.AuthenticationRequired)
            {
                execution = execution with
                {
                    Status = "PAUSED_QUOTA",
                    LastFailureCode = outcome.FailureCode ?? classifiedOutcome.ReasonCode,
                    QuotaState = "AUTH_REQUIRED",
                    Events = Append(execution.Events, $"{MissionPrefix(mission)}_QUOTA_PAUSED", active.Alias, "Executor authentication required."),
                };
                store.WriteExecution(execution);
                if (state is not null) UpdateLifecycle(state, "PAUSED_QUOTA", "PAUSED_QUOTA");
                return new V3BuildRuntimeResult(execution, null);
            }

            if (classifiedOutcome.Kind == AgentRunOutcomeKind.Transient)
            {
                var transientContinuations = TransientContinuationCount(execution);
                var failureCode = outcome.FailureCode ?? classifiedOutcome.ReasonCode;
                if (transientContinuations >= MaxTransientContinuationsPerExecution)
                {
                    execution = execution with
                    {
                        Status = "PAUSED_PROVIDER",
                        LastFailureCode = failureCode,
                        QuotaState = "PROVIDER_TRANSPORT_TRANSIENT",
                        ConsecutiveNoProgressCount = 0,
                        Events = Append(
                            execution.Events,
                            $"{MissionPrefix(mission)}_PROVIDER_PAUSED",
                            active.Alias,
                            "Provider transport/transient failure exceeded the bounded retry/failover budget."),
                    };
                    store.WriteExecution(execution);
                    if (state is not null) UpdateLifecycle(state, "BLOCKED", "PROVIDER_TRANSPORT_TRANSIENT");
                    return new V3BuildRuntimeResult(execution, null);
                }

                var transientAttempts = TransientAttemptsFor(execution, active.Alias) + 1;
                if (transientAttempts < MaxTransientAttemptsPerExecutor)
                {
                    execution = execution with
                    {
                        Status = "RUNNING",
                        LastFailureCode = failureCode,
                        QuotaState = "PROVIDER_TRANSPORT_TRANSIENT",
                        ContinueCount = execution.ContinueCount + 1,
                        ConsecutiveNoProgressCount = 0,
                        Events = Append(
                            execution.Events,
                            $"{MissionPrefix(mission)}_TRANSIENT_RETRY",
                            active.Alias,
                            $"Provider transport/transient failure; retry {transientAttempts + 1}/{MaxTransientAttemptsPerExecutor} on the same executor."),
                    };
                    store.WriteExecution(execution);
                    continuationPrompt = BuildTransientRetryPrompt(mission, lastOutput, transientAttempts + 1);
                    previousSnapshot = currentSnapshot;
                    continue;
                }

                var failover = V3BuildExecutorSelector.Select(
                    accounts,
                    excludeAliases: [active.Alias],
                    requiredRole: RequiredRoleForMission(mission.MissionType));
                if (failover.Account is not null)
                {
                    var continuation = new V3BuildContinuationRecord(active.Alias, failover.Account.Alias, "PROVIDER_TRANSPORT_FAILOVER", now);
                    active = failover.Account;
                    execution = execution with
                    {
                        ExecutorAccountId = active.Alias,
                        Provider = active.ProviderKind,
                        SessionId = null,
                        Status = "RUNNING",
                        LastFailureCode = failureCode,
                        QuotaState = "PROVIDER_TRANSPORT_TRANSIENT",
                        ContinueCount = execution.ContinueCount + 1,
                        ConsecutiveNoProgressCount = 0,
                        Continuations = [.. execution.Continuations, continuation],
                        Events = Append(
                            execution.Events,
                            $"{MissionPrefix(mission)}_TRANSIENT_FAILOVER",
                            active.Alias,
                            "Persistent provider transport/transient failure; continued on another eligible executor."),
                    };
                    store.WriteExecution(execution);
                    continuationPrompt = BuildProviderFailoverPrompt(mission, lastOutput, previousSnapshot, currentSnapshot);
                    previousSnapshot = currentSnapshot;
                    continue;
                }

                execution = execution with
                {
                    Status = "PAUSED_PROVIDER",
                    LastFailureCode = failureCode,
                    QuotaState = "PROVIDER_TRANSPORT_TRANSIENT",
                    ConsecutiveNoProgressCount = 0,
                    Events = Append(
                        execution.Events,
                        $"{MissionPrefix(mission)}_PROVIDER_PAUSED",
                        active.Alias,
                        "Persistent provider transport/transient failure; no alternate executor available."),
                };
                store.WriteExecution(execution);
                if (state is not null) UpdateLifecycle(state, "BLOCKED", "PROVIDER_TRANSPORT_TRANSIENT");
                return new V3BuildRuntimeResult(execution, null);
            }

            if (lastOutput.Contains("POSEIDON_HUMAN_BLOCKER", StringComparison.Ordinal))
            {
                execution = execution with
                {
                    Status = "BLOCKED",
                    CompletedAt = now,
                    HumanBlocker = ExtractAfterMarker(lastOutput, "POSEIDON_HUMAN_BLOCKER"),
                    Events = Append(execution.Events, $"{MissionPrefix(mission)}_HUMAN_BLOCKED", active.Alias, "Executor declared a human blocker."),
                };
                store.WriteExecution(execution);
                if (state is not null) UpdateLifecycle(state, "BLOCKED", "BLOCKED");
                return new V3BuildRuntimeResult(execution, null);
            }

            var completionMarker = CompletionMarker(mission.MissionType);
            if (lastOutput.Contains(completionMarker, StringComparison.Ordinal))
            {
                if (RequiresCleanWorktreeForCompletion(mission.MissionType) &&
                    V3GitSnapshot.HasUncommittedChanges(mission.Repository!))
                {
                    var rejectedCount = CompletionRejectedCount(execution);
                    if (rejectedCount + 1 >= MaxContinueWithoutProgress)
                    {
                        execution = execution with
                        {
                            Status = "STALLED",
                            CompletedAt = now,
                            LastFailureCode = "completion_requires_clean_worktree",
                            ConsecutiveNoProgressCount = rejectedCount + 1,
                            Events = Append(execution.Events, $"{MissionPrefix(mission)}_STALLED", active.Alias,
                                "Executor declared completion but the repository still has uncommitted changes after recovery attempts."),
                        };
                        store.WriteExecution(execution);
                        if (state is not null) UpdateLifecycle(state, "BLOCKED", "COMPLETION_REQUIRES_CLEAN_WORKTREE");
                        return new V3BuildRuntimeResult(execution, null);
                    }

                    execution = execution with
                    {
                        Status = "RUNNING",
                        LastFailureCode = "completion_requires_clean_worktree",
                        ContinueCount = execution.ContinueCount + 1,
                        ConsecutiveNoProgressCount = rejectedCount + 1,
                        Events = Append(execution.Events, $"{MissionPrefix(mission)}_COMPLETION_REJECTED", active.Alias,
                            "Executor declared completion but the repository still has uncommitted changes."),
                    };
                    store.WriteExecution(execution);
                    continuationPrompt = BuildCompletionRequiresCommitPrompt(mission);
                    previousSnapshot = currentSnapshot;
                    continue;
                }

                var validationReport = mission.MissionType.Equals("VALIDATE", StringComparison.OrdinalIgnoreCase)
                    ? V3ValidationReport.Parse(lastOutput, currentSnapshot.Head)
                    : null;
                if (validationReport is not null && validationReport.HasBlockingFailures)
                {
                    execution = execution with
                    {
                        Status = "BLOCKED",
                        CompletedAt = now,
                        LastFailureCode = "validation_failures_remaining",
                        ValidationReport = validationReport,
                        Events = Append(execution.Events, "VALIDATION_BLOCKED", active.Alias, "Validation completion marker included blocking failures."),
                    };
                    store.WriteExecution(execution);
                    if (state is not null) UpdateLifecycle(state, "BLOCKED", "VALIDATION_BLOCKED");
                    return new V3BuildRuntimeResult(execution, null);
                }

                execution = execution with
                {
                    Status = "COMPLETED",
                    CompletedAt = now,
                    FinalReport = lastOutput,
                    ValidationReport = validationReport,
                    ConsecutiveNoProgressCount = 0,
                    Events = Append(execution.Events, $"{MissionPrefix(mission)}_COMPLETED", active.Alias, $"Executor declared {mission.MissionType.ToUpperInvariant()} complete."),
                };
                store.WriteExecution(execution);
                understandStore.WriteMission(mission with { Status = "COMPLETED" });
                if (state is not null)
                {
                    UpdateLifecycle(
                        state,
                        mission.MissionType.Equals("VALIDATE", StringComparison.OrdinalIgnoreCase)
                            ? "READY_FOR_HUMAN_ACCEPTANCE"
                            : CompletedLifecycleFor(mission.MissionType),
                        $"{mission.MissionType.ToUpperInvariant()}_COMPLETED");
                }
                return new V3BuildRuntimeResult(execution, null);
            }

            var progressed = currentSnapshot.HasProgressComparedTo(previousSnapshot);
            previousSnapshot = currentSnapshot;
            var noProgressCount = progressed ? 0 : execution.ConsecutiveNoProgressCount + 1;
            if (noProgressCount >= MaxContinueWithoutProgress)
            {
                execution = execution with
                {
                    Status = "STALLED",
                    CompletedAt = now,
                    ConsecutiveNoProgressCount = noProgressCount,
                    Events = Append(execution.Events, $"{MissionPrefix(mission)}_STALLED", active.Alias, "Two consecutive continuations ended without completion, blocker, quota or repository progress."),
                };
                store.WriteExecution(execution);
                if (state is not null) UpdateLifecycle(state, "BLOCKED", "STALLED");
                return new V3BuildRuntimeResult(execution, null);
            }

            execution = execution with
            {
                Status = "RUNNING",
                ContinueCount = execution.ContinueCount + 1,
                ConsecutiveNoProgressCount = noProgressCount,
                Events = Append(execution.Events, $"{MissionPrefix(mission)}_CONTINUED", active.Alias, progressed
                    ? "Execution ended without completion marker but repository progress was detected."
                    : "Execution ended without completion marker; one deterministic continuation queued."),
            };
            store.WriteExecution(execution);
        }
    }

    private void UpdateLifecycle(V3ProjectUnderstandState? state, string lifecycle, string status)
    {
        if (state is null) return;
        understandStore.WriteProject(state with
        {
            LifecycleState = lifecycle,
            Status = status,
            UpdatedAt = clock.UtcNow,
        });
    }

    private static V3BuildRuntimeResult Conflict(string code, string detail) =>
        new(null, Results.Problem(statusCode: 409, title: code, detail: detail));

    private IReadOnlyList<V3BuildExecutionEvent> Append(
        IReadOnlyList<V3BuildExecutionEvent> events,
        string type,
        string? actor,
        string detail) =>
        [.. events, new V3BuildExecutionEvent(type, clock.UtcNow, actor, detail)];

    private static string MissionPrefix(V3BuildMissionRecord mission) => MissionPrefix(mission.MissionType);

    private static string CompletionMarker(string missionType) =>
        missionType.Equals("VALIDATE", StringComparison.OrdinalIgnoreCase)
            ? "POSEIDON_VALIDATION_COMPLETE"
            : "POSEIDON_MISSION_COMPLETE";

    private static string BuildInitialPrompt(V3BuildMissionRecord mission) =>
        mission.MissionText + Environment.NewLine + Environment.NewLine + ExitContract(mission.MissionType);

    private static string BuildContinuePrompt(string missionType) =>
        """
        Continue a missão original autonomamente.

        Você ainda não declarou o marcador de conclusão correto.

        Inspecione o estado atual do repositório e continue de onde parou.

        Não recomece trabalho já concluído.

        Somente encerre ao satisfazer a Definition of Done ou encontrar um blocker genuinamente humano.

        """ + Environment.NewLine + ExitContract(missionType);

    private static string BuildQuotaFailoverPrompt(
        V3BuildMissionRecord mission,
        string? lastOutput,
        V3GitSnapshot before,
        V3GitSnapshot after) =>
        $"""
        Você está continuando uma BuildMission em andamento.

        Original MissionId: {mission.MissionId}
        Repository: {mission.Repository}
        Current HEAD: {after.Head ?? "unknown"}
        Previous HEAD: {before.Head ?? "unknown"}
        Last output from previous executor:
        {lastOutput ?? "(sem saída registrada)"}

        Não recomece o projeto.
        Leia a missão original.
        Inspecione o repositório e os commits atuais.
        Preserve o trabalho válido.
        Continue exatamente de onde o executor anterior parou.

        """ + Environment.NewLine + ExitContract(mission.MissionType);

    private static string BuildTransientRetryPrompt(
        V3BuildMissionRecord mission,
        string? lastOutput,
        int attempt) =>
        $"""
        A tentativa anterior encontrou uma falha transitória do provider/transporte.

        Attempt: {attempt}
        MissionId: {mission.MissionId}

        Última saída observada:
        {lastOutput ?? "(sem saída registrada)"}

        Não recomece o projeto.
        Inspecione o repositório atual, preserve trabalho válido e continue a mesma missão.

        """ + Environment.NewLine + ExitContract(mission.MissionType);

    private static string BuildProviderFailoverPrompt(
        V3BuildMissionRecord mission,
        string? lastOutput,
        V3GitSnapshot before,
        V3GitSnapshot after) =>
        $"""
        Você está continuando uma missão em andamento após falha transitória persistente do provider anterior.

        Original MissionId: {mission.MissionId}
        Repository: {mission.Repository}
        Current HEAD: {after.Head ?? "unknown"}
        Previous HEAD: {before.Head ?? "unknown"}
        Last output from previous executor:
        {lastOutput ?? "(sem saída registrada)"}

        Não recomece o projeto.
        Leia a missão original.
        Inspecione o repositório e os commits atuais.
        Preserve o trabalho válido.
        Continue exatamente de onde a execução anterior parou.

        """ + Environment.NewLine + ExitContract(mission.MissionType);

    private static string BuildCompletionRequiresCommitPrompt(V3BuildMissionRecord mission) =>
        $"""
        Você declarou o marcador de conclusão da missão, mas o runtime detectou alterações não commitadas no repositório.

        MissionId: {mission.MissionId}
        Repository: {mission.Repository}

        A missão ainda NÃO está concluída.

        Inspecione `git status`.
        Preserve o trabalho válido.
        Faça commits úteis das alterações de produto, testes e documentação mínima.
        Remova ou ignore somente artefatos gerados que não devem entrar no Git.
        Rode os gates proporcionais novamente se o commit alterar código.

        Só finalize com o marcador correto depois que o repositório estiver em estado limpo ou houver um blocker genuinamente humano.

        """ + Environment.NewLine + ExitContract(mission.MissionType);

    private static string BuildHumanAnswerPrompt(string answer, string missionType) =>
        $"""
        O humano respondeu ao blocker anterior:

        {answer.Trim()}

        Retome a missão original. Preserve o trabalho válido e continue até o marcador de conclusão correto ou novo POSEIDON_HUMAN_BLOCKER.

        """ + Environment.NewLine + ExitContract(missionType);

    private static int TransientAttemptsFor(V3BuildExecutionRecord execution, string alias)
    {
        var prefix = MissionPrefix(execution);
        return execution.Events.Count(item =>
            string.Equals(item.Actor, alias, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Type, $"{prefix}_TRANSIENT_RETRY", StringComparison.Ordinal));
    }

    private static int CompletionRejectedCount(V3BuildExecutionRecord execution)
    {
        var prefix = MissionPrefix(execution);
        return execution.Events.Count(item =>
            string.Equals(item.Type, $"{prefix}_COMPLETION_REJECTED", StringComparison.Ordinal));
    }

    private static int TransientContinuationCount(V3BuildExecutionRecord execution)
    {
        var prefix = MissionPrefix(execution);
        return execution.Events.Count(item =>
            string.Equals(item.Type, $"{prefix}_TRANSIENT_RETRY", StringComparison.Ordinal) ||
            string.Equals(item.Type, $"{prefix}_TRANSIENT_FAILOVER", StringComparison.Ordinal));
    }

    private async Task RecordV3InvocationAsync(
        string tenantId,
        AgentAccountContract account,
        V3BuildExecutionRecord execution,
        V3BuildExecutorOutcome outcome,
        AgentRunOutcome classifiedOutcome,
        DateTimeOffset now,
        CancellationToken token)
    {
        if (invocations is null)
        {
            return;
        }

        var usage = outcome.Usage;
        var outcomeCode = $"{execution.MissionType.ToLowerInvariant()}:{classifiedOutcome.Kind.ToString().ToLowerInvariant()}";
        if (usage is null)
        {
            outcomeCode += "|usage_unknown";
        }
        else if (usage.Precision == ExternalAgentUsagePrecision.Estimated)
        {
            outcomeCode += "|usage_estimated";
        }

        await invocations.RecordInvocationAsync(
            new ModelInvocationRecord(
                UlidValue.New(now).ToString(),
                tenantId,
                execution.ProjectId,
                execution.MissionId,
                execution.MissionExecutionId,
                account.ProviderKind,
                string.Empty,
                account.Alias,
                (int)(usage?.InputTokens ?? 0),
                (int)(usage?.OutputTokens ?? 0),
                usage?.CostUsd ?? 0m,
                outcome.DurationMs ?? 0,
                outcomeCode,
                now),
            token);
    }

    private static string MissionPrefix(V3BuildExecutionRecord execution) =>
        MissionPrefix(execution.MissionType);

    private static string MissionPrefix(string missionType) =>
        missionType.Equals("VALIDATE", StringComparison.OrdinalIgnoreCase)
            ? "VALIDATION"
            : missionType.Equals("PLATFORM_MAINTENANCE", StringComparison.OrdinalIgnoreCase)
                ? "PLATFORM_MAINTENANCE"
                : "BUILD";

    private static string RequiredRoleForMission(string missionType) =>
        missionType.Equals("PLATFORM_MAINTENANCE", StringComparison.OrdinalIgnoreCase)
            ? AgentRoles.PlatformMaintainer
            : AgentRoles.ProjectExecutor;

    private static bool RequiresCleanWorktreeForCompletion(string missionType) =>
        missionType.Equals("BUILD", StringComparison.OrdinalIgnoreCase) ||
        missionType.Equals("VALIDATE", StringComparison.OrdinalIgnoreCase) ||
        missionType.Equals("PLATFORM_MAINTENANCE", StringComparison.OrdinalIgnoreCase);

    private static AgentAccountContract? SelectResumeAccount(
        V3BuildExecutionRecord execution,
        IReadOnlyList<AgentAccountContract> accounts)
    {
        var preferred = V3BuildExecutorSelector.Select(
            accounts,
            execution.ExecutorAccountId,
            requiredRole: RequiredRoleForMission(execution.MissionType));
        return preferred.Account ??
            V3BuildExecutorSelector.Select(accounts, requiredRole: RequiredRoleForMission(execution.MissionType)).Account;
    }

    private static string RunningLifecycleFor(string missionType) =>
        missionType.Equals("VALIDATE", StringComparison.OrdinalIgnoreCase)
            ? "VALIDATING"
            : missionType.Equals("PLATFORM_MAINTENANCE", StringComparison.OrdinalIgnoreCase)
                ? "PLATFORM_MAINTENANCE"
                : "BUILDING";

    private static string CompletedLifecycleFor(string missionType) =>
        missionType.Equals("PLATFORM_MAINTENANCE", StringComparison.OrdinalIgnoreCase)
            ? "PLATFORM_MAINTENANCE_COMPLETE"
            : "VALIDATING";

    private static string BuildRecoveryPrompt(V3BuildExecutionRecord execution, string missionType) =>
        $"""
        Continue a missão original após recuperação de restart/processo.

        ExecutionId anterior: {execution.MissionExecutionId}
        Último HEAD observado: {execution.CurrentHead ?? "unknown"}
        Última saída observada:
        {execution.LastOutput ?? "(sem saída registrada)"}

        Não recomece o projeto.
        Inspecione o repositório atual e preserve trabalho válido.
        Continue até satisfazer a Definition of Done ou encontrar um blocker genuinamente humano.

        """ + Environment.NewLine + ExitContract(missionType);

    public static string ExitContract(string missionType = "BUILD") =>
        missionType.Equals("VALIDATE", StringComparison.OrdinalIgnoreCase)
            ? """
        ## MACHINE-READABLE EXIT CONTRACT

        Você pode registrar progresso intermediário com:
        POSEIDON_PROGRESS_CHECKPOINT

        Isso NÃO encerra a missão.

        POSEIDON_MISSION_COMPLETE NÃO conclui uma ValidationMission.

        Somente quando acreditar que a VALIDAÇÃO está concluída, checklist aplicável tem FAIL=0, navegador foi usado quando aplicável e regressão foi realizada, finalize com:
        POSEIDON_VALIDATION_COMPLETE

        Se existir blocker que depende genuinamente do humano, finalize com:
        POSEIDON_HUMAN_BLOCKER
        seguido da descrição objetiva.

        Não aguarde aprovação depois de checkpoints.
        Continue autonomamente.
        """
            : missionType.Equals("PLATFORM_MAINTENANCE", StringComparison.OrdinalIgnoreCase)
                ? """
        ## MACHINE-READABLE EXIT CONTRACT

        Você pode registrar progresso intermediário com:
        POSEIDON_PROGRESS_CHECKPOINT

        Isso NÃO encerra a missão.

        Somente quando acreditar que a manutenção da plataforma foi concluída, testes relevantes passaram e o commit local foi feito, finalize o relatório com:
        POSEIDON_MISSION_COMPLETE

        Se existir blocker que depende genuinamente do humano, finalize com:
        POSEIDON_HUMAN_BLOCKER
        seguido da descrição objetiva.

        Não use POSEIDON_MISSION_COMPLETE em checkpoints.
        Não aguarde aprovação depois de checkpoints.
        Continue autonomamente.
        """
            : """
        ## MACHINE-READABLE EXIT CONTRACT

        Você pode registrar progresso intermediário com:
        POSEIDON_PROGRESS_CHECKPOINT

        Isso NÃO encerra a missão.

        Somente quando acreditar que a BUILD está concluída e a Definition of Done foi satisfeita, finalize o relatório com:
        POSEIDON_MISSION_COMPLETE

        Se existir blocker que depende genuinamente do humano, finalize com:
        POSEIDON_HUMAN_BLOCKER
        seguido da descrição objetiva.

        Não use POSEIDON_MISSION_COMPLETE em checkpoints.
        Não aguarde aprovação depois de checkpoints.
        Continue autonomamente.
        """;

    private static string? ExtractCheckpoint(string? output)
    {
        if (string.IsNullOrWhiteSpace(output) ||
            !output.Contains("POSEIDON_PROGRESS_CHECKPOINT", StringComparison.Ordinal))
        {
            return null;
        }

        var index = output.IndexOf("POSEIDON_PROGRESS_CHECKPOINT", StringComparison.Ordinal);
        return output[index..Math.Min(output.Length, index + 500)].Trim();
    }

    private static string ExtractAfterMarker(string output, string marker)
    {
        var index = output.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? output : output[(index + marker.Length)..].Trim();
    }

    private static string SanitizeOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;
        var trimmed = output.Trim();
        return trimmed.Length <= 8_000 ? trimmed : trimmed[..8_000];
    }
}

public sealed class V3ExternalBuildExecutor(
    ExternalAgentExecutorFactory factory,
    AccountProfileProvisioner profiles,
    IClock clock) : IV3BuildExecutor
{
    public async Task<V3BuildExecutorOutcome> RunAsync(
        AgentAccountContract account,
        V3BuildExecutionPrompt prompt,
        string repository,
        string? resumeSessionId,
        CancellationToken token)
    {
        var executorProfile = ExecutorCatalog.Find(account.ExecutorId)
            ?? throw new ExternalAgentException("executor.unknown");
        var handle = profiles.Ensure(account, executorProfile, clock.UtcNow, owner: "poseidon-v3-build");
        var external = factory.Create(account.ExecutorId);
        await using var session = await external.StartAsync(
            new ExternalAgentRunRequest
            {
                Alias = account.Alias,
                Prompt = prompt.Text,
                WorkingDirectory = repository,
                Profile = handle.Layout,
                Access = ExternalAgentAccess.Workspace,
                ResumeSessionId = resumeSessionId,
                Timeout = TimeSpan.FromMinutes(45),
            },
            token);
        try
        {
            var result = await session.CollectAsync(token);
            return new V3BuildExecutorOutcome(
                result.Status,
                result.FinalMessage,
                result.FailureKind,
                result.FailureCode,
                result.SessionId,
                result.Usage,
                result.DurationMs);
        }
        finally
        {
            await session.CleanupAsync(CancellationToken.None);
        }
    }
}

public interface IV3BuildExecutor
{
    Task<V3BuildExecutorOutcome> RunAsync(
        AgentAccountContract account,
        V3BuildExecutionPrompt prompt,
        string repository,
        string? resumeSessionId,
        CancellationToken token);
}

public sealed class V3UnavailableBuildExecutor : IV3BuildExecutor
{
    public Task<V3BuildExecutorOutcome> RunAsync(
        AgentAccountContract account,
        V3BuildExecutionPrompt prompt,
        string repository,
        string? resumeSessionId,
        CancellationToken token) =>
        Task.FromResult(new V3BuildExecutorOutcome(
            ExternalAgentRunStatus.Failed,
            "V3 external execution is disabled in this host configuration.",
            ExternalFailureKind.Permanent,
            "v3.executor_disabled"));
}

public sealed partial class V3BuildExecutionRecoveryHostedService(
    V3BuildRuntimeService runtime,
    AgentAccountRegistry accounts,
    ILogger<V3BuildExecutionRecoveryHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var recovered = await runtime.RecoverRunningExecutionsAsync(accounts.List(), cancellationToken);
        if (recovered.Count > 0)
        {
            LogRecovered(logger, recovered.Count);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "V3 startup recovery reconciled {Count} persisted RUNNING mission execution(s).")]
    private static partial void LogRecovered(ILogger logger, int count);
}

public sealed record V3BuildExecutorOutcome(
    ExternalAgentRunStatus Status,
    string Output,
    ExternalFailureKind FailureKind = ExternalFailureKind.Unknown,
    string? FailureCode = null,
    string? SessionId = null,
    ExternalAgentUsage? Usage = null,
    long? DurationMs = null);

public sealed record V3BuildExecutionPrompt(string Text, bool IsContinuation, string? Reason);

public sealed record V3BuildDispatchCommand(
    string ProjectId,
    V3ProjectUnderstandState? State,
    V3BuildMissionRecord Mission,
    string ReadinessOverall,
    IReadOnlyList<AgentAccountContract> Accounts,
    string TenantId = "local");

public sealed record V3BuildRuntimeResult(V3BuildExecutionRecord? Execution, IResult? Result);

public sealed class V3BuildRuntimeStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;

    public V3BuildRuntimeStore(string dataDir)
    {
        _root = Path.Combine(dataDir, "v3");
    }

    public static V3BuildRuntimeStore ForConfiguration(IConfiguration configuration)
    {
        var dataDir = configuration["Harness:DataDir"];
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".harness-poseidon");
        }

        return new V3BuildRuntimeStore(dataDir);
    }

    public void WriteExecution(V3BuildExecutionRecord execution)
    {
        Write(ExecutionPath(execution.MissionExecutionId), execution);
    }

    public V3BuildExecutionRecord? ReadExecution(string executionId)
    {
        var path = ExecutionPath(executionId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<V3BuildExecutionRecord>(File.ReadAllText(path), Json)
            : null;
    }

    public V3BuildExecutionRecord? LatestForProject(string projectId)
    {
        var dir = ExecutionsDirectory();
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "*.json")
            .Select(path => JsonSerializer.Deserialize<V3BuildExecutionRecord>(File.ReadAllText(path), Json))
            .Where(execution => execution is not null && string.Equals(execution.ProjectId, projectId, StringComparison.Ordinal))
            .Select(execution => execution!)
            .OrderByDescending(execution => execution.StartedAt)
            .FirstOrDefault();
    }

    public V3BuildExecutionRecord? LatestCompletedBuildForProject(string projectId) =>
        ListExecutions()
            .Where(execution =>
                string.Equals(execution.ProjectId, projectId, StringComparison.Ordinal) &&
                string.Equals(execution.MissionType, "BUILD", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(execution.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(execution => execution.CompletedAt ?? execution.LastActivityAt)
            .FirstOrDefault();

    public IReadOnlyList<V3BuildExecutionRecord> ListExecutions()
    {
        var dir = ExecutionsDirectory();
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir, "*.json")
            .Select(path => JsonSerializer.Deserialize<V3BuildExecutionRecord>(File.ReadAllText(path), Json))
            .Where(execution => execution is not null)
            .Select(execution => execution!)
            .OrderByDescending(execution => execution.StartedAt)];
    }

    private string ExecutionPath(string executionId) => Path.Combine(ExecutionsDirectory(), $"{executionId}.json");

    private string ExecutionsDirectory() => Path.Combine(_root, "build-executions");

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, overwrite: true);
    }
}

public static class V3BuildExecutorSelector
{
    public static (AgentAccountContract? Account, string Reason) Select(
        IReadOnlyList<AgentAccountContract> accounts,
        string? preferredAlias = null,
        IReadOnlyList<string>? excludeAliases = null,
        string requiredRole = AgentRoles.ProjectExecutor)
    {
        var excluded = new HashSet<string>(excludeAliases ?? [], StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(preferredAlias))
        {
            var preferred = Eligible(accounts, excluded, requiredRole, allowProjectFallback: true)
                .FirstOrDefault(account => string.Equals(account.Alias, preferredAlias, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return (preferred, "RECOMMENDED_EXECUTOR + AVAILABLE + WRITE_CAPABLE + role compatible.");
            }
        }

        var exactRole = Eligible(accounts, excluded, requiredRole, allowProjectFallback: false).ToArray();
        var selected = exactRole.Length > 0
            ? exactRole[0]
            : Eligible(accounts, excluded, requiredRole, allowProjectFallback: true).FirstOrDefault();
        return selected is null
            ? (null, "No authenticated quota-available write-capable executor is available.")
            : (selected, "AVAILABLE + WRITE_CAPABLE + role compatible.");
    }

    private static IEnumerable<AgentAccountContract> Eligible(
        IReadOnlyList<AgentAccountContract> accounts,
        HashSet<string> excluded,
        string requiredRole,
        bool allowProjectFallback) =>
        accounts
            .Where(account => !excluded.Contains(account.Alias))
            .Where(account => account.State == AgentAccountState.Available)
            .Where(account => account.Health is AgentAccountHealth.Healthy or AgentAccountHealth.Degraded)
            .Where(account => !account.AllowedRoles.Contains(AgentRoles.ChiefOrchestrator, StringComparer.OrdinalIgnoreCase))
            .Where(account => account.AllowedRoles.Contains(requiredRole, StringComparer.OrdinalIgnoreCase) ||
                (allowProjectFallback && string.Equals(requiredRole, AgentRoles.ProjectExecutor, StringComparison.OrdinalIgnoreCase) &&
                 account.AllowedRoles.Any(role =>
                    string.Equals(role, AgentRoles.ProjectExecutor, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, AgentRoles.BackendSpecialist, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, AgentRoles.FrontendSpecialist, StringComparison.OrdinalIgnoreCase))))
            .Where(account => ExecutorCatalog.Find(account.ExecutorId)?.Capabilities.Capabilities.Contains("code", StringComparer.OrdinalIgnoreCase) == true)
            .OrderBy(account => account.Priority)
            .ThenBy(account => account.ActiveAttempts)
            .ThenBy(account => account.Alias, StringComparer.Ordinal);
}

public sealed record V3GitSnapshot(
    string? Head,
    int CommitCount,
    string StatusFingerprint)
{
    public static V3GitSnapshot Capture(string repository)
    {
        var head = Git(repository, "rev-parse", "HEAD");
        var countText = Git(repository, "rev-list", "--count", "HEAD");
        var status = Git(repository, "status", "--porcelain=v1", "--", ".", ":!.poseidon") ?? string.Empty;
        _ = int.TryParse(countText, out var count);
        return new V3GitSnapshot(head, count, Fingerprint(status));
    }

    public bool HasProgressComparedTo(V3GitSnapshot previous) =>
        !string.Equals(Head, previous.Head, StringComparison.Ordinal) ||
        CommitCount != previous.CommitCount ||
        !string.Equals(StatusFingerprint, previous.StatusFingerprint, StringComparison.Ordinal);

    public static bool HasUncommittedChanges(string repository) =>
        !string.IsNullOrWhiteSpace(Git(repository, "status", "--porcelain=v1", "--", ".", ":!.poseidon"));

    private static string? Git(string repository, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000) || process.ExitCode != 0) return null;
            return output.Trim();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Fingerprint(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}

public sealed record V3BuildExecutionRecord(
    string MissionExecutionId,
    string MissionId,
    string ProjectId,
    string MissionType,
    string ExecutorAccountId,
    string Provider,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? CompletedAt,
    string? InitialHead,
    string? CurrentHead,
    int InitialCommitCount,
    int CommitDelta,
    string? SessionId,
    int ContinueCount,
    int ConsecutiveNoProgressCount,
    string? LastOutput,
    string? LastFailureCode,
    string? QuotaState,
    string? HumanBlocker,
    IReadOnlyList<V3BuildContinuationRecord> Continuations,
    IReadOnlyList<V3BuildExecutionEvent> Events)
{
    public string? LastCheckpointSummary { get; init; }
    public string? FinalReport { get; init; }
    public V3ValidationReport? ValidationReport { get; init; }
}

public sealed record V3ValidationReport(
    int RequirementsChecked,
    int RequirementsPassed,
    int RequirementsFailed,
    int ChecklistTotal,
    int ChecklistPass,
    int ChecklistFixed,
    int ChecklistNA,
    int ChecklistFail,
    int BrowserTestsPassed,
    int BrowserTestsFailed,
    int BrowserTestsSkipped,
    int BugsFound,
    int BugsFixed,
    int BugsRemaining,
    string? FinalHead)
{
    public bool HasBlockingFailures =>
        RequirementsFailed > 0 ||
        ChecklistFail > 0 ||
        BrowserTestsFailed > 0 ||
        BugsRemaining > 0;

    public static V3ValidationReport Parse(string output, string? finalHead) =>
        new(
            Number(output, "RequirementsChecked"),
            Number(output, "RequirementsPassed"),
            Number(output, "RequirementsFailed"),
            Number(output, "ChecklistTotal"),
            Number(output, "ChecklistPass"),
            Number(output, "ChecklistFixed"),
            Number(output, "ChecklistNA"),
            Number(output, "ChecklistFail"),
            Number(output, "BrowserTestsPassed"),
            Number(output, "BrowserTestsFailed"),
            Number(output, "BrowserTestsSkipped"),
            Number(output, "BugsFound"),
            Number(output, "BugsFixed"),
            Number(output, "BugsRemaining"),
            finalHead);

    private static int Number(string output, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            output,
            $@"(?im)^\s*[-*]?\s*{System.Text.RegularExpressions.Regex.Escape(key)}\s*[:=]\s*(\d+)\b");
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : 0;
    }
}

public sealed record V3BuildContinuationRecord(
    string PreviousExecutor,
    string NewExecutor,
    string Reason,
    DateTimeOffset CreatedAt);

public sealed record V3BuildExecutionEvent(
    string Type,
    DateTimeOffset OccurredAt,
    string? Actor,
    string Detail);

public sealed record V3BuildExecutionResponse(
    string MissionExecutionId,
    string MissionId,
    string ProjectId,
    string MissionType,
    string ExecutorAccountId,
    string Provider,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? CompletedAt,
    string? InitialHead,
    string? CurrentHead,
    int CommitDelta,
    string? QuotaState,
    int ContinueCount,
    string? LastCheckpointSummary,
    string? LastOutput,
    V3ValidationReport? ValidationReport,
    IReadOnlyList<V3BuildContinuationRecord> Continuations,
    IReadOnlyList<V3BuildExecutionEvent> Events)
{
    public static V3BuildExecutionResponse From(V3BuildExecutionRecord execution) =>
        new(
            execution.MissionExecutionId,
            execution.MissionId,
            execution.ProjectId,
            execution.MissionType,
            execution.ExecutorAccountId,
            execution.Provider,
            execution.Status,
            execution.StartedAt,
            execution.LastActivityAt,
            execution.CompletedAt,
            execution.InitialHead,
            execution.CurrentHead,
            execution.CommitDelta,
            execution.QuotaState,
            execution.ContinueCount,
            execution.LastCheckpointSummary,
            execution.LastOutput,
            execution.ValidationReport,
            execution.Continuations,
            execution.Events);
}

public sealed record V3BuildDispatchRequest(string? MissionId = null);

public sealed record V3BuildHumanAnswerRequest(string Answer);
