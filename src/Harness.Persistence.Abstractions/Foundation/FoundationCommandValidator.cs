using System.Text.Json;
using Harness.SharedKernel.Identifiers;

namespace Harness.Persistence.Abstractions.Foundation;

public static class FoundationCommandValidator
{
    public static void Validate(ProjectProvisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        foreach (var id in new[]
        {
            command.TenantId,
            command.OrganizationId,
            command.ProjectId,
            command.UserId,
            command.LedgerEventId,
            command.OutboxMessageId,
        })
        {
            if (!UlidValue.TryParse(id, out _))
            {
                throw new ArgumentException("Foundation command IDs must be canonical ULIDs.", nameof(command));
            }
        }

        foreach (var name in new[]
        {
            command.TenantName,
            command.OrganizationName,
            command.ProjectName,
            command.UserDisplayName,
            command.EventType,
        })
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            {
                throw new ArgumentException("Foundation command names and event type are required and bounded.", nameof(command));
            }
        }

        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 200 ||
            string.IsNullOrWhiteSpace(command.MessageHash) || command.MessageHash.Length != 64 ||
            command.MessageHash.Any(character => !Uri.IsHexDigit(character)) ||
            string.IsNullOrWhiteSpace(command.PayloadJson))
        {
            throw new ArgumentException("Foundation idempotency metadata is invalid.", nameof(command));
        }

        try
        {
            using var _ = JsonDocument.Parse(command.PayloadJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Foundation event payload must be valid JSON.", nameof(command), exception);
        }
    }
}
