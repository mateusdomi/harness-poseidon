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
    string RiskTier,
    decimal Weight,
    string InstructionVersionId,
    int InstructionVersion,
    string InstructionContentHash,
    int AttemptCount,
    int EvidenceCount,
    int ReviewCount);

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
