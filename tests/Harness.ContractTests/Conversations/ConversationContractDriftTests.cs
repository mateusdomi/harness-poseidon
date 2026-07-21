using System.Text.Json;

namespace Harness.ContractTests.Conversations;

public sealed class ConversationContractDriftTests
{
    private static readonly string[] ConversationFields =
    [
        "id", "projectId", "title", "state", "createdByProfileId", "createdAt",
        "lastMessageAt",
    ];

    private static readonly string[] MessageFields =
    [
        "id", "conversationId", "authorRole", "authorProfileId", "authorAgentId",
        "content", "tokenCount", "createdAt",
    ];

    [Fact]
    public void OpenApiMatchesFrontendConversationMessageAndTurnContracts()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "contracts", "openapi.json")));
        var openApi = document.RootElement;
        var paths = openApi.GetProperty("paths");
        AssertMethods(paths, "/api/v1/conversations", "get", "post");
        AssertMethods(paths, "/api/v1/conversations/{conversationId}", "get", "delete");
        AssertMethods(paths, "/api/v1/conversations/{conversationId}/turns", "post");
        AssertMethods(paths, "/api/v1/messages", "get", "post");
        AssertMethods(paths, "/api/v1/messages/{messageId}", "get");

        AssertSchema(openApi, "ConversationResponse", ConversationFields);
        AssertSchema(openApi, "MessageResponse", MessageFields);
        // C4/ADR-019: comando idempotente da primeira conversa do projeto.
        Assert.True(openApi.GetProperty("paths")
            .GetProperty("/api/v1/projects/{projectId}/conversations/primary")
            .TryGetProperty("post", out _));

        // C2/ADR-019: o turno expõe estado tipado, bloqueadores, próximas ações e prontidão.
        AssertSchema(openApi, "ChatTurnHandle",
            ["turnId", "conversationId", "state", "correlationId", "readiness", "blockers",
             "nextActions", "links"]);
        AssertSchema(openApi, "ChatTurnBlocker", ["code", "relatedIds"]);
        AssertSchema(openApi, "ChatTurnNextAction", ["code", "route", "resourceId"]);
        AssertSchema(openApi, "ChatTurnReadiness", ["overallState", "executionState"]);
        AssertSchema(openApi, "ChatTurnLinks", ["readiness", "conversation"]);
        AssertSchema(openApi, "StartChatTurnRequest",
            ["content", "accountId", "modelId", "effort", "fallbackModelIds", "selectionReason"]);

        var frontend = File.ReadAllText(
            Path.Combine(root, "frontend", "src", "api", "contracts", "core.ts"));
        AssertFrontendBlock(frontend, "conversationSchema", "Conversation", ConversationFields);
        AssertFrontendBlock(frontend, "messageSchema", "Message", MessageFields);

        var events = File.ReadAllText(
            Path.Combine(root, "frontend", "src", "api", "contracts", "events.ts"));
        Assert.Contains("chatTurnStartedPayloadSchema", events, StringComparison.Ordinal);
        Assert.Contains("chatTurnChunkPayloadSchema", events, StringComparison.Ordinal);
        Assert.Contains("chatTurnCompletedPayloadSchema", events, StringComparison.Ordinal);
        Assert.Contains("messageAppendedPayloadSchema", events, StringComparison.Ordinal);
    }

    private static void AssertMethods(JsonElement paths, string path, params string[] methods)
    {
        var item = paths.GetProperty(path);
        Assert.All(methods, method => Assert.True(item.TryGetProperty(method, out _)));
    }

    private static void AssertSchema(JsonElement openApi, string name, string[] fields)
    {
        var properties = openApi.GetProperty("components").GetProperty("schemas")
            .GetProperty(name).GetProperty("properties")
            .EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(fields.Order(StringComparer.Ordinal), properties);
    }

    private static void AssertFrontendBlock(
        string frontend,
        string schemaName,
        string typeName,
        string[] fields)
    {
        var start = frontend.IndexOf($"export const {schemaName}", StringComparison.Ordinal);
        var end = frontend.IndexOf($"export type {typeName}", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var block = frontend[start..end];
        Assert.All(fields, field => Assert.Contains($"{field}:", block, StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
