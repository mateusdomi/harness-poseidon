using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Host.Agents;
using Harness.Host.Profiles;
using Harness.Modules.Agents.Application.Accounts;
using Harness.Modules.Agents.Contracts;
using Harness.Modules.Readiness.Contracts;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;
using Microsoft.AspNetCore.Mvc;

namespace Harness.Host.V3;

public static class V3FoundationEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapV3Foundation(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v1/v3").WithTags("v3-foundation");
        group.MapGet("/lifecycle/states", () => Results.Ok(V3Lifecycle.States));
        group.MapGet("/projects/{projectId}/lifecycle", GetLifecycleAsync)
            .Produces<V3ProjectLifecycleResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        group.MapGet("/projects/{projectId}/readiness", GetProjectReadinessAsync)
            .Produces<V3ProjectReadinessResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        group.MapGet("/capacity", GetCapacityAsync)
            .Produces<V3ExecutionCapacityResponse>()
            .ProducesProblem(401);
        group.MapGet("/agent-accounts", GetAccountsAsync)
            .Produces<V3AgentAccountsResponse>()
            .ProducesProblem(401)
            .ProducesProblem(409);
        group.MapPost("/agent-accounts", UpsertAccountAsync)
            .Produces<V3AgentAccountsResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(409);
        group.MapPost("/agent-accounts/{alias}/prepare-auth", PrepareAuthAsync)
            .Produces<V3AccountAuthInstruction>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapPost("/agent-accounts/{alias}/logout", LogoutAsync)
            .Produces<V3AccountLogoutResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapPost("/agent-accounts/{alias}/disable", DisableAccountAsync)
            .Produces<V3AgentAccountsResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapPost("/agent-accounts/{alias}/enable", EnableAccountAsync)
            .Produces<V3AgentAccountsResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapGet("/chief-assignment", GetChiefAssignmentAsync)
            .Produces<V3ChiefAssignmentResponse>()
            .ProducesProblem(401);
        group.MapPut("/chief-assignment", PutChiefAssignmentAsync)
            .Produces<V3ChiefAssignmentResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapPost("/projects/{projectId}/human-acceptance/accept", AcceptHumanAcceptanceAsync)
            .Produces<V3HumanAcceptanceResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapPost("/projects/{projectId}/human-acceptance/request-changes", RequestHumanAcceptanceChangesAsync)
            .Produces<V3HumanAcceptanceResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);

        return endpoints;
    }

    private static async Task<IResult> GetLifecycleAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        Readiness.ProjectReadinessService readiness,
        IConfiguration configuration,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _)) return Problem(400, "invalid_project_id", "Project ID must be a ULID.");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var project = await projects.GetAsync(profile.TenantId, projectId, token);
        if (project is null) return NotFound("project");

        var snapshot = await readiness.EvaluateAsync(profile.TenantId, project, profileReady: true, token);
        var saved = V3UnderstandStore.ForConfiguration(configuration).ReadProject(project.Id);
        return Results.Ok(V3Lifecycle.For(project, snapshot, saved?.LifecycleState));
    }

    private static async Task<IResult> GetProjectReadinessAsync(
        string projectId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        Readiness.ProjectReadinessService readiness,
        [FromServices] AgentAccountRegistry accounts,
        IWorkBoardStore board,
        ISolicitationAttachmentStore attachments,
        IChannelLinkStore channelLinks,
        IConfiguration configuration,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(projectId, out _)) return Problem(400, "invalid_project_id", "Project ID must be a ULID.");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        var project = await projects.GetAsync(profile.TenantId, projectId, token);
        if (project is null) return NotFound("project");

        var snapshot = await readiness.EvaluateAsync(profile.TenantId, project, profileReady: true, token);
        var artifactCount = 0;
        foreach (var solicitation in await board.ListSolicitationsAsync(profile.TenantId, project.Id, null, 200, token))
        {
            artifactCount += (await attachments.ListAsync(profile.TenantId, solicitation.Id, token)).Count;
        }

        var state = V3UnderstandStore.ForConfiguration(configuration).ReadProject(project.Id);
        var stack = V3StackResolver.Resolve(project, state, [], V3RequirementSourceFacts.Empty);
        var links = await channelLinks.ListAsync(profile.TenantId, token);
        return Results.Ok(V3Readiness.For(
            project,
            snapshot,
            accounts.List(),
            state,
            artifactCount,
            stack,
            links.Count > 0));
    }

    private static async Task<IResult> GetCapacityAsync(
        HttpRequest request,
        ILocalProfileStore profiles,
        [FromServices] AgentAccountRegistry accounts,
        IClock clock,
        CancellationToken token)
    {
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        return Results.Ok(V3Capacity.From(accounts.List(), clock.UtcNow));
    }

    private static async Task<IResult> GetAccountsAsync(
        HttpRequest request,
        ILocalProfileStore profiles,
        AgentRunSettings settings,
        [FromServices] AgentAccountRegistry registry,
        AccountAvailabilityLedger availability,
        IClock clock,
        CancellationToken token)
    {
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        try
        {
            var definitions = AgentAccountConfigurationLoader.LoadDefinitions(settings.AccountsFilePath);
            return Results.Ok(V3Accounts.From(definitions, registry.List(), availability.List(), clock.UtcNow));
        }
        catch (AgentAccountValidationException exception)
        {
            return Problem(409, "account_configuration_invalid", exception.Code);
        }
    }

    private static async Task<IResult> UpsertAccountAsync(
        V3AccountUpsertRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        AgentRunSettings settings,
        [FromServices] AgentAccountRegistry registry,
        AccountAvailabilityLedger availability,
        IClock clock,
        CancellationToken token)
    {
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        try
        {
            var definition = input.ToDefinition();
            var path = AgentAccountConfigurationWriter.Upsert(settings.AccountsFilePath, definition);
            registry.Register(AgentAccountConfigurationLoader.ToContract(definition));
            var definitions = AgentAccountConfigurationLoader.LoadDefinitions(path);
            return Results.Ok(V3Accounts.From(definitions, registry.List(), availability.List(), clock.UtcNow));
        }
        catch (AgentAccountValidationException exception)
        {
            return Problem(400, "invalid_agent_account", exception.Code);
        }
        catch (IOException)
        {
            return Problem(409, "account_configuration_write_failed", "The local account configuration file could not be written.");
        }
    }

    private static async Task<IResult> PrepareAuthAsync(
        string alias,
        HttpRequest request,
        ILocalProfileStore profiles,
        AgentRunSettings settings,
        [FromServices] AgentAccountRegistry registry,
        AccountProfileProvisioner provisioner,
        IClock clock,
        CancellationToken token)
    {
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        try
        {
            AgentAccountRegistry.ValidateAlias(alias);
            var account = registry.Get(alias);
            if (account is null) return NotFound("agent_account");
            var executor = ExecutorCatalog.Find(account.ExecutorId);
            if (executor is null) return Problem(409, "executor_unknown", "The account references an unknown executor.");

            var handle = provisioner.Ensure(account, executor, clock.UtcNow, owner: "poseidon-auth");
            return Results.Ok(V3AccountAuthInstruction.For(account, executor, handle.Layout, settings.AccountsFilePath));
        }
        catch (AgentAccountValidationException exception)
        {
            return Problem(400, "invalid_agent_account", exception.Code);
        }
    }

    private static async Task<IResult> LogoutAsync(
        string alias,
        HttpRequest request,
        ILocalProfileStore profiles,
        [FromServices] AgentAccountRegistry registry,
        AccountProfileProvisioner provisioner,
        CancellationToken token)
    {
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        try
        {
            AgentAccountRegistry.ValidateAlias(alias);
            if (registry.Get(alias) is null) return NotFound("agent_account");
            var result = provisioner.Cleanup(alias, AccountProfileCleanupScope.Full);
            registry.UpdateHealth(alias, AgentAccountHealth.Unknown, AgentAccountState.AuthenticationRequired, "account.logged_out");
            return Results.Ok(new V3AccountLogoutResponse(alias, result.RemovedPaths.Count > 0, result.ConfigHomePreserved));
        }
        catch (AgentAccountValidationException exception)
        {
            return Problem(400, "invalid_agent_account", exception.Code);
        }
    }

    private static async Task<IResult> DisableAccountAsync(
        string alias,
        HttpRequest request,
        ILocalProfileStore profiles,
        AgentRunSettings settings,
        [FromServices] AgentAccountRegistry registry,
        AccountAvailabilityLedger availability,
        IClock clock,
        CancellationToken token) =>
        await SetAccountUsagePolicyAsync(
            alias,
            AgentAccountUsagePolicies.Reserved,
            request,
            profiles,
            settings,
            registry,
            availability,
            clock,
            token);

    private static async Task<IResult> EnableAccountAsync(
        string alias,
        HttpRequest request,
        ILocalProfileStore profiles,
        AgentRunSettings settings,
        [FromServices] AgentAccountRegistry registry,
        AccountAvailabilityLedger availability,
        IClock clock,
        CancellationToken token) =>
        await SetAccountUsagePolicyAsync(
            alias,
            AgentAccountUsagePolicies.Automatic,
            request,
            profiles,
            settings,
            registry,
            availability,
            clock,
            token);

    private static async Task<IResult> SetAccountUsagePolicyAsync(
        string alias,
        string usagePolicy,
        HttpRequest request,
        ILocalProfileStore profiles,
        AgentRunSettings settings,
        AgentAccountRegistry registry,
        AccountAvailabilityLedger availability,
        IClock clock,
        CancellationToken token)
    {
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        try
        {
            AgentAccountRegistry.ValidateAlias(alias);
            var path = AgentAccountConfigurationWriter.SetUsagePolicy(settings.AccountsFilePath, alias, usagePolicy);
            foreach (var definition in AgentAccountConfigurationLoader.LoadDefinitions(path))
            {
                registry.Register(AgentAccountConfigurationLoader.ToContract(definition));
            }

            return Results.Ok(V3Accounts.From(
                AgentAccountConfigurationLoader.LoadDefinitions(path),
                registry.List(),
                availability.List(),
                clock.UtcNow));
        }
        catch (AgentAccountValidationException exception)
        {
            return Problem(exception.Code == "account.not_found" ? 404 : 400, "invalid_agent_account", exception.Code);
        }
        catch (IOException)
        {
            return Problem(409, "account_configuration_write_failed", "The local account configuration file could not be written.");
        }
    }

    private static async Task<IResult> GetChiefAssignmentAsync(
        HttpRequest request,
        ILocalProfileStore profiles,
        [FromServices] AgentAccountRegistry registry,
        AgentRunSettings settings,
        CancellationToken token)
    {
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        return Results.Ok(V3ChiefAssignmentStore.Read(settings.AccountsFilePath, registry.List()));
    }

    private static async Task<IResult> PutChiefAssignmentAsync(
        V3ChiefAssignmentRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        AgentRunSettings settings,
        [FromServices] AgentAccountRegistry registry,
        CancellationToken token)
    {
        if (await LocalProfileSession.ResolveAsync(request, profiles, token) is null) return SessionRequired();
        try
        {
            AgentAccountRegistry.ValidateAlias(input.PrimaryAlias);
            var current = registry.Get(input.PrimaryAlias);
            if (current is null) return NotFound("agent_account");
            if (!current.AllowedRoles.Contains(AgentRoles.ChiefOrchestrator, StringComparer.OrdinalIgnoreCase))
            {
                return Problem(400, "account_not_chief_capable", "The selected account is not allowed to act as chief-orchestrator.");
            }

            var path = V3ChiefAssignmentStore.Write(settings.AccountsFilePath, input.PrimaryAlias);
            var definitions = AgentAccountConfigurationLoader.LoadDefinitions(path);
            foreach (var definition in definitions.Where(definition =>
                         definition.AllowedRoles.Contains(AgentRoles.ChiefOrchestrator, StringComparer.OrdinalIgnoreCase)))
            {
                registry.Register(AgentAccountConfigurationLoader.ToContract(definition));
            }

            return Results.Ok(V3ChiefAssignmentStore.Read(path, registry.List()));
        }
        catch (AgentAccountValidationException exception)
        {
            return Problem(400, "invalid_chief_assignment", exception.Code);
        }
        catch (IOException)
        {
            return Problem(409, "chief_assignment_write_failed", "The local account configuration file could not be written.");
        }
    }

    private static async Task<IResult> AcceptHumanAcceptanceAsync(
        string projectId,
        V3HumanAcceptanceRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IConfiguration configuration,
        IClock clock,
        CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (!UlidValue.TryParse(projectId, out _)) return Problem(400, "invalid_project_id", "Project ID must be a ULID.");
        var project = await projects.GetAsync(profile.TenantId, projectId, token);
        if (project is null) return NotFound("project");

        var store = V3UnderstandStore.ForConfiguration(configuration);
        var state = store.ReadProject(project.Id) ?? V3ProjectUnderstandState.Create(project.Id, clock.UtcNow);
        var transition = V3HumanAcceptance.Accept(state, input.Note, clock.UtcNow);
        if (!transition.Accepted) return Problem(409, "human_acceptance_not_ready", transition.Message);
        store.WriteProject(transition.State);
        return Results.Ok(V3HumanAcceptanceResponse.From(transition.State, transition.Message));
    }

    private static async Task<IResult> RequestHumanAcceptanceChangesAsync(
        string projectId,
        V3HumanAcceptanceRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IConfiguration configuration,
        IClock clock,
        CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        if (!UlidValue.TryParse(projectId, out _)) return Problem(400, "invalid_project_id", "Project ID must be a ULID.");
        var project = await projects.GetAsync(profile.TenantId, projectId, token);
        if (project is null) return NotFound("project");

        var store = V3UnderstandStore.ForConfiguration(configuration);
        var state = store.ReadProject(project.Id) ?? V3ProjectUnderstandState.Create(project.Id, clock.UtcNow);
        var transition = V3HumanAcceptance.RequestChanges(state, input.Note, clock.UtcNow);
        if (!transition.Accepted) return Problem(409, "human_acceptance_not_ready", transition.Message);
        store.WriteProject(transition.State);
        return Results.Ok(V3HumanAcceptanceResponse.From(transition.State, transition.Message));
    }

    private static IResult SessionRequired() =>
        Problem(401, "local_session_required", "A local profile session is required.");

    private static IResult NotFound(string resource) =>
        Problem(404, $"{resource}_not_found", "The requested resource does not exist.");

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

