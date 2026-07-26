using Harness.Modules.Coordination.Contracts;
using Harness.SharedKernel.Identifiers;

namespace Harness.Modules.Coordination.Application;

public static class WorkBoardApplicationService
{
    private static readonly string[] AmbiguityTerms =
        ["talvez", "aproximadamente", "adequado", "rápido", "simples", "etc", "quando possível", "se necessário"];
    private static readonly HashSet<string> Priorities =
        new(["low", "medium", "high", "critical"], StringComparer.Ordinal);
    private static readonly HashSet<string> SolicitationKinds =
        new(["request", "intervention"], StringComparer.Ordinal);
    private static readonly HashSet<string> TaskStates =
        new(["backlog", "ready", "development", "review", "corrections", "testsGates", "blocked", "done"], StringComparer.Ordinal);
    private static readonly HashSet<string> SolicitationStates =
        new(["open", "inAnalysis", "converted", "answered", "closed"], StringComparer.Ordinal);
    // Conjunto fechado de tipos de card. Só 'agent_task' é auto-despachável pelo loop do Chefe;
    // os demais exigem um humano (gate/decisão) ou são portadores de escopo (feature/spike).
    // Vocabulário completo: os cinco tipos pré-playbook (compatibilidade) + os tipos canônicos
    // do playbook (§3) — historia/tarefa/bug/adr/documento/revisao/gate/incidente/chamado.
    private static readonly HashSet<string> CardTypes =
        new([
            "feature", "agent_task", "human_gate", "spike", "decision",
            "historia", "tarefa", "bug", "adr", "documento",
            "revisao", "gate", "incidente", "chamado",
        ], StringComparer.Ordinal);

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
            Choice(request.Priority ?? "medium", Priorities), Utc(now),
            OptionalText(request.PhaseName, 200));
    }

    public static (BoardTaskContract Task, TaskInstructionContract Instruction, string CardType) CreateTask(
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

        // Ausente => 'agent_task' (fail-safe: o card nasce auto-despachável).
        // O tipo também integra o contrato público para permitir filtros e rastreabilidade no quadro.
        var cardType = request.CardType is null ? "agent_task" : Choice(request.CardType, CardTypes);

        var task = new BoardTaskContract(
            Id(taskId), projectId, demandId, Text(request.Title, 500), "backlog",
            Choice(request.Priority ?? "medium", Priorities), assignee, null, 1,
            new WorkProgressContract(0m, 0m, 0m), Utc(now), now, request.DueAt, null,
            OptionalText(request.PhaseName, 200), cardType);
        var instruction = new TaskInstructionContract(
            Id(instructionId), task.Id, 1, Text(request.Instruction, 100_000), "chief", null, now);
        return (task, instruction, cardType);
    }

    public static (string State, string? Note) MoveTask(MoveTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : Text(request.Note, 10_000);
        return (Choice(request.ToState, TaskStates), note);
    }

    public static string SetTaskPriority(SetTaskPriorityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Choice(request.Priority, Priorities);
    }

    private static readonly HashSet<string> BatchOperations =
        new(["move", "priority", "archive", "unarchive"], StringComparer.Ordinal);

    // Valida os argumentos DE NÍVEL DE OPERAÇÃO do lote (não os ids). Uma falha aqui é 400 do
    // request inteiro; os ids são validados/aplicados por item pelo endpoint. Devolve a operação
    // normalizada + a lista de ids distintos (preservando ordem) + os args já validados.
    public static (string Operation, IReadOnlyList<string> TaskIds, string? ToState, string? Note,
        string? Priority) ValidateBatchTaskOperation(BatchTaskOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operation = Choice(request.Operation, BatchOperations);
        if (request.TaskIds is null || request.TaskIds.Count == 0)
            throw new ArgumentException("At least one task id is required.", nameof(request));
        if (request.TaskIds.Count > 500)
            throw new ArgumentException("At most 500 task ids are supported.", nameof(request));
        var ids = request.TaskIds.Distinct(StringComparer.Ordinal).ToArray();

        string? toState = null;
        string? note = null;
        string? priority = null;
        switch (operation)
        {
            case "move":
                (toState, note) = MoveTask(new MoveTaskRequest(
                    request.ToState ?? throw new ArgumentException("toState is required.", nameof(request)),
                    request.Note));
                break;
            case "priority":
                priority = SetTaskPriority(new SetTaskPriorityRequest(
                    request.Priority ?? throw new ArgumentException("priority is required.", nameof(request))));
                break;
        }

        return (operation, ids, toState, note, priority);
    }

    private static string? OptionalText(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Text(value, maxLength);

    public static string AppendInstruction(AppendTaskInstructionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Text(request.Body, 100_000);
    }

    public static (string TaskId, string Body) CreateInstruction(CreateTaskInstructionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (Id(request.TaskId), Text(request.Body, 100_000));
    }

    public static string TransitionSolicitation(TransitionSolicitationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Choice(request.State, SolicitationStates);
    }

    public static SolicitationAnalysisDraft AnalyzeSolicitation(
        string solicitationId, string profileId, AnalyzeSolicitationRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = Text(request.Text, 100_000);
        var attachments = NormalizeAttachments(request.AttachmentNames);
        var clauses = body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split(['\n', '.', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();
        if (clauses.Length == 0) clauses = [body];

        var requirements = clauses.Where(value => !value.EndsWith('?'))
            .Select(CleanClause).Where(value => value.Length > 0).Take(25).ToList();
        if (requirements.Count == 0) requirements.Add(CleanClause(clauses[0]));
        requirements.AddRange(attachments.Select(name => $"Considerar o anexo \"{name}\"."));

        var ambiguities = clauses.Where(value => AmbiguityTerms.Any(term =>
                value.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(value => $"Termo impreciso a esclarecer: {CleanClause(value)}")
            .Take(20).ToArray();
        var questions = clauses.Where(value => value.EndsWith('?'))
            .Select(value => CleanClause(value).TrimEnd('?') + "?")
            .Concat(ambiguities.Select(value => $"Como tornar mensurável: {value[0].ToString().ToLowerInvariant()}{value[1..]}?"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToList();
        if (questions.Count == 0)
            questions.Add("Qual resultado observável define que esta solicitação foi atendida?");

        var normalized = clauses.Select(value => CleanClause(value).TrimEnd('.', '?', '!'))
            .ToArray();
        var contradictions = normalized.Where(value => value.StartsWith("não ", StringComparison.OrdinalIgnoreCase) &&
                normalized.Contains(value[4..], StringComparer.OrdinalIgnoreCase))
            .Select(value => $"Declarações opostas detectadas para: {value[4..]}.")
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        var acceptance = requirements.Select(value =>
                $"Deve ser verificável que {LowerFirst(value.TrimEnd('.', '?', '!'))}.")
            .Take(25).ToArray();
        var title = CleanClause(clauses[0]);
        if (title.Length > 120) title = title[..120].TrimEnd();
        var solicitation = CreateSolicitation(solicitationId, profileId,
            new(request.ProjectId, "request", title, body), now);
        return new(solicitation, requirements, ambiguities, contradictions, questions, acceptance);
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

    private static string[] NormalizeAttachments(IReadOnlyList<string>? values)
    {
        if (values is null) return [];
        if (values.Count > 25) throw new ArgumentException("At most 25 attachments are supported.", nameof(values));
        var normalized = values.Select(value => Text(value, 255))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Any(value => value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                value.Contains('/') || value.Contains('\\') || value is "." or ".."))
            throw new ArgumentException("Attachment names must not contain paths.", nameof(values));
        return normalized;
    }

    private static string CleanClause(string value) => value.Trim().TrimEnd('.', ';') +
        (value.TrimEnd().EndsWith('?') ? string.Empty : ".");

    private static string LowerFirst(string value) => value.Length == 0
        ? value
        : char.ToLowerInvariant(value[0]) + value[1..];

    private static DateTimeOffset Utc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
}
