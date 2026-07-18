using System.Text.Json;

namespace Harness.ContractTests.Documents;

public sealed class DocumentContractDriftTests
{
    [Fact]
    public void OpenApiMatchesDocumentResourcesAndFrontendFields()
    {
        var root = Root(); using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "contracts", "openapi.json")));
        var api = document.RootElement; var paths = api.GetProperty("paths");
        Methods(paths, "/api/v1/documents", "get", "post");
        Methods(paths, "/api/v1/documents/{id}", "get");
        Methods(paths, "/api/v1/documents/{id}/classification", "post");
        Methods(paths, "/api/v1/documents/{id}/transitions", "post");
        Methods(paths, "/api/v1/document-versions", "get", "post");
        Methods(paths, "/api/v1/document-versions/{id}", "get");
        Methods(paths, "/api/v1/approvals", "get", "post");
        Methods(paths, "/api/v1/approvals/{id}", "get");
        Methods(paths, "/api/v1/approvals/{id}/resolution", "post");
        var documentFields = new[] { "id", "projectId", "title", "kind", "state", "currentVersion", "classifications", "phaseName", "inconsistent", "waiver", "createdAt", "updatedAt" };
        var versionFields = new[] { "id", "documentId", "version", "body", "authorKind", "authorId", "createdAt" };
        Schema(api, "DocumentContract", documentFields); Schema(api, "DocumentVersionContract", versionFields);
        var approvalFields = new[] { "id", "projectId", "gateId", "taskId", "documentId", "title", "description", "priority", "dueAt", "state", "requestedByAgentId", "requestedAt", "resolvedByProfileId", "resolvedAt", "resolutionNote" };
        Schema(api, "ApprovalContract", approvalFields);
        var frontend = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "content.ts"));
        Frontend(frontend, "documentSchema", "Document", documentFields);
        Frontend(frontend, "documentVersionSchema", "DocumentVersion", versionFields);
        var delivery = File.ReadAllText(Path.Combine(root, "frontend", "src", "api", "contracts", "delivery.ts"));
        Frontend(delivery, "approvalSchema", "Approval", approvalFields);
    }

    private static void Methods(JsonElement paths, string path, params string[] methods) =>
        Assert.All(methods, method => Assert.True(paths.GetProperty(path).TryGetProperty(method, out _)));
    private static void Schema(JsonElement api, string name, string[] fields) => Assert.Equal(
        fields.Order(StringComparer.Ordinal), api.GetProperty("components").GetProperty("schemas")
            .GetProperty(name).GetProperty("properties").EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
    private static void Frontend(string text, string schema, string type, string[] fields)
    {
        var start = text.IndexOf($"export const {schema}", StringComparison.Ordinal);
        var end = text.IndexOf($"export type {type}", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start); var block = text[start..end];
        Assert.All(fields, field => Assert.Contains($"{field}:", block, StringComparison.Ordinal));
    }
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