public static class V3Lifecycle
{
    public static readonly IReadOnlyList<string> States =
    [
        "DRAFT",
        "UNDERSTANDING",
        "AWAITING_INPUT",
        "READY_TO_START",
        "BUILDING",
        "PAUSED_QUOTA",
        "BLOCKED",
        "VALIDATING",
        "READY_FOR_HUMAN_ACCEPTANCE",
        "HUMAN_ACCEPTED",
    ];

    public static readonly IReadOnlyDictionary<string, int> Weights = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["UNDERSTAND"] = 15,
        ["BUILD"] = 55,
        ["VALIDATE"] = 25,
        ["HUMAN_ACCEPTANCE"] = 5,
    };

    public static V3ProjectLifecycleResponse For(
        Harness.Persistence.Abstractions.Projects.ProjectRecord project,
        ProjectReadinessSnapshot readiness,
        string? persistedStage = null)
    {
        var stage = States.Contains(persistedStage ?? string.Empty, StringComparer.Ordinal)
            ? persistedStage!
            : project.State switch
        {
            "archived" => "HUMAN_ACCEPTED",
            "paused" => "BLOCKED",
            _ when readiness.OverallState == ConfigurationState.Ready => "READY_TO_START",
            _ when readiness.Steps.Any(step => step.Blockers.Any(blocker =>
                blocker.Code.Contains("quota", StringComparison.OrdinalIgnoreCase))) => "PAUSED_QUOTA",
            _ when readiness.NextActions.Count > 0 => "AWAITING_INPUT",
            _ => "UNDERSTANDING",
        };

        return new V3ProjectLifecycleResponse(
            project.Id,
            project.State,
            stage,
            PercentFor(stage),
            Weights,
            readiness.OverallState.ToString(),
            readiness.NextActions.Select(action => action.Code).ToArray());
    }

    public static int PercentFor(string stage) => stage switch
    {
        "DRAFT" => 0,
        "UNDERSTANDING" => 5,
        "AWAITING_INPUT" => 10,
        "READY_TO_START" => 15,
        "BUILDING" => 35,
        "PAUSED_QUOTA" => 35,
        "BLOCKED" => 35,
        "VALIDATING" => 80,
        "READY_FOR_HUMAN_ACCEPTANCE" => 95,
        "HUMAN_ACCEPTED" => 100,
        _ => 0,
    };
}

