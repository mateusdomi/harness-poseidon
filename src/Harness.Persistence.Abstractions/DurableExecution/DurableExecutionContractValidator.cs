using System.Text.Json;
using Harness.SharedKernel.Identifiers;

namespace Harness.Persistence.Abstractions.DurableExecution;

public static class DurableExecutionContractValidator
{
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromHours(24);

    public static void Validate(DurableExecutionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUlids(request.TenantId, request.ProjectId, request.ExecutionId);
        ValidateJson(request.PayloadJson, nameof(request));
        ValidateIdempotencyKey(request.IdempotencyKey, nameof(request));
        ArgumentNullException.ThrowIfNull(request.RetryPolicy);
    }

    public static void Validate(DurableLeaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateLeaseIdentity(command.TenantId, command.ExecutionId, command.AttemptId, command.Owner, command.FencingToken);
        ValidateIdempotencyKey(command.IdempotencyKey, nameof(command));
    }

    public static void Validate(DurableCheckpointCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateLeaseIdentity(command.TenantId, command.ExecutionId, command.AttemptId, command.Owner, command.FencingToken);
        ValidateBounded(command.CheckpointKey, "checkpointKey", nameof(command));
        ValidateJson(command.PayloadJson, nameof(command));
        ValidateIdempotencyKey(command.IdempotencyKey, nameof(command));
    }

    public static void Validate(DurableFailureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateLeaseIdentity(command.TenantId, command.ExecutionId, command.AttemptId, command.Owner, command.FencingToken);
        ValidateBounded(command.ErrorCode, "errorCode", nameof(command));
        if (string.IsNullOrWhiteSpace(command.ErrorDetail) || command.ErrorDetail.Length > 4_000)
        {
            throw new ArgumentException("errorDetail is required and cannot exceed 4000 characters.", nameof(command));
        }

        ValidateIdempotencyKey(command.IdempotencyKey, nameof(command));
    }

    public static void Validate(DurableSignalCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUlids(command.TenantId, command.ExecutionId);
        ValidateBounded(command.SignalName, "signalName", nameof(command));
        ValidateJson(command.PayloadJson, nameof(command));
        ValidateIdempotencyKey(command.IdempotencyKey, nameof(command));
    }

    public static void Validate(DurableTimerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUlids(command.TenantId, command.ExecutionId);
        ValidateBounded(command.TimerId, "timerId", nameof(command));
        ValidateJson(command.PayloadJson, nameof(command));
        ValidateIdempotencyKey(command.IdempotencyKey, nameof(command));
    }

    public static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                "Lease duration must be positive and cannot exceed 24 hours.");
        }
    }

    public static void ValidateTenantAndExecution(string tenantId, string executionId) =>
        ValidateUlids(tenantId, executionId);

    public static void ValidateOwner(string owner) => ValidateBounded(owner, "owner", nameof(owner));

    public static void ValidateIdempotencyKey(string key, string parameterName) =>
        ValidateBounded(key, "idempotencyKey", parameterName);

    private static void ValidateLeaseIdentity(
        string tenantId,
        string executionId,
        string attemptId,
        string owner,
        long fencingToken)
    {
        ValidateUlids(tenantId, executionId, attemptId);
        ValidateBounded(owner, "owner", nameof(owner));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fencingToken);
    }

    private static void ValidateUlids(params string[] identifiers)
    {
        if (identifiers.Any(identifier => !UlidValue.TryParse(identifier, out _)))
        {
            throw new ArgumentException("Durable execution identifiers must be canonical ULIDs.", nameof(identifiers));
        }
    }

    private static void ValidateBounded(string value, string fieldName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
        {
            throw new ArgumentException($"{fieldName} is required and cannot exceed 200 characters.", parameterName);
        }
    }

    private static void ValidateJson(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("JSON payload is required.", parameterName);
        }

        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Payload must be valid JSON.", parameterName, exception);
        }
    }
}
