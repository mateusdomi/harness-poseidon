using Harness.Host.Profiles;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Coordination.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.WorkBoard;

public static class WorkBoardEndpoints
{
    public static IEndpointRouteBuilder MapWorkBoard(this IEndpointRouteBuilder endpoints)
    {
        var solicitations = endpoints.MapGroup("/api/v1/solicitations").WithTags("solicitations");
        solicitations.MapGet("/", ListSolicitationsAsync).Produces<SolicitationPage>().ProducesProblem(400).ProducesProblem(401);
        solicitations.MapGet("/{id}", GetSolicitationAsync).Produces<SolicitationContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        solicitations.MapPost("/", CreateSolicitationAsync).Produces<SolicitationContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        solicitations.MapPost("/{id}/transitions", TransitionSolicitationAsync).Produces<SolicitationContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);

        var demands = endpoints.MapGroup("/api/v1/demands").WithTags("demands");
        demands.MapGet("/", ListDemandsAsync).Produces<DemandPage>().ProducesProblem(400).ProducesProblem(401);
        demands.MapGet("/{id}", GetDemandAsync).Produces<DemandContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        demands.MapPost("/", CreateDemandAsync).Produces<DemandContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

        var tasks = endpoints.MapGroup("/api/v1/tasks").WithTags("tasks");
        tasks.MapGet("/", ListTasksAsync).Produces<TaskPage>().ProducesProblem(400).ProducesProblem(401);
        tasks.MapGet("/{id}", GetTaskAsync).Produces<BoardTaskContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        tasks.MapPost("/", CreateTaskAsync).Produces<BoardTaskContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        tasks.MapPost("/{id}/moves", MoveTaskAsync).Produces<BoardTaskContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        tasks.MapPost("/{id}/priority", SetTaskPriorityAsync).Produces<BoardTaskContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        tasks.MapPost("/{id}/instructions", AppendTaskInstructionAsync).Produces<TaskInstructionContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);

        var instructions = endpoints.MapGroup("/api/v1/task-instructions").WithTags("task-instructions");
        instructions.MapGet("/", ListInstructionsAsync).Produces<InstructionPage>().ProducesProblem(400).ProducesProblem(401);
        instructions.MapGet("/{id}", GetInstructionAsync).Produces<TaskInstructionContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        instructions.MapPost("/", CreateTaskInstructionAsync).Produces<TaskInstructionContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);

        var attempts = endpoints.MapGroup("/api/v1/attempts").WithTags("attempts");
        attempts.MapGet("/", ListAttemptsAsync).Produces<AttemptPage>().ProducesProblem(400).ProducesProblem(401);
        attempts.MapGet("/{id}", GetAttemptAsync).Produces<AttemptContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

        var events = endpoints.MapGroup("/api/v1/attempt-events").WithTags("attempt-events");
        events.MapGet("/", ListAttemptEventsAsync).Produces<AttemptEventPage>().ProducesProblem(400).ProducesProblem(401);
        events.MapGet("/{id}", GetAttemptEventAsync).Produces<AttemptEventContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> ListSolicitationsAsync(string? projectId, string? cursor, int? limit,
        HttpRequest request, ILocalProfileStore profiles, IWorkBoardStore store, CancellationToken token)
    {
        var invalid = ValidatePage(cursor, limit, projectId); if (invalid is not null) return invalid;
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        var size = limit ?? 50; var rows = await store.ListSolicitationsAsync(profile.TenantId, projectId, cursor, size + 1, token);
        var more = rows.Count > size; var items = rows.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new SolicitationPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetSolicitationAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkBoardStore store, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("solicitation"); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired(); var row = await store.GetSolicitationAsync(profile.TenantId, id, token);
        return row is null ? NotFound("solicitation") : Results.Ok(ToContract(row));
    }

    private static async Task<IResult> CreateSolicitationAsync(CreateSolicitationRequest input,
        HttpRequest request, ILocalProfileStore profiles, IWorkBoardStore store, IClock clock, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        try
        {
            var now = clock.UtcNow; var value = WorkBoardApplicationService.CreateSolicitation(UlidValue.New(now).ToString(), profile.Id, input, now);
            var row = await store.CreateSolicitationAsync(new(profile.TenantId, value.Id, value.ProjectId, value.AuthorProfileId,
                value.Kind, value.Title, value.Body, value.SupersedesId, now), token);
            return Results.Created($"/api/v1/solicitations/{value.Id}", ToContract(row));
        }
        catch (WorkBoardReferenceNotFoundException e) { return ReferenceNotFound(e.Reference); }
        catch (ArgumentException e) { return Invalid("solicitation", e.Message); }
    }

    private static async Task<IResult> TransitionSolicitationAsync(string id,
        TransitionSolicitationRequest input, HttpRequest request, ILocalProfileStore profiles,
        IWorkBoardStore store, IClock clock, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("solicitation");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            var state = WorkBoardApplicationService.TransitionSolicitation(input);
            var row = await store.TransitionSolicitationAsync(
                new(profile.TenantId, id, state, clock.UtcNow), token);
            return Results.Ok(ToContract(row));
        }
        catch (WorkBoardReferenceNotFoundException e) { return ReferenceNotFound(e.Reference); }
        catch (WorkBoardInvalidStateException e) { return Conflict("solicitation_state_conflict", e.Message); }
        catch (ArgumentException e) { return Invalid("solicitation", e.Message); }
    }

    private static async Task<IResult> ListDemandsAsync(string? projectId, string? solicitationId,
        string? cursor, int? limit, HttpRequest request, ILocalProfileStore profiles, IWorkBoardStore store, CancellationToken token)
    {
        var invalid = ValidatePage(cursor, limit, projectId, solicitationId); if (invalid is not null) return invalid;
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        var size = limit ?? 50; var rows = await store.ListDemandsAsync(profile.TenantId, projectId, solicitationId, cursor, size + 1, token);
        var more = rows.Count > size; var items = rows.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new DemandPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetDemandAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkBoardStore store, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("demand"); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired(); var row = await store.GetDemandAsync(profile.TenantId, id, token);
        return row is null ? NotFound("demand") : Results.Ok(ToContract(row));
    }

    private static async Task<IResult> CreateDemandAsync(CreateDemandRequest input, HttpRequest request,
        ILocalProfileStore profiles, IWorkBoardStore store, IClock clock, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        try
        {
            var now = clock.UtcNow; var value = WorkBoardApplicationService.CreateDemand(UlidValue.New(now).ToString(), input, now);
            var row = await store.CreateDemandAsync(new(profile.TenantId, value.Id, value.ProjectId,
                value.SolicitationId, UlidValue.New(now.AddMilliseconds(1)).ToString(), profile.Id,
                value.Title, value.Description, value.Priority, now), token);
            return Results.Created($"/api/v1/demands/{value.Id}", ToContract(row));
        }
        catch (WorkBoardReferenceNotFoundException e) { return ReferenceNotFound(e.Reference); }
        catch (ArgumentException e) { return Invalid("demand", e.Message); }
    }

    private static async Task<IResult> ListTasksAsync(string? projectId, string? demandId, string? cursor,
        int? limit, HttpRequest request, ILocalProfileStore profiles, IWorkBoardStore store, CancellationToken token)
    {
        var invalid = ValidatePage(cursor, limit, projectId, demandId); if (invalid is not null) return invalid;
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        var size = limit ?? 100; var rows = await store.ListTasksAsync(profile.TenantId, projectId, demandId, cursor, size + 1, token);
        var more = rows.Count > size; var items = rows.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new TaskPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetTaskAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkBoardStore store, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("task"); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired(); var row = await store.GetTaskAsync(profile.TenantId, id, token);
        return row is null ? NotFound("task") : Results.Ok(ToContract(row));
    }

    private static async Task<IResult> CreateTaskAsync(CreateTaskRequest input, HttpRequest request,
        ILocalProfileStore profiles, IWorkBoardStore store, IClock clock, CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        try
        {
            var now = clock.UtcNow; var taskId = UlidValue.New(now).ToString(); var instructionId = UlidValue.New(now.AddMilliseconds(1)).ToString();
            var values = WorkBoardApplicationService.CreateTask(taskId, instructionId, input, now);
            var result = await store.CreateTaskAsync(new(profile.TenantId, taskId, values.Task.ProjectId,
                values.Task.DemandId, UlidValue.New(now.AddMilliseconds(2)).ToString(),
                UlidValue.New(now.AddMilliseconds(3)).ToString(), profile.Id, values.Task.Title,
                values.Task.Priority, values.Task.AssigneeAgentId, values.Task.DueAt,
                instructionId, values.Instruction.Body, now), token);
            return Results.Created($"/api/v1/tasks/{taskId}", ToContract(result.Task));
        }
        catch (WorkBoardReferenceNotFoundException e) { return ReferenceNotFound(e.Reference); }
        catch (ArgumentException e) { return Invalid("task", e.Message); }
    }

    private static async Task<IResult> MoveTaskAsync(string id, MoveTaskRequest input,
        HttpRequest request, ILocalProfileStore profiles, IWorkBoardStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("task");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            var value = WorkBoardApplicationService.MoveTask(input);
            var row = await store.MoveTaskAsync(new(profile.TenantId, id, value.State, value.Note,
                "user", clock.UtcNow), token);
            return Results.Ok(ToContract(row));
        }
        catch (WorkBoardReferenceNotFoundException e) { return ReferenceNotFound(e.Reference); }
        catch (WorkBoardInvalidStateException e) { return Conflict("task_state_conflict", e.Message); }
        catch (ArgumentException e) { return Invalid("task", e.Message); }
    }

    private static async Task<IResult> SetTaskPriorityAsync(string id, SetTaskPriorityRequest input,
        HttpRequest request, ILocalProfileStore profiles, IWorkBoardStore store, IClock clock,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("task");
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            var priority = WorkBoardApplicationService.SetTaskPriority(input);
            var row = await store.SetTaskPriorityAsync(
                new(profile.TenantId, id, priority, clock.UtcNow), token);
            return Results.Ok(ToContract(row));
        }
        catch (WorkBoardReferenceNotFoundException e) { return ReferenceNotFound(e.Reference); }
        catch (ArgumentException e) { return Invalid("task", e.Message); }
    }

    private static async Task<IResult> AppendTaskInstructionAsync(string id,
        AppendTaskInstructionRequest input, HttpRequest request, ILocalProfileStore profiles,
        IWorkBoardStore store, IClock clock, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("task");
        string body;
        try { body = WorkBoardApplicationService.AppendInstruction(input); }
        catch (ArgumentException e) { return Invalid("instruction", e.Message); }
        return await AppendInstructionCoreAsync(id, body, request, profiles, store, clock, token);
    }

    private static async Task<IResult> CreateTaskInstructionAsync(CreateTaskInstructionRequest input,
        HttpRequest request, ILocalProfileStore profiles, IWorkBoardStore store, IClock clock,
        CancellationToken token)
    {
        try
        {
            var value = WorkBoardApplicationService.CreateInstruction(input);
            return await AppendInstructionCoreAsync(
                value.TaskId, value.Body, request, profiles, store, clock, token);
        }
        catch (ArgumentException e) { return Invalid("instruction", e.Message); }
    }

    private static async Task<IResult> AppendInstructionCoreAsync(string taskId, string body,
        HttpRequest request, ILocalProfileStore profiles, IWorkBoardStore store, IClock clock,
        CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired();
        try
        {
            var now = clock.UtcNow; var instructionId = UlidValue.New(now).ToString();
            var row = await store.AppendInstructionAsync(new(profile.TenantId, taskId,
                instructionId, body, "user", profile.Id, now), token);
            return Results.Created($"/api/v1/task-instructions/{instructionId}", ToContract(row));
        }
        catch (WorkBoardReferenceNotFoundException e) { return ReferenceNotFound(e.Reference); }
        catch (WorkBoardInvalidStateException e) { return Conflict("instruction_state_conflict", e.Message); }
    }

    private static async Task<IResult> ListInstructionsAsync(string? taskId, string? cursor, int? limit,
        HttpRequest request, ILocalProfileStore profiles, IWorkBoardStore store, CancellationToken token)
    {
        var invalid = ValidatePage(cursor, limit, taskId); if (invalid is not null) return invalid;
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        var size = limit ?? 100; var rows = await store.ListInstructionsAsync(profile.TenantId, taskId, cursor, size + 1, token);
        var more = rows.Count > size; var items = rows.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new InstructionPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetInstructionAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkBoardStore store, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("instruction"); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired(); var row = await store.GetInstructionAsync(profile.TenantId, id, token);
        return row is null ? NotFound("instruction") : Results.Ok(ToContract(row));
    }

    private static async Task<IResult> ListAttemptsAsync(string? taskId, string? cursor, int? limit,
        HttpRequest request, ILocalProfileStore profiles, IWorkBoardStore store, CancellationToken token)
    {
        var invalid = ValidatePage(cursor, limit, taskId); if (invalid is not null) return invalid;
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        var size = limit ?? 100; var rows = await store.ListAttemptsAsync(profile.TenantId, taskId, cursor, size + 1, token);
        var more = rows.Count > size; var items = rows.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new AttemptPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetAttemptAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkBoardStore store, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("attempt"); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired(); var row = await store.GetAttemptAsync(profile.TenantId, id, token);
        return row is null ? NotFound("attempt") : Results.Ok(ToContract(row));
    }

    private static async Task<IResult> ListAttemptEventsAsync(string? attemptId, string? cursor, int? limit,
        HttpRequest request, ILocalProfileStore profiles, IWorkBoardStore store, CancellationToken token)
    {
        var invalid = ValidatePage(cursor, limit, attemptId); if (invalid is not null) return invalid;
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token); if (profile is null) return SessionRequired();
        var size = limit ?? 100; var rows = await store.ListAttemptEventsAsync(profile.TenantId, attemptId, cursor, size + 1, token);
        var more = rows.Count > size; var items = rows.Take(size).Select(ToContract).ToArray();
        return Results.Ok(new AttemptEventPage(items, more ? items[^1].Id : null));
    }

    private static async Task<IResult> GetAttemptEventAsync(string id, HttpRequest request,
        ILocalProfileStore profiles, IWorkBoardStore store, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId("attempt_event"); var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null) return SessionRequired(); var row = await store.GetAttemptEventAsync(profile.TenantId, id, token);
        return row is null ? NotFound("attempt_event") : Results.Ok(ToContract(row));
    }

    private static IResult? ValidatePage(string? cursor, int? limit, params string?[] ids)
    {
        if ((cursor is not null && !Valid(cursor)) || limit is < 1 or > 200 || ids.Any(id => id is not null && !Valid(id)))
            return Problem(400, "invalid_cursor", "Filter, cursor, or limit is invalid.");
        return null;
    }

    private static SolicitationContract ToContract(BoardSolicitationRecord x) => new(x.Id, x.ProjectId, x.AuthorProfileId, x.Kind, x.Title, x.Body, x.State, x.SupersedesId, x.CreatedAt);
    private static DemandContract ToContract(BoardDemandRecord x) => new(x.Id, x.ProjectId, x.SolicitationId, x.Title, x.Description, x.State, x.Priority, x.CreatedAt);
    private static BoardTaskContract ToContract(BoardTaskRecord x) => new(x.Id, x.ProjectId, x.DemandId, x.Title, x.State, x.Priority, x.AssigneeAgentId, x.BlockedReason, x.InstructionVersion, new(x.Progress.Executed, x.Progress.Validated, x.Progress.Approved), x.CreatedAt, x.UpdatedAt, x.DueAt);
    private static TaskInstructionContract ToContract(BoardInstructionRecord x) => new(x.Id, x.TaskId, x.Version, x.Body, x.AuthorKind, x.AuthorId, x.CreatedAt);
    private static AttemptContract ToContract(BoardAttemptRecord x) => new(x.Id, x.TaskId, x.Number, x.State, x.AgentId, x.StartedAt, x.FinishedAt, x.DurationMs, x.CostUsd, x.TokensInput, x.TokensOutput, x.CommitRefs, x.Summary, x.FailureReason);
    private static AttemptEventContract ToContract(BoardAttemptEventRecord x) => new(x.Id, x.AttemptId, x.Kind, x.Content, x.OccurredAt);
    private static bool Valid(string id) => UlidValue.TryParse(id, out _);
    private static IResult SessionRequired() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult InvalidId(string resource) => Problem(400, $"invalid_{resource}_id", "ID must be a ULID.");
    private static IResult Invalid(string resource, string detail) => Problem(400, $"invalid_{resource}", detail);
    private static IResult NotFound(string resource) => Problem(404, $"{resource}_not_found", "The resource does not exist.");
    private static IResult ReferenceNotFound(string reference) => Problem(404, $"{reference}_not_found", "The referenced resource does not exist.");
    private static IResult Conflict(string title, string detail) => Problem(409, title, detail);
    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record SolicitationPage(IReadOnlyList<SolicitationContract> Items, string? NextCursor);
public sealed record DemandPage(IReadOnlyList<DemandContract> Items, string? NextCursor);
public sealed record TaskPage(IReadOnlyList<BoardTaskContract> Items, string? NextCursor);
public sealed record InstructionPage(IReadOnlyList<TaskInstructionContract> Items, string? NextCursor);
public sealed record AttemptPage(IReadOnlyList<AttemptContract> Items, string? NextCursor);
public sealed record AttemptEventPage(IReadOnlyList<AttemptEventContract> Items, string? NextCursor);