public static class V3HumanAcceptance
{
    public static V3HumanAcceptanceTransition Accept(V3ProjectUnderstandState state, string? note, DateTimeOffset now)
    {
        if (!string.Equals(state.LifecycleState, "READY_FOR_HUMAN_ACCEPTANCE", StringComparison.Ordinal))
        {
            return new V3HumanAcceptanceTransition(false, state, "Project must be READY_FOR_HUMAN_ACCEPTANCE before human acceptance.");
        }

        var updated = state with
        {
            Status = "HUMAN_ACCEPTED",
            LifecycleState = "HUMAN_ACCEPTED",
            Decisions = AppendDecision(state.Decisions, note, "Human accepted the delivery."),
            UpdatedAt = now,
        };
        return new V3HumanAcceptanceTransition(true, updated, "Human acceptance recorded.");
    }

    public static V3HumanAcceptanceTransition RequestChanges(V3ProjectUnderstandState state, string? note, DateTimeOffset now)
    {
        if (!string.Equals(state.LifecycleState, "READY_FOR_HUMAN_ACCEPTANCE", StringComparison.Ordinal))
        {
            return new V3HumanAcceptanceTransition(false, state, "Project must be READY_FOR_HUMAN_ACCEPTANCE before requesting acceptance changes.");
        }

        var updated = state with
        {
            Status = "HUMAN_REQUESTED_CHANGES",
            LifecycleState = "VALIDATING",
            Decisions = AppendDecision(state.Decisions, note, "Human requested changes before acceptance."),
            UpdatedAt = now,
        };
        return new V3HumanAcceptanceTransition(true, updated, "Human changes requested; project returned to VALIDATING.");
    }

