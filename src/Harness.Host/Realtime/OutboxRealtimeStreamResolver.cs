using System.Text.Json;
using Harness.Persistence.Abstractions.Messaging;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Realtime;

public sealed class OutboxRealtimeStreamResolver
{
    public string Resolve(OutboxLease message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var document = JsonDocument.Parse(message.PayloadJson);
        if (document.RootElement.TryGetProperty("projectId", out var projectIdElement) &&
            projectIdElement.ValueKind == JsonValueKind.String &&
            UlidValue.TryParse(projectIdElement.GetString(), out var projectId))
        {
            return $"project:{projectId}";
        }

        if (!UlidValue.TryParse(message.TenantId, out var tenantId))
        {
            throw new ArgumentException("Outbox tenant must be a canonical ULID.", nameof(message));
        }

        return $"tenant:{tenantId}";
    }
}
