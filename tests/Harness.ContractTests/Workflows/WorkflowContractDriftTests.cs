using System.Text.Json;

namespace Harness.ContractTests.Workflows;

public sealed class WorkflowContractDriftTests
{
    private static readonly Dictionary<string, string[]> Schemas = new(StringComparer.Ordinal)
    {
        ["WorkflowTemplateContract"] = ["id", "name", "description", "currentVersionId", "state", "archivedAt", "createdAt", "recommended"],
        ["WorkflowVersionContract"] = ["id", "templateId", "version", "phases", "gatesByPhase", "phaseConfigs", "defaultOperationMode", "transitions", "changelog", "state", "publishedAt", "archivedAt"],
        ["WorkflowContract"] = ["id", "projectId", "templateId", "activeVersionId", "operationMode", "semiautonomousPauseGates", "riskAcceptances", "createdAt"],
        ["WorkflowRunContract"] = ["id", "workflowId", "versionId", "state", "startedAt", "finishedAt"],
        ["PhaseContract"] = ["id", "runId", "name", "order", "state", "startedAt", "finishedAt", "progress", "deliverables"],
        ["GateContract"] = ["id", "phaseId", "runId", "name", "state", "requiresApproval", "decidedByProfileId", "decidedAt", "note"],
    };

    [Fact]
    public void OpenApiMatchesWorkflowResourcesAndFrontendFields()
    {
        var root = Root(); using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "contracts", "openapi.json"))); var api = document.RootElement; var paths = api.GetProperty("paths");
        Methods(paths, "/api/v1/workflow-templates", "get", "post"); Methods(paths, "/api/v1/workflow-templates/{id}", "get", "delete");
        Methods(paths, "/api/v1/workflow-templates/{id}/versions", "post");
        Methods(paths, "/api/v1/workflow-templates/{id}/drafts", "post");
        Methods(paths, "/api/v1/workflow-templates/{id}/archive", "post");
        Methods(paths, "/api/v1/workflow-templates/{id}/duplicate", "post");
        Methods(paths, "/api/v1/workflow-versions", "get"); Methods(paths, "/api/v1/workflow-versions/{id}", "get", "patch", "delete");
        Methods(paths, "/api/v1/workflow-versions/{id}/publish", "post");
        Methods(paths, "/api/v1/workflow-versions/{id}/archive", "post");
        Methods(paths, "/api/v1/workflow-versions/{id}/duplicate", "post");
        Methods(paths, "/api/v1/workflows", "get", "post"); Methods(paths, "/api/v1/workflows/{id}", "get");
        Methods(paths, "/api/v1/workflows/{id}/operation-mode", "post");
        Methods(paths, "/api/v1/projects/{id}/workflow", "post");
        Methods(paths, "/api/v1/workflow-runs", "get", "post"); Methods(paths, "/api/v1/workflow-runs/{id}", "get");
        Methods(paths, "/api/v1/workflow-runs/{id}/transitions", "post");
        Methods(paths, "/api/v1/workflow-runs/{id}/objectives", "post");
        Methods(paths, "/api/v1/workflow-runs/{id}/gates", "post");
        Methods(paths, "/api/v1/workflow-runs/{id}/phases/{phaseKey}/completion", "post");
        Methods(paths, "/api/v1/phases", "get"); Methods(paths, "/api/v1/phases/{id}", "get"); Methods(paths, "/api/v1/gates", "get"); Methods(paths, "/api/v1/gates/{id}", "get");
        foreach (var schema in Schemas) Schema(api, schema.Key, schema.Value);
        var frontend = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "delivery.ts"));
        Frontend(frontend, "workflowTemplateSchema", "WorkflowTemplate", Schemas["WorkflowTemplateContract"]);
        Frontend(frontend, "workflowVersionSchema", "WorkflowVersion", Schemas["WorkflowVersionContract"]);
        Frontend(frontend, "workflowSchema", "Workflow", Schemas["WorkflowContract"]);
        Frontend(frontend, "workflowRunSchema", "WorkflowRun", Schemas["WorkflowRunContract"]);
        Frontend(frontend, "phaseSchema", "Phase", Schemas["PhaseContract"]); Frontend(frontend, "gateSchema", "Gate", Schemas["GateContract"]);
    }
    private static void Methods(JsonElement paths, string path, params string[] methods) => Assert.All(methods, m => Assert.True(paths.GetProperty(path).TryGetProperty(m, out _)));
    private static void Schema(JsonElement api, string name, string[] fields) { var actual = api.GetProperty("components").GetProperty("schemas").GetProperty(name).GetProperty("properties").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal); Assert.Equal(fields.Order(StringComparer.Ordinal), actual); }
    private static void Frontend(string text, string schema, string type, string[] fields) { var start = text.IndexOf($"export const {schema}", StringComparison.Ordinal); var end = text.IndexOf($"export type {type}", start, StringComparison.Ordinal); Assert.True(start >= 0 && end > start); var block = text[start..end]; Assert.All(fields, f => Assert.Contains($"{f}:", block, StringComparison.Ordinal)); }
    private static string Root() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "Harness.sln"))) d = d.Parent; return d?.FullName ?? throw new DirectoryNotFoundException(); }
}