    private static IReadOnlyList<string> AppendDecision(IReadOnlyList<string> decisions, string? note, string fallback)
    {
        var decision = string.IsNullOrWhiteSpace(note)
            ? fallback
            : $"{fallback} Note: {note.Trim()}";
        return [.. decisions, decision];
    }
}

public static class V3Readiness
{
    public static V3ProjectReadinessResponse For(
        Harness.Persistence.Abstractions.Projects.ProjectRecord project,
        ProjectReadinessSnapshot existing,
        IReadOnlyList<AgentAccountContract> accounts,
        V3ProjectUnderstandState? v3State = null,
        int artifactCount = 0,
        V3EffectiveStackContract? effectiveStack = null,
        bool operationalNotificationConfigured = false,
        bool? runtimeReadyOverride = null)
    {
        var capacity = V3Capacity.From(accounts, DateTimeOffset.UtcNow);
        var runtimeReady = runtimeReadyOverride ?? RuntimeReady();
        var confirmedDeadline = v3State?.Deadline ?? project.TargetDeadline;
        var confirmedRepository = string.IsNullOrWhiteSpace(v3State?.Repository)
            ? project.RepositoryUrl
            : v3State.Repository;
        var openQuestions = V3OpenQuestionPolicy.RequiredQuestions(
            project.Id,
            confirmedDeadline,
            confirmedRepository,
            v3State?.SourceFacts,
            v3State?.PrimaryRequirementsCoverage ?? []);
        var database = effectiveStack?.Database ?? string.Join(' ', project.Technologies);
        var databaseApplies = database.Contains("Oracle", StringComparison.OrdinalIgnoreCase) ||
            database.Contains("Postgre", StringComparison.OrdinalIgnoreCase) ||
            database.Contains("SQL Server", StringComparison.OrdinalIgnoreCase) ||
            database.Contains("database", StringComparison.OrdinalIgnoreCase) &&
            !database.Contains("conforme requisitos", StringComparison.OrdinalIgnoreCase);
        var stackResolved = project.Technologies.Count > 0 || effectiveStack is not null;
        var items = new List<V3ReadinessItem>
        {
            Item("Requirements", HasText(project.Description) ? "PASS" : "ACTION_REQUIRED", "project.description"),
            Item("Artifacts", artifactCount > 0 ? "PASS" : "ACTION_REQUIRED", artifactCount > 0 ? $"project artifacts: {artifactCount}" : "no project artifacts associated"),
            Item("OpenQuestions", openQuestions.Count == 0 ? "PASS" : "ACTION_REQUIRED", "v3.openQuestions"),
            Item("Deadline", confirmedDeadline.HasValue ? "PASS" : "NOT_APPLICABLE",
                confirmedDeadline.HasValue
                    ? "v3.deadline || project.targetDeadline"
                    : "deadline absent; V3 treats deadline as optional unless a business rule requires it"),
            Item("Repository", RepositoryReachable(confirmedRepository) ? "PASS" : "BLOCKED", "v3.repository || project.repositoryUrl"),
            Item("EffectiveStack", stackResolved ? "PASS" : "ACTION_REQUIRED", stackResolved ? "v3.effectiveStack" : "project.technologies"),
            Item("RuntimeEnvironment", runtimeReady ? "PASS" : "ACTION_REQUIRED", "dotnet/node/git/docker probe"),
            Item("Database", databaseApplies ? runtimeReady ? "PASS" : "ACTION_REQUIRED" : "NOT_APPLICABLE",
                databaseApplies ? $"database applies: {database}" : "project stack does not require local database"),
            Item("Notification", operationalNotificationConfigured ? "PASS" : "NOT_APPLICABLE",
                operationalNotificationConfigured ? "Poseidon operational channel configured" : "Poseidon operational notification is optional in this personal session"),
            Item("ExecutionCapacity", capacity.EffectiveExecutionSlots > 0 ? "PASS" : "ACTION_REQUIRED", "account capacity"),
            Item("Authentication", capacity.ChiefSlots > 0 ? "PASS" : "ACTION_REQUIRED", "chief account availability"),
        };

        // Para projetos V3, este read model é a autoridade do READY_TO_START. O snapshot legado
        // ainda é exposto para auditoria, mas não pode manter um V3 totalmente verde como
        // NOT_READY só porque a instalação antiga usa "Configured" como teto.
        var ready = items.All(item => item.Status is "PASS" or "NOT_APPLICABLE");
        return new V3ProjectReadinessResponse(
            project.Id,
            ready ? "READY" : "NOT_READY",
            existing.OverallState.ToString(),
            items);
    }

