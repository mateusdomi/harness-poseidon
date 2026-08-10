using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Host.Profiles;
using Harness.Host.WorkBoard;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Readiness.Contracts;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Prototyping;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;
using Microsoft.AspNetCore.Mvc;

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
        IPrototypeStore prototypes,
        Readiness.ProjectReadinessService readiness,
        [FromServices] AgentAccountRegistry accounts,
        IChannelLinkStore channelLinks,
        IConfiguration configuration,
        V3BuildRuntimeService runtime,
        CancellationToken token)
    {
        var resolved = await ResolveAsync(projectId, request, profiles, projects, token);
        if (resolved.Result is not null) return resolved.Result;

        var understandStore = V3UnderstandStore.ForConfiguration(configuration);
        var context = await V3ProjectContextBuilder.BuildAsync(
            resolved.Profile!, resolved.Project!, board, attachments, documents, prototypes,
            attachmentStorage, readiness, accounts, channelLinks, understandStore, token);
        var mission = ResolveMission(understandStore, context.ProjectId, input.MissionId);
        if (mission is null) return Problem(404, "mission_not_found", "The requested BUILD mission does not exist.");

        var readinessOverall = context.Readiness.All(item => item.Status is "PASS" or "NOT_APPLICABLE")
            ? "READY"
            : "NOT_READY";
        var result = await runtime.DispatchAsync(
            new V3BuildDispatchCommand(context.ProjectId, context.State, mission, readinessOverall, accounts.List()),
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
        V3BuildRuntimeService runtime,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(executionId, out _)) return Problem(400, "invalid_execution_id", "Execution ID must be a ULID.");
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        var execution = V3BuildRuntimeStore.ForConfiguration(configuration).ReadExecution(executionId);
        if (execution is null) return Problem(404, "build_execution_not_found", "The requested BUILD execution does not exist.");
        var result = await runtime.ContinueWithHumanAnswerAsync(execution, input.Answer, accounts.List(), token);
        return result.Result is not null ? result.Result : Results.Ok(V3BuildExecutionResponse.From(result.Execution!));
    }

    private static V3BuildMissionRecord? ResolveMission(V3UnderstandStore store, string projectId, string? missionId)
    {
        if (!string.IsNullOrWhiteSpace(missionId))
        {
            return store.ReadMission(missionId.Trim());
        }

        return store.ListMissions(projectId).FirstOrDefault(mission =>
            string.Equals(mission.MissionType, "BUILD", StringComparison.OrdinalIgnoreCase));
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
    IClock clock)
{
    private const int MaxContinueWithoutProgress = 2;

    public async Task<V3BuildRuntimeResult> DispatchAsync(V3BuildDispatchCommand command, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.State?.AuthorizedAt is null)
        {
            return Conflict("build_not_authorized", "BUILD dispatch requires explicit authorization.");
        }

        if (!string.Equals(command.ReadinessOverall, "READY", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict("build_readiness_not_ready", "BUILD dispatch requires V3 readiness READY.");
        }

        if (!string.Equals(command.Mission.MissionType, "BUILD", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(command.Mission.Status, "COMPILED", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict("build_mission_not_compiled", "BUILD dispatch requires a COMPILED BUILD mission.");
        }

        if (command.Mission.PrimaryRequirementsCoverage.Any(source => !source.Complete))
        {
            return Conflict("primary_requirements_incomplete", "BUILD dispatch requires 100% primary requirement coverage.");
        }

        if (string.IsNullOrWhiteSpace(command.Mission.Repository) ||
            !Directory.Exists(command.Mission.Repository))
        {
            return Conflict("repository_unreachable", "BUILD dispatch requires a local reachable repository.");
        }

        var selected = V3BuildExecutorSelector.Select(command.Accounts);
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
            "BUILD",
            selected.Account.Alias,
            selected.Account.ProviderKind,
            "DISPATCHED",
            now,
            now,
            null,
            initialSnapshot.Head,
            initialSnapshot.Head,
            0,
            initialSnapshot.CommitCount,
            null,
            0,
            0,
            null,
            null,
            null,
            null,
            [],
            [
                new V3BuildExecutionEvent("BUILD_DISPATCHED", now, selected.Account.Alias, "Initial BUILD mission dispatch."),
            ]);
        store.WriteExecution(execution);
        UpdateLifecycle(command.State, "BUILDING", "BUILDING");
        var running = execution with
        {
            Status = "RUNNING",
            Events = Append(execution.Events, "BUILD_STARTED", selected.Account.Alias, "Executor started."),
        };
        store.WriteExecution(running);
        understandStore.WriteMission(command.Mission with { Status = "RUNNING" });
        return await RunLoopAsync(running, command.Mission, selected.Account, command.Accounts, command.State, null, token);
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
            V3BuildExecutorSelector.Select(accounts).Account;
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
            Events = Append(execution.Events, "BUILD_CONTINUED", account.Alias, "Human answer registered; execution continued."),
        };
        store.WriteExecution(resumed);
        UpdateLifecycle(state, "BUILDING", "BUILDING");
        return RunLoopAsync(resumed, mission, account, accounts, state, BuildHumanAnswerPrompt(answer), token);
    }

    private async Task<V3BuildRuntimeResult> RunLoopAsync(
        V3BuildExecutionRecord start,
        V3BuildMissionRecord mission,
        AgentAccountContract account,
        IReadOnlyList<AgentAccountContract> accounts,
        V3ProjectUnderstandState? state,
        string? oneShotContinuationPrompt,
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
                : BuildContinuePrompt());
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

            if (outcome.FailureKind == ExternalFailureKind.QuotaExhausted)
            {
                var failover = V3BuildExecutorSelector.Select(accounts, excludeAliases: [active.Alias]);
                if (failover.Account is null)
                {
                    execution = execution with
                    {
                        Status = "PAUSED_QUOTA",
                        LastFailureCode = outcome.FailureCode ?? "executor.quota_exhausted",
                        QuotaState = "EXHAUSTED",
                        Events = Append(execution.Events, "BUILD_QUOTA_PAUSED", active.Alias, "Quota exhausted; no alternate executor available."),
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
                    Status = "RUNNING",
                    ContinueCount = execution.ContinueCount + 1,
                    ConsecutiveNoProgressCount = 0,
                    Continuations = [.. execution.Continuations, continuation],
                    Events = Append(execution.Events, "BUILD_FAILOVER", active.Alias, "Quota failover continued on another executor."),
                };
                store.WriteExecution(execution);
                continuationPrompt = BuildQuotaFailoverPrompt(mission, execution.LastOutput, previousSnapshot, currentSnapshot);
                previousSnapshot = currentSnapshot;
                continue;
            }

            if (outcome.FailureKind == ExternalFailureKind.AuthenticationRequired)
            {
                execution = execution with
                {
                    Status = "PAUSED_QUOTA",
                    LastFailureCode = outcome.FailureCode ?? "executor.authentication_required",
                    QuotaState = "AUTH_REQUIRED",
                    Events = Append(execution.Events, "BUILD_QUOTA_PAUSED", active.Alias, "Executor authentication required."),
                };
                store.WriteExecution(execution);
                if (state is not null) UpdateLifecycle(state, "PAUSED_QUOTA", "PAUSED_QUOTA");
                return new V3BuildRuntimeResult(execution, null);
            }

            if (lastOutput.Contains("POSEIDON_HUMAN_BLOCKER", StringComparison.Ordinal))
            {
                execution = execution with
                {
                    Status = "BLOCKED",
                    CompletedAt = now,
                    HumanBlocker = ExtractAfterMarker(lastOutput, "POSEIDON_HUMAN_BLOCKER"),
                    Events = Append(execution.Events, "BUILD_HUMAN_BLOCKED", active.Alias, "Executor declared a human blocker."),
                };
                store.WriteExecution(execution);
                if (state is not null) UpdateLifecycle(state, "BLOCKED", "BLOCKED");
                return new V3BuildRuntimeResult(execution, null);
            }

            if (lastOutput.Contains("POSEIDON_MISSION_COMPLETE", StringComparison.Ordinal))
            {
                execution = execution with
                {
                    Status = "COMPLETED",
                    CompletedAt = now,
                    FinalReport = lastOutput,
                    ConsecutiveNoProgressCount = 0,
                    Events = Append(execution.Events, "BUILD_COMPLETED", active.Alias, "Executor declared BUILD complete."),
                };
                store.WriteExecution(execution);
                understandStore.WriteMission(mission with { Status = "COMPLETED" });
                if (state is not null) UpdateLifecycle(state, "VALIDATING", "BUILD_COMPLETED");
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
                    Events = Append(execution.Events, "BUILD_STALLED", active.Alias, "Two consecutive continuations ended without completion, blocker, quota or repository progress."),
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
                Events = Append(execution.Events, "BUILD_CONTINUED", active.Alias, progressed
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

    private static string BuildInitialPrompt(V3BuildMissionRecord mission) =>
        mission.MissionText + Environment.NewLine + Environment.NewLine + ExitContract();

    private static string BuildContinuePrompt() =>
        """
        Continue a BuildMission original autonomamente.

        Você ainda não declarou POSEIDON_MISSION_COMPLETE.

        Inspecione o estado atual do repositório e continue de onde parou.

        Não recomece trabalho já concluído.

        Somente encerre ao satisfazer a Definition of Done ou encontrar um blocker genuinamente humano.

        """ + Environment.NewLine + ExitContract();

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

        """ + Environment.NewLine + ExitContract();

    private static string BuildHumanAnswerPrompt(string answer) =>
        $"""
        O humano respondeu ao blocker anterior:

        {answer.Trim()}

        Retome a BuildMission original. Preserve o trabalho válido e continue até POSEIDON_MISSION_COMPLETE ou novo POSEIDON_HUMAN_BLOCKER.

        """ + Environment.NewLine + ExitContract();

    public static string ExitContract() =>
        """
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
                result.SessionId);
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

public sealed record V3BuildExecutorOutcome(
    ExternalAgentRunStatus Status,
    string Output,
    ExternalFailureKind FailureKind = ExternalFailureKind.Unknown,
    string? FailureCode = null,
    string? SessionId = null);

public sealed record V3BuildExecutionPrompt(string Text, bool IsContinuation, string? Reason);

public sealed record V3BuildDispatchCommand(
    string ProjectId,
    V3ProjectUnderstandState? State,
    V3BuildMissionRecord Mission,
    string ReadinessOverall,
    IReadOnlyList<AgentAccountContract> Accounts);

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
        IReadOnlyList<string>? excludeAliases = null)
    {
        var excluded = new HashSet<string>(excludeAliases ?? [], StringComparer.OrdinalIgnoreCase);
        var projectExecutors = Eligible(accounts, excluded, onlyProjectExecutor: true).ToArray();
        var selected = projectExecutors.Length > 0
            ? projectExecutors[0]
            : Eligible(accounts, excluded, onlyProjectExecutor: false).FirstOrDefault();
        return selected is null
            ? (null, "No authenticated quota-available write-capable executor is available.")
            : (selected, "AVAILABLE + WRITE_CAPABLE + role compatible.");
    }

    private static IEnumerable<AgentAccountContract> Eligible(
        IReadOnlyList<AgentAccountContract> accounts,
        HashSet<string> excluded,
        bool onlyProjectExecutor) =>
        accounts
            .Where(account => !excluded.Contains(account.Alias))
            .Where(account => account.State == AgentAccountState.Available)
            .Where(account => account.Health is AgentAccountHealth.Healthy or AgentAccountHealth.Degraded)
            .Where(account => !account.AllowedRoles.Contains(AgentRoles.ChiefOrchestrator, StringComparer.OrdinalIgnoreCase))
            .Where(account => onlyProjectExecutor
                ? account.AllowedRoles.Contains(AgentRoles.ProjectExecutor, StringComparer.OrdinalIgnoreCase)
                : account.AllowedRoles.Any(role =>
                    string.Equals(role, AgentRoles.ProjectExecutor, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, AgentRoles.BackendSpecialist, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, AgentRoles.FrontendSpecialist, StringComparison.OrdinalIgnoreCase)))
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
        var status = Git(repository, "status", "--porcelain=v1") ?? string.Empty;
        _ = int.TryParse(countText, out var count);
        return new V3GitSnapshot(head, count, Fingerprint(status));
    }

    public bool HasProgressComparedTo(V3GitSnapshot previous) =>
        !string.Equals(Head, previous.Head, StringComparison.Ordinal) ||
        CommitCount != previous.CommitCount ||
        !string.Equals(StatusFingerprint, previous.StatusFingerprint, StringComparison.Ordinal);

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
            execution.Continuations,
            execution.Events);
}

public sealed record V3BuildDispatchRequest(string? MissionId = null);

public sealed record V3BuildHumanAnswerRequest(string Answer);
