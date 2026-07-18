using Harness.Modules.Coordination.Contracts;
using Harness.SharedKernel.Identifiers;

namespace Harness.Modules.Coordination.Application;

public static class WorkBoardApplicationService
{
    private static readonly HashSet<string> Priorities =
        new(["low", "medium", "high", "critical"], StringComparer.Ordinal);
    private static readonly HashSet<string> SolicitationKinds =
        new(["request", "intervention"], StringComparer.Ordinal);

    public static SolicitationContract CreateSolicitation(
        string id, string profileId, CreateSolicitationRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new SolicitationContract(
            Id(id), Id(request.ProjectId), Id(profileId), Choice(request.Kind, SolicitationKinds),
            Text(request.Title, 500), Text(request.Body, 20_000), "open",
            request.SupersedesId is null ? null : Id(request.SupersedesId), Utc(now));
    }

    public static DemandContract CreateDemand(
        string id, CreateDemandRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new DemandContract(
            Id(id), Id(request.ProjectId),
            request.SolicitationId is null ? null : Id(request.SolicitationId),
            Text(request.Title, 500), Text(request.Description, 20_000), "open",
            Choice(request.Priority ?? "medium", Priorities), Utc(now));
    }

    public static (BoardTaskContract Task, TaskInstructionContract Instruction) CreateTask(
        string taskId, string instructionId, CreateTaskRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projectId = Id(request.ProjectId);
        var demandId = request.DemandId is null ? null : Id(request.DemandId);
        var assignee = request.AssigneeAgentId is null ? null : Id(request.AssigneeAgentId);
        if (request.DueAt is { } dueAt && (dueAt == default || dueAt.Offset != TimeSpan.Zero))
        {
            throw new ArgumentException("DueAt must be UTC.", nameof(request));
        }

        var task = new BoardTaskContract(
            Id(taskId), projectId, demandId, Text(request.Title, 500), "backlog",
            Choice(request.Priority ?? "medium", Priorities), assignee, null, 1,
            new WorkProgressContract(0m, 0m, 0m), Utc(now), now, request.DueAt);
        var instruction = new TaskInstructionContract(
            Id(instructionId), task.Id, 1, Text(request.Instruction, 100_000), "chief", null, now);
        return (task, instruction);
    }

    private static string Id(string value) =>
        UlidValue.TryParse(value, out var id)
            ? id.ToString()
            : throw new ArgumentException("Value must be a canonical ULID.", nameof(value));

    private static string Text(string value, int max)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        return normalized.Length <= max
            ? normalized
            : throw new ArgumentException($"Value exceeds {max} characters.", nameof(value));
    }

    private static string Choice(string value, HashSet<string> choices)
    {
        var normalized = Text(value, 100);
        return choices.Contains(normalized)
            ? normalized
            : throw new ArgumentException("Value is not supported.", nameof(value));
    }

    private static DateTimeOffset Utc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
}