    private static V3ReadinessItem Item(string category, string status, string evidence) =>
        new(category, status, evidence);

    private static bool HasText(string value) => !string.IsNullOrWhiteSpace(value);

    private static bool RepositoryReachable(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl) || repositoryUrl.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        try { return Directory.Exists(Path.GetFullPath(repositoryUrl)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool RuntimeReady() =>
        CommandAvailable("dotnet", "--version") &&
        CommandAvailable("node", "--version") &&
        CommandAvailable("git", "--version") &&
        CommandAvailable("docker", "info");

    private static bool CommandAvailable(string command, string argument)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }.WithArgument(argument));
            if (process is null) return false;
            return process.WaitForExit((int)TimeSpan.FromSeconds(3).TotalMilliseconds) && process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static ProcessStartInfo WithArgument(this ProcessStartInfo startInfo, string argument)
    {
        startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}

public static class V3Capacity
{
    public static V3ExecutionCapacityResponse From(IReadOnlyList<AgentAccountContract> accounts, DateTimeOffset asOf)
    {
        var chief = Slots(accounts, AgentRoles.ChiefOrchestrator, "chat");
        var write = WriteSlots(accounts);
        var review = Slots(accounts, AgentRoles.Critic, "review");
        return new V3ExecutionCapacityResponse(
            asOf,
            chief,
            write,
            review,
            write,
            [.. accounts.Select(AccountSlot)]);
    }

