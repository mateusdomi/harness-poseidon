using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.SharedKernel.Identifiers;

namespace Harness.Persistence.Abstractions.WorkChain;

public interface IWorkChainStore
{
    Task<WorkChainCreateReceipt> CreateAsync(
        WorkChainCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainSnapshot?> ReadAsync(
        string tenantId,
        string solicitationId,
        CancellationToken cancellationToken = default);

    Task<WorkChainAggregateSnapshot?> ReadAggregateAsync(
        string tenantId,
        string solicitationId,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> TriageTaskAsync(
        WorkTaskLifecycleCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> MarkTaskReadyAsync(
        WorkTaskLifecycleCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> AssignTaskAsync(
        WorkTaskAssignmentCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> AddInstructionVersionAsync(
        WorkInstructionVersionCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> StartAttemptAsync(
        WorkAttemptStartCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> CompleteAttemptAsync(
        WorkAttemptCompleteCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> ExpireAttemptLeaseAsync(
        WorkAttemptLeaseExpiredCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> BlockRunningTaskAsync(
        WorkTaskBlockCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> UnblockTaskAsync(
        WorkTaskUnblockCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> ReplanEscalatedTaskAsync(
        WorkTaskReplanCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> ReviewAttemptAsync(
        WorkAttemptReviewCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> EscalateUnreviewableTaskAsync(
        WorkTaskReviewUnavailableCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> EscalateUndispatchableTaskAsync(
        WorkTaskUndispatchableCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> MergeApprovedTaskAsync(
        WorkTaskMergeCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> CompleteMergedTaskAsync(
        WorkTaskDeliveryCompleteCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkChainMutationReceipt> CancelRunningTaskAsync(
        WorkTaskCancellationCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record WorkChainCreateCommand(
    string TenantId,
    string ProjectId,
    string UserId,
    string SolicitationId,
    string SolicitationContent,
    string DemandId,
    string DemandTitle,
    string AcceptanceCriteriaJson,
    string TaskId,
    string TaskTitle,
    string RiskTier,
    decimal Weight,
    string InstructionVersionId,
    string InstructionContent,
    string InstructionContentHash,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkChainCreateReceipt(
    string SolicitationId,
    string DemandId,
    string TaskId,
    string InstructionVersionId,
    long LedgerSequence,
    string LedgerHash,
    string OutboxMessageId,
    bool Replay);

public sealed record WorkChainSnapshot(
    string TenantId,
    string ProjectId,
    string SolicitationId,
    string SolicitationContent,
    string DemandId,
    string TaskId,
    string TaskState,
    long TaskVersion,
    string RiskTier,
    decimal Weight,
    string InstructionVersionId,
    int InstructionVersion,
    string InstructionContentHash,
    int AttemptCount,
    int EvidenceCount,
    int ReviewCount);

public enum WorkChainMutationStatus
{
    Applied,
    IdempotentReplay,
    NotFound,
    VersionConflict,
    InvalidState,
    IndependentReviewerRequired,
}

public sealed record WorkChainMutationReceipt(
    WorkChainMutationStatus Status,
    string TaskId,
    string? AttemptId,
    long? TaskVersion,
    string? TaskState,
    string? AttemptState,
    long? LedgerSequence = null,
    string? LedgerHash = null,
    string? OutboxMessageId = null,
    string? InstructionVersionId = null,
    int? InstructionVersion = null);

public sealed record WorkInstructionVersionCreateCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string InstructionVersionId,
    string Content,
    string ContentHash,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkTaskLifecycleCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string ActorKind,
    string ActorId,
    string Reason,
    string EvidenceReference,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkTaskAssignmentCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string InstructionVersionId,
    string AssigneeAgentId,
    string ActorKind,
    string ActorId,
    string LeaseReference,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkAttemptStartCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string InstructionVersionId,
    string AttemptId,
    string ProducerAgentId,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkEvidenceInput(string EvidenceId, string Reference);

public sealed record WorkAttemptCompleteCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string AttemptId,
    long ExpectedTaskVersion,
    IReadOnlyList<WorkEvidenceInput> Evidence,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    WorkAttemptUsage? Usage = null);

/// <summary>
/// Consumo medido de uma tentativa, colhido do executor no fechamento. Existe porque
/// `model_invocations` é um espelho por invocação: sem esta projeção na própria tentativa, o
/// quadro, a Central de Entregas e a observabilidade da auditoria mostram custo e duração
/// zerados mesmo quando o trabalho realmente aconteceu.
///
/// <see langword="null"/> em <paramref name="TokensInput"/>, <paramref name="TokensOutput"/> ou
/// <paramref name="CostUsd"/> significa que o executor NÃO expôs o número. Zero medido e zero
/// desconhecido não podem ser lidos como a mesma coisa, então o campo permanece intocado no
/// banco em vez de ser sobrescrito por zero.
/// </summary>
public sealed record WorkAttemptUsage(
    long? DurationMs,
    long? TokensInput,
    long? TokensOutput,
    decimal? CostUsd);

public sealed record WorkAttemptLeaseExpiredCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string AttemptId,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    bool CountsTowardRoundBudget = false,
    string? FailureReason = null);

public sealed record WorkTaskBlockCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string AttemptId,
    string ActorKind,
    string ActorId,
    string Reason,
    string EvidenceReference,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

/// <summary>
/// Escala um card que está em `awaiting_review` e cuja REVISÃO não pôde ser executada por causa
/// de infraestrutura (nenhum crítico independente disponível, executor do crítico falhando de
/// forma persistente, timeout repetido). Existe porque adiar com backoff é correto uma vez e
/// errado para sempre: sem um teto, o mesmo card re-invoca o crítico a cada ciclo, derruba a
/// capacidade da conta por falhas consecutivas e passa a bloquear a revisão de TODOS os projetos,
/// sem ninguém nunca ser avisado.
///
/// A tentativa é PRESERVADA em `awaiting_review`: a falha é do revisor, não do trabalho, e nada
/// aqui reprova o ator. O card vai a `escalated`/`blocked` com motivo tipado, o que o tira da
/// fila de retry e faz a Diretora de Engenharia anunciá-lo como impedimento.
/// </summary>
public sealed record WorkTaskReviewUnavailableCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string AttemptId,
    string Reason,
    string EvidenceReference,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

/// <summary>
/// Escala um card PRONTO que o despacho não consegue assumir por uma razão estrutural — hoje, um
/// papel resolvido sem nenhum escopo de escrita, cuja tentativa a política de path recusaria em
/// toda rodada.
///
/// Existe pelo mesmo motivo da escalação de revisão impossível: pular é a resposta certa uma vez e
/// errada para sempre. Sem um destino, o card ficava em `ready` indefinidamente, o loop o
/// reexaminava a cada ciclo, o log crescia sem limite e ninguém — nem a Diretora de Engenharia,
/// nem o dono — jamais ficava sabendo que aquele trabalho nunca ia acontecer.
///
/// Não há tentativa envolvida: nada foi executado, então nada é reprovado.
/// </summary>
public sealed record WorkTaskUndispatchableCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string Reason,
    string EvidenceReference,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkTaskUnblockCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string ActorKind,
    string ActorId,
    string Resolution,
    string EvidenceReference,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkTaskReplanCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string InstructionVersionId,
    string Content,
    string ContentHash,
    string ChiefAgentId,
    string Reason,
    string EvidenceReference,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkAttemptReviewCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string AttemptId,
    string ReviewId,
    string ReviewerAgentId,
    string Decision,
    string Rationale,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkTaskMergeCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string CoordinatorAgentId,
    string SubmissionReference,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkTaskDeliveryCompleteCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string ActorId,
    string EvidenceReference,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkTaskCancellationCommand(
    string TenantId,
    string SolicitationId,
    string TaskId,
    string AttemptId,
    string ActorKind,
    string ActorId,
    string Reason,
    string EvidenceReference,
    long ExpectedTaskVersion,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record WorkChainAggregateSnapshot(
    string TenantId,
    string ProjectId,
    string UserId,
    string SolicitationId,
    string SolicitationContent,
    DateTimeOffset CreatedAt,
    IReadOnlyList<WorkDemandSnapshot> Demands);

public sealed record WorkDemandSnapshot(
    string DemandId,
    string Title,
    IReadOnlyList<string> AcceptanceCriteria,
    DateTimeOffset CreatedAt,
    IReadOnlyList<WorkTaskAggregateSnapshot> Tasks);

public sealed record WorkTaskAggregateSnapshot(
    string TaskId,
    string Title,
    string RiskTier,
    decimal Weight,
    string State,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<WorkInstructionVersionSnapshot> Instructions,
    IReadOnlyList<WorkAttemptSnapshot> Attempts);

public sealed record WorkInstructionVersionSnapshot(
    string InstructionVersionId,
    int Version,
    string Content,
    string ContentHash,
    string? SupersedesId,
    DateTimeOffset CreatedAt);

public sealed record WorkAttemptSnapshot(
    string AttemptId,
    string InstructionVersionId,
    int Number,
    string ProducerAgentId,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WorkEvidenceSnapshot> Evidence,
    WorkReviewSnapshot? Review);

public sealed record WorkEvidenceSnapshot(
    string EvidenceId,
    int Ordinal,
    string Reference,
    DateTimeOffset CreatedAt);

public sealed record WorkReviewSnapshot(
    string ReviewId,
    string ReviewerAgentId,
    string Decision,
    string Rationale,
    DateTimeOffset CreatedAt);

public static class WorkChainCreateValidator
{
    private static readonly HashSet<string> RiskTiers =
        new(StringComparer.Ordinal) { "low", "medium", "high", "critical" };

    public static void Validate(WorkChainCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUlid(command.TenantId, nameof(command.TenantId));
        ValidateUlid(command.ProjectId, nameof(command.ProjectId));
        ValidateUlid(command.UserId, nameof(command.UserId));
        ValidateUlid(command.SolicitationId, nameof(command.SolicitationId));
        ValidateUlid(command.DemandId, nameof(command.DemandId));
        ValidateUlid(command.TaskId, nameof(command.TaskId));
        ValidateUlid(command.InstructionVersionId, nameof(command.InstructionVersionId));
        ValidateText(command.SolicitationContent, nameof(command.SolicitationContent), 20_000);
        ValidateText(command.DemandTitle, nameof(command.DemandTitle), 500);
        ValidateText(command.TaskTitle, nameof(command.TaskTitle), 500);
        ValidateText(command.InstructionContent, nameof(command.InstructionContent), 100_000);
        ValidateText(command.IdempotencyKey, nameof(command.IdempotencyKey), 200);
        if (!RiskTiers.Contains(command.RiskTier))
        {
            throw new ArgumentException("RiskTier is invalid.", nameof(command));
        }

        if (command.Weight <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Weight must be positive.");
        }

        using var criteria = JsonDocument.Parse(command.AcceptanceCriteriaJson);
        if (criteria.RootElement.ValueKind != JsonValueKind.Array ||
            criteria.RootElement.GetArrayLength() == 0 ||
            criteria.RootElement.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
        {
            throw new ArgumentException(
                "Acceptance criteria must be a non-empty array of non-empty strings.",
                nameof(command));
        }

        var expectedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(command.InstructionContent)));
        if (!string.Equals(expectedHash, command.InstructionContentHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Instruction content hash does not match its immutable content.",
                nameof(command));
        }
    }

    private static void ValidateUlid(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }

    private static void ValidateText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"Value exceeds {maximumLength} characters.", parameterName);
        }
    }

}

public static class WorkChainCreateHash
{
    public static string Compute(WorkChainCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));
    }
}

public static class WorkChainMutationValidator
{
    private static readonly HashSet<string> ActorKinds =
        new(StringComparer.Ordinal) { "user", "chief", "agent", "system" };

    public static void Validate(WorkInstructionVersionCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUlid(command.TenantId, nameof(command));
        ValidateUlid(command.SolicitationId, nameof(command));
        ValidateUlid(command.TaskId, nameof(command));
        ValidateUlid(command.InstructionVersionId, nameof(command));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.ExpectedTaskVersion);
        ValidateText(command.Content, nameof(command), 100_000);
        ValidateText(command.IdempotencyKey, nameof(command), 200);
        var expectedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(command.Content)));
        if (!string.Equals(expectedHash, command.ContentHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Instruction content hash does not match its immutable content.",
                nameof(command));
        }
    }

    public static void Validate(WorkTaskLifecycleCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateTaskTransition(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        if (!ActorKinds.Contains(command.ActorKind))
        {
            throw new ArgumentException("Lifecycle actor kind is invalid.", nameof(command));
        }

        ValidateText(command.ActorId, nameof(command), 200);
        ValidateText(command.Reason, nameof(command), 10_000);
        ValidateText(command.EvidenceReference, nameof(command), 2_000);
    }

    public static void Validate(WorkTaskAssignmentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateTaskTransition(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        ValidateUlid(command.InstructionVersionId, nameof(command));
        if (!ActorKinds.Contains(command.ActorKind))
        {
            throw new ArgumentException("Assignment actor kind is invalid.", nameof(command));
        }

        ValidateText(command.AssigneeAgentId, nameof(command), 200);
        ValidateText(command.ActorId, nameof(command), 200);
        ValidateText(command.LeaseReference, nameof(command), 2_000);
    }

    public static void Validate(WorkAttemptStartCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.AttemptId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        ValidateUlid(command.InstructionVersionId, nameof(command));
        ValidateText(command.ProducerAgentId, nameof(command), 200);
    }

    public static void Validate(WorkAttemptCompleteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.AttemptId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        ArgumentNullException.ThrowIfNull(command.Evidence);
        if (command.Evidence.Count == 0)
        {
            throw new ArgumentException("At least one evidence reference is required.", nameof(command));
        }

        if (command.Usage is { } usage)
        {
            ValidateNonNegative(usage.DurationMs, nameof(command));
            ValidateNonNegative(usage.TokensInput, nameof(command));
            ValidateNonNegative(usage.TokensOutput, nameof(command));
            if (usage.CostUsd is < 0m)
            {
                throw new ArgumentException("Usage values cannot be negative.", nameof(command));
            }
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in command.Evidence)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            ValidateUlid(evidence.EvidenceId, nameof(command));
            ValidateText(evidence.Reference, nameof(command), 2_000);
            if (!identifiers.Add(evidence.EvidenceId))
            {
                throw new ArgumentException("Evidence identifiers must be unique.", nameof(command));
            }
        }
    }

    public static void Validate(WorkAttemptLeaseExpiredCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.AttemptId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        if (command.FailureReason is not null)
        {
            ValidateText(command.FailureReason, nameof(command), 2_000);
        }
        if (command.CountsTowardRoundBudget && command.FailureReason is null)
        {
            throw new ArgumentException(
                "A failed attempt that consumes a round requires a failure reason.",
                nameof(command));
        }
        // Uma expiração DEFERIDA pode — e precisa — gravar o motivo. Os dois campos medem eixos
        // diferentes: `CountsTowardRoundBudget` diz se a rodada foi consumida (orçamento de
        // esforço), `FailureReason` diz se houve falha real (saúde do card). Uma falha transitória
        // não queima rodada e ainda assim é falha; sem o motivo ela chega ao circuito do card como
        // um `cancelled` anônimo, indistinguível de um reinício do Host, e o card é redespachado
        // para sempre. `CardCircuitBreakerService.IsFailure` depende exatamente desta combinação.
    }

    /// <summary>
    /// Uma tentativa está ENCERRADA sem aprovação e, por isso, o card pode ser replanejado.
    ///
    /// `rejected` vem do crítico; `cancelled` e `abandoned` vêm de uma expiração de lease (com e
    /// sem motivo de falha). As três significam a mesma coisa para o replanejamento: aquela
    /// rodada acabou e não entregou. O que NÃO é replanejável é tentativa ainda viva (`running`)
    /// ou já aprovada — não há o que reescrever.
    /// </summary>
    public static bool WorkAttemptIsReplannable(string? attemptState) =>
        attemptState is "rejected" or "cancelled" or "abandoned";

    public static void Validate(WorkTaskBlockCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.AttemptId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        ValidateActor(command.ActorKind, command.ActorId, command.Reason, command.EvidenceReference);
    }

    public static void Validate(WorkTaskReviewUnavailableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.AttemptId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        ValidateText(command.Reason, nameof(command), 10_000);
        ValidateText(command.EvidenceReference, nameof(command), 2_000);
    }

    public static void Validate(WorkTaskUndispatchableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateTaskTransition(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        ValidateText(command.Reason, nameof(command), 10_000);
        ValidateText(command.EvidenceReference, nameof(command), 2_000);
    }

    public static void Validate(WorkTaskUnblockCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateTaskTransition(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        ValidateActor(
            command.ActorKind,
            command.ActorId,
            command.Resolution,
            command.EvidenceReference);
    }

    /// <summary>O limite da coluna <c>card_circuit_breakers.replan_note</c> (CHECK nos dois provedores).</summary>
    public const int CircuitReplanNoteMaxLength = 2_000;

    /// <summary>
    /// A nota de proveniência do circuito precisa caber na coluna. O <c>Reason</c> do comando
    /// aceita 10.000 caracteres e o ledger/evento guardam o texto INTEGRAL — só a nota do
    /// circuito é recortada, com o marcador deixando o truncamento explícito. Sem isto, uma
    /// decisão do dono em prosa (o passo natural seguinte) abortaria a mutação INTEIRA por
    /// violação de CHECK — e como o replanejamento é o único caminho de volta do card escalado,
    /// a falha se apresentaria igual ao OPS-024: card parado sem ninguém entender por quê.
    /// </summary>
    public static string TruncateCircuitReplanNote(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return reason.Length <= CircuitReplanNoteMaxLength
            ? reason
            : string.Concat(reason.AsSpan(0, CircuitReplanNoteMaxLength - 1), "…");
    }

    public static void Validate(WorkTaskReplanCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateTaskTransition(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        ValidateUlid(command.InstructionVersionId, nameof(command));
        ValidateText(command.Content, nameof(command), 100_000);
        ValidateText(command.ChiefAgentId, nameof(command), 200);
        ValidateText(command.Reason, nameof(command), 10_000);
        ValidateText(command.EvidenceReference, nameof(command), 2_000);
        var expectedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(command.Content)));
        if (!string.Equals(expectedHash, command.ContentHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Replanned instruction hash does not match its immutable content.",
                nameof(command));
        }
    }

    public static void Validate(WorkAttemptReviewCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommon(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.AttemptId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        ValidateUlid(command.ReviewId, nameof(command));
        ValidateText(command.ReviewerAgentId, nameof(command), 200);
        ValidateText(command.Rationale, nameof(command), 10_000);
        if (command.Decision is not ("approved" or "rejected"))
        {
            throw new ArgumentException("Review decision is invalid.", nameof(command));
        }
    }

    public static void Validate(WorkTaskMergeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateTaskTransition(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        ValidateText(command.CoordinatorAgentId, nameof(command), 200);
        ValidateText(command.SubmissionReference, nameof(command), 2_000);
    }

    public static void Validate(WorkTaskDeliveryCompleteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateTaskTransition(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        ValidateText(command.ActorId, nameof(command), 200);
        ValidateText(command.EvidenceReference, nameof(command), 2_000);
    }

    public static void Validate(WorkTaskCancellationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateTaskTransition(
            command.TenantId,
            command.SolicitationId,
            command.TaskId,
            command.ExpectedTaskVersion,
            command.IdempotencyKey);
        ValidateUlid(command.AttemptId, nameof(command));
        if (!ActorKinds.Contains(command.ActorKind))
        {
            throw new ArgumentException("Cancellation actor kind is invalid.", nameof(command));
        }

        ValidateText(command.ActorId, nameof(command), 200);
        ValidateText(command.Reason, nameof(command), 10_000);
        ValidateText(command.EvidenceReference, nameof(command), 2_000);
    }

    public static string Hash<TCommand>(TCommand command)
        where TCommand : notnull =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)));

    private static void ValidateCommon(
        string tenantId,
        string solicitationId,
        string taskId,
        string attemptId,
        long expectedTaskVersion,
        string idempotencyKey)
    {
        ValidateUlid(tenantId, nameof(tenantId));
        ValidateUlid(solicitationId, nameof(solicitationId));
        ValidateUlid(taskId, nameof(taskId));
        ValidateUlid(attemptId, nameof(attemptId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedTaskVersion);
        ValidateText(idempotencyKey, nameof(idempotencyKey), 200);
    }

    private static void ValidateTaskTransition(
        string tenantId,
        string solicitationId,
        string taskId,
        long expectedTaskVersion,
        string idempotencyKey)
    {
        ValidateUlid(tenantId, nameof(tenantId));
        ValidateUlid(solicitationId, nameof(solicitationId));
        ValidateUlid(taskId, nameof(taskId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedTaskVersion);
        ValidateText(idempotencyKey, nameof(idempotencyKey), 200);
    }

    private static void ValidateActor(
        string actorKind,
        string actorId,
        string reason,
        string evidenceReference)
    {
        if (!ActorKinds.Contains(actorKind))
        {
            throw new ArgumentException("Transition actor kind is invalid.", nameof(actorKind));
        }

        ValidateText(actorId, nameof(actorId), 200);
        ValidateText(reason, nameof(reason), 10_000);
        ValidateText(evidenceReference, nameof(evidenceReference), 2_000);
    }

    private static void ValidateNonNegative(long? value, string parameterName)
    {
        if (value is < 0)
        {
            throw new ArgumentException("Usage values cannot be negative.", parameterName);
        }
    }

    private static void ValidateUlid(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }

    private static void ValidateText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"Value exceeds {maximumLength} characters.", parameterName);
        }
    }
}
