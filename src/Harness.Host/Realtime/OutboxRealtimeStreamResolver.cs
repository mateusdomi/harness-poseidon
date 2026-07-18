using System.Text.Json;
using Harness.Persistence.Abstractions.Messaging;
using Harness.SharedKernel.Identifiers;

namespace Harness.Host.Realtime;

public sealed class OutboxRealtimeStreamResolver
{
    public string Resolve(OutboxLease message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.EventType is "workflow.versionPublished" or "audit.eventAppended" or "agent.statusChanged" or "tool.statusChanged" or "quota.updated")
        {
            return "global";
        }
        using var document = JsonDocument.Parse(message.PayloadJson);
        if (message.EventType == "notification.created" &&
            document.RootElement.TryGetProperty("notification", out var notification) &&
            notification.ValueKind == JsonValueKind.Object &&
            notification.TryGetProperty("profileId", out var profileIdElement) &&
            profileIdElement.ValueKind == JsonValueKind.String &&
            UlidValue.TryParse(profileIdElement.GetString(), out var profileId))
        {
            return $"profile:{profileId}";
        }
        if (TryResolveConversation(document.RootElement, out var conversationStream))
        {
            return conversationStream;
        }

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

    private static bool TryResolveConversation(JsonElement payload, out string stream)
    {
        if (TryReadConversationId(payload, out var conversationId) ||
            payload.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object &&
            TryReadConversationId(message, out conversationId))
        {
            stream = $"conversation:{conversationId}";
            return true;
        }

        stream = string.Empty;
        return false;
    }

    private static bool TryReadConversationId(JsonElement value, out UlidValue conversationId)
    {
        if (value.TryGetProperty("conversationId", out var id) &&
            id.ValueKind == JsonValueKind.String &&
            UlidValue.TryParse(id.GetString(), out conversationId))
        {
            return true;
        }

        conversationId = default;
        return false;
    }
}