    private static int WriteSlots(IReadOnlyList<AgentAccountContract> accounts) =>
        accounts
            .Where(account =>
                account.State == AgentAccountState.Available &&
                account.AllowedRoles.Any(role =>
                    string.Equals(role, AgentRoles.ProjectExecutor, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, AgentRoles.BackendSpecialist, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, AgentRoles.FrontendSpecialist, StringComparison.OrdinalIgnoreCase)) &&
                ExecutorCatalog.Find(account.ExecutorId)?.Capabilities.Capabilities.Contains("code", StringComparer.OrdinalIgnoreCase) == true)
            .Sum(account => Math.Max(0, account.ConcurrencyLimit - account.ActiveAttempts));

    private static int Slots(IReadOnlyList<AgentAccountContract> accounts, string role, string capability) =>
        accounts
            .Where(account =>
                account.State == AgentAccountState.Available &&
                account.AllowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase) &&
                ExecutorCatalog.Find(account.ExecutorId)?.Capabilities.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase) == true)
            .Sum(account => Math.Max(0, account.ConcurrencyLimit - account.ActiveAttempts));

    private static V3AccountCapacityItem AccountSlot(AgentAccountContract account)
    {
        var profile = ExecutorCatalog.Find(account.ExecutorId);
        return new V3AccountCapacityItem(
            account.Alias,
            account.ProviderKind,
            account.ExecutorId,
            account.AllowedRoles,
            account.State.ToString(),
            account.Health.ToString(),
            Math.Max(0, account.ConcurrencyLimit - account.ActiveAttempts),
            profile?.Capabilities.Capabilities ?? []);
    }
}

public static class V3Accounts
{
    public static V3AgentAccountsResponse From(
        IReadOnlyList<AgentAccountDefinition> definitions,
        IReadOnlyList<AgentAccountContract> registry,
        IReadOnlyList<AccountAvailabilityRecord> availability,
        DateTimeOffset asOf)
    {
        var registryByAlias = registry.ToDictionary(account => account.Alias, StringComparer.OrdinalIgnoreCase);
        var availabilityByAlias = availability.ToDictionary(account => account.Alias, StringComparer.OrdinalIgnoreCase);
        return new V3AgentAccountsResponse(
            asOf,
            [.. definitions.Select(definition =>
            {
                registryByAlias.TryGetValue(definition.Alias, out var account);
                availabilityByAlias.TryGetValue(definition.Alias, out var observed);
                var state = account?.State ?? (definition.Enabled ? AgentAccountState.AuthenticationRequired : AgentAccountState.Disabled);
                return new V3AgentAccountItem(
                    definition.Alias,
                    definition.ProviderKind,
                    definition.ExecutorId,
                    definition.AllowedRoles,
                    definition.ConcurrencyLimit,
                    definition.Priority,
                    definition.Enabled,
                    definition.UsagePolicy,
                    state.ToString(),
                    account?.Health.ToString() ?? AgentAccountHealth.Unknown.ToString(),
                    observed?.CooldownUntil,
                    observed?.ReasonCode,
                    ExecutorCatalog.Find(definition.ExecutorId)?.ConfigHomeEnvironmentVariable);
            })]);
    }
}

