using System.Text.Json;

namespace Harness.ContractTests.Coordination;

public sealed class WorkBoardContractDriftTests
{
    private static readonly Dictionary<string, string[]> Schemas = new(StringComparer.Ordinal)
    {
        ["SolicitationContract"] = ["id", "projectId", "authorProfileId", "kind", "title", "body", "state", "supersedesId", "createdAt"],
        ["DemandContract"] = ["id", "projectId", "solicitationId", "title", "description", "state", "priority", "createdAt"],
        ["BoardTaskContract"] = ["id", "projectId", "demandId", "title", "state", "priority", "assigneeAgentId", "blockedReason", "instructionVersion", "progress", "createdAt", "updatedAt", "dueAt"],
        ["TaskInstructionContract"] = ["id", "taskId", "version", "body", "authorKind", "authorId", "createdAt"],
        ["AttemptContract"] = ["id", "taskId", "number", "state", "agentId", "startedAt", "finishedAt", "durationMs", "costUsd", "tokensInput", "tokensOutput", "commitRefs", "summary", "failureReason"],
        ["AttemptEventContract"] = ["id", "attemptId", "kind", "content", "occurredAt"],
    };

    [Fact]
    public void OpenApiMatchesBoardResourcesAndFrontendFields()
    {
        var root = FindRepositoryRoot(); using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "contracts", "openapi.json")));
        var openApi = document.RootElement; var paths = openApi.GetProperty("paths");
        AssertMethods(paths, "/api/v1/solicitations", "get", "post");
        AssertMethods(paths, "/api/v1/solicitations/{id}", "get");
        AssertMethods(paths, "/api/v1/demands", "get", "post");
        AssertMethods(paths, "/api/v1/demands/{id}", "get");
        AssertMethods(paths, "/api/v1/tasks", "get", "post");
        AssertMethods(paths, "/api/v1/tasks/{id}", "get");
        AssertMethods(paths, "/api/v1/task-instructions", "get");
        AssertMethods(paths, "/api/v1/task-instructions/{id}", "get");
        AssertMethods(paths, "/api/v1/attempts", "get");
        AssertMethods(paths, "/api/v1/attempts/{id}", "get");
        AssertMethods(paths, "/api/v1/attempt-events", "get");
        AssertMethods(paths, "/api/v1/attempt-events/{id}", "get");
        foreach (var schema in Schemas) AssertSchema(openApi, schema.Key, schema.Value);

        var core = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "core.ts"));
        AssertFrontend(core, "solicitationSchema", "Solicitation", Schemas["SolicitationContract"]);
        AssertFrontend(core, "demandSchema", "Demand", Schemas["DemandContract"]);
        var delivery = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "delivery.ts"));
        AssertFrontend(delivery, "taskSchema", "Task", Schemas["BoardTaskContract"]);
        AssertFrontend(delivery, "taskInstructionSchema", "TaskInstruction", Schemas["TaskInstructionContract"]);
        AssertFrontend(delivery, "attemptSchema", "Attempt", Schemas["AttemptContract"]);
        AssertFrontend(delivery, "attemptEventSchema", "AttemptEvent", Schemas["AttemptEventContract"]);
    }

    private static void AssertMethods(JsonElement paths, string path, params string[] methods) =>
        Assert.All(methods, method => Assert.True(paths.GetProperty(path).TryGetProperty(method, out _)));
    private static void AssertSchema(JsonElement openApi, string name, string[] fields)
    {
        var actual = openApi.GetProperty("components").GetProperty("schemas").GetProperty(name)
            .GetProperty("properties").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(fields.Order(StringComparer.Ordinal), actual);
    }
    private static void AssertFrontend(string text, string schema, string type, string[] fields)
    {
        var start = text.IndexOf($"export const {schema}", StringComparison.Ordinal);
        var end = text.IndexOf($"export type {type}", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start); var block = text[start..end];
        Assert.All(fields, field => Assert.Contains($"{field}:", block, StringComparison.Ordinal));
    }
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