public static class AgentAccountConfigurationWriter
{
    private static readonly JsonSerializerOptions LocalJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Upsert(string? configuredPath, AgentAccountDefinition definition)
    {
        AgentAccountRegistry.ValidateAlias(definition.Alias);
        AgentAccountRegistry.ValidateCredentialReference(definition.CredentialRef);
        AgentAccountRegistry.ValidateRoles(definition.AllowedRoles);
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredPath)
            ? AgentAccountConfigurationLoader.DefaultFilePath
            : configuredPath);
        var file = ReadLocalFile(path);
        var accounts = file.Accounts
            .Where(account => !string.Equals(account.Alias, definition.Alias, StringComparison.OrdinalIgnoreCase))
            .Append(definition)
            .OrderBy(account => account.Alias, StringComparer.Ordinal)
            .ToArray();
        Write(path, new AgentAccountsFile { Accounts = accounts });
        return path;
    }

    public static string SetUsagePolicy(string? configuredPath, string alias, string usagePolicy)
    {
        AgentAccountRegistry.ValidateAlias(alias);
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredPath)
            ? AgentAccountConfigurationLoader.DefaultFilePath
            : configuredPath);
        var definitions = AgentAccountConfigurationLoader.LoadDefinitions(path);
        var found = false;
        var rewritten = definitions.Select(definition =>
        {
            if (!string.Equals(definition.Alias, alias, StringComparison.OrdinalIgnoreCase))
            {
                return definition;
            }

            found = true;
            return definition with { UsagePolicy = usagePolicy };
        }).ToArray();
        if (!found)
        {
            throw new AgentAccountValidationException("account.not_found");
        }

        Write(path, new AgentAccountsFile { Accounts = rewritten });
        return path;
    }

    public static AgentAccountsFile ReadLocalFile(string path)
    {
        if (!File.Exists(path)) return new AgentAccountsFile();
        return JsonSerializer.Deserialize<AgentAccountsFile>(File.ReadAllText(path), LocalJson) ?? new AgentAccountsFile();
    }

    public static void Write(string path, AgentAccountsFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(file, LocalJson));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, overwrite: true);
    }
}

public static class V3ChiefAssignmentStore
{
    public static V3ChiefAssignmentResponse Read(string? accountsPath, IReadOnlyList<AgentAccountContract> accounts)
    {
        var primary = accounts
            .Where(account => account.AllowedRoles.Contains(AgentRoles.ChiefOrchestrator, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(account => account.Priority)
            .ThenBy(account => account.Alias, StringComparer.Ordinal)
            .FirstOrDefault();
        return new V3ChiefAssignmentResponse(
            primary?.Alias,
            primary?.ProviderKind,
            primary?.ExecutorId,
            primary?.State.ToString(),
            accountsPath ?? AgentAccountConfigurationLoader.DefaultFilePath);
    }

    public static string Write(string? configuredPath, string primaryAlias)
    {
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredPath)
            ? AgentAccountConfigurationLoader.DefaultFilePath
            : configuredPath);
        var definitions = AgentAccountConfigurationLoader.LoadDefinitions(path);
        var rewritten = definitions.Select(definition =>
        {
            if (!definition.AllowedRoles.Contains(AgentRoles.ChiefOrchestrator, StringComparer.OrdinalIgnoreCase))
            {
                return definition;
            }

            return definition with
            {
                Priority = string.Equals(definition.Alias, primaryAlias, StringComparison.OrdinalIgnoreCase)
                    ? 10_000
                    : Math.Min(definition.Priority, 999),
            };
        }).ToArray();
        AgentAccountConfigurationWriter.Write(path, new AgentAccountsFile { Accounts = rewritten });
        return path;
    }
}

public sealed record V3ProjectLifecycleResponse(
    string ProjectId,
    string LegacyProjectState,
    string Stage,
    int Percent,
    IReadOnlyDictionary<string, int> Weights,
    string ReadinessState,
    IReadOnlyList<string> NextActions);

public sealed record V3HumanAcceptanceRequest(string? Note);

public sealed record V3HumanAcceptanceTransition(
    bool Accepted,
    V3ProjectUnderstandState State,
    string Message);

public sealed record V3HumanAcceptanceResponse(
    string ProjectId,
    string LifecycleState,
    string Status,
    DateTimeOffset UpdatedAt,
    string Message)
{
    public static V3HumanAcceptanceResponse From(V3ProjectUnderstandState state, string message) =>
        new(state.ProjectId, state.LifecycleState, state.Status, state.UpdatedAt, message);
}

public sealed record V3ReadinessItem(string Category, string Status, string EvidenceProvider);

public sealed record V3ProjectReadinessResponse(
    string ProjectId,
    string Overall,
    string LegacyReadinessState,
    IReadOnlyList<V3ReadinessItem> Items);

public sealed record V3ExecutionCapacityResponse(
    DateTimeOffset AsOf,
    int ChiefSlots,
    int WriteExecutorSlots,
    int ReviewValidationSlots,
    int EffectiveExecutionSlots,
    IReadOnlyList<V3AccountCapacityItem> Accounts);

public sealed record V3AccountCapacityItem(
    string Alias,
    string ProviderKind,
    string ExecutorId,
    IReadOnlyList<string> Roles,
    string State,
    string Health,
    int FreeSlots,
    IReadOnlyList<string> Capabilities);

public sealed record V3AgentAccountsResponse(DateTimeOffset AsOf, IReadOnlyList<V3AgentAccountItem> Accounts);

public sealed record V3AgentAccountItem(
    string Alias,
    string ProviderKind,
    string ExecutorId,
    IReadOnlyList<string> Roles,
    int ConcurrencyLimit,
    int Priority,
    bool Enabled,
    string UsagePolicy,
    string State,
    string Health,
    DateTimeOffset? ReturnsAt,
    string? ReasonCode,
    string? ConfigHomeEnvironmentVariable);

public sealed record V3AccountAuthInstruction(
    string Alias,
    string ProviderKind,
    string ExecutorId,
    string ConfigHomePath,
    string? ConfigHomeEnvironmentVariable,
    string Command,
    IReadOnlyList<string> Arguments,
    string ShellCommand,
    string Instruction,
    string? AccountsFilePath)
{
    public static V3AccountAuthInstruction For(
        AgentAccountContract account,
        ExecutorProfile executor,
        AccountProfileLayout layout,
        string? accountsFilePath)
    {
        var (arguments, instruction) = executor.ExecutorId switch
        {
            ExecutorCatalog.Codex => ((IReadOnlyList<string>)["login", "--device-auth"],
                "Execute o comando em um terminal. Conclua o login no navegador/dispositivo e depois rode o Doctor/Probe."),
            ExecutorCatalog.ClaudeCode => ((IReadOnlyList<string>)[],
                "Execute o comando em um terminal. Se a sessão pedir login, use /login e conclua o OAuth no navegador. Depois rode o Doctor/Probe."),
            _ => ((IReadOnlyList<string>)["--help"], "Este executor não possui fluxo OAuth padronizado nesta rodada."),
        };
        var prefix = executor.ConfigHomeEnvironmentVariable is null
            ? $"HOME={Shell(layout.ConfigHomePath)}"
            : $"{executor.ConfigHomeEnvironmentVariable}={Shell(layout.ConfigHomePath)}";
        var shell = $"{prefix} {executor.Command}{(arguments.Count == 0 ? string.Empty : " " + string.Join(' ', arguments.Select(Shell)))}";
        return new V3AccountAuthInstruction(
            account.Alias,
            account.ProviderKind,
            account.ExecutorId,
            layout.ConfigHomePath,
            executor.ConfigHomeEnvironmentVariable,
            executor.Command,
            arguments,
            shell,
            instruction,
            accountsFilePath ?? AgentAccountConfigurationLoader.DefaultFilePath);
    }

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

public sealed record V3AccountLogoutResponse(string Alias, bool Removed, bool ConfigHomePreserved);

public sealed class V3AccountUpsertRequest
{
    public required string Alias { get; init; }
    public required string ProviderKind { get; init; }
    public required string ExecutorId { get; init; }
    public IReadOnlyList<string> AllowedRoles { get; init; } = [];
    public int ConcurrencyLimit { get; init; } = 1;
    public int Priority { get; init; } = 100;
    public bool Enabled { get; init; } = true;
    public string UsagePolicy { get; init; } = AgentAccountUsagePolicies.Automatic;

    public AgentAccountDefinition ToDefinition()
    {
        var roles = AllowedRoles.Count == 0 ? [DefaultRole(ExecutorId)] : AllowedRoles;
        return new AgentAccountDefinition
        {
            Alias = Alias.Trim(),
            ProviderKind = ProviderKind.Trim(),
            ExecutorId = ExecutorId.Trim(),
            CredentialRef = $"keychain://poseidon/{Alias.Trim()}",
            AllowedRoles = roles,
            AllowedPathScopes = [.. roles.SelectMany(AgentRoles.PathScopesFor).Distinct(StringComparer.Ordinal)],
            ConcurrencyLimit = Math.Max(1, ConcurrencyLimit),
            Priority = Priority,
            Enabled = Enabled,
            UsagePolicy = UsagePolicy,
        };
    }

    private static string DefaultRole(string executorId) =>
        string.Equals(executorId, ExecutorCatalog.ClaudeCode, StringComparison.OrdinalIgnoreCase)
            ? AgentRoles.ChiefOrchestrator
            : AgentRoles.ProjectExecutor;
}

public sealed record V3ChiefAssignmentRequest(string PrimaryAlias);

public sealed record V3ChiefAssignmentResponse(
    string? PrimaryAlias,
    string? ProviderKind,
    string? ExecutorId,
    string? State,
    string AccountsFilePath);
