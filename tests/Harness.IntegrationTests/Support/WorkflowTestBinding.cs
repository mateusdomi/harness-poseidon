using System.Net.Http.Json;
using Harness.Host.Workflows;
using Harness.Modules.Workflows.Contracts;

namespace Harness.IntegrationTests.Support;

/// <summary>
/// Vincula ao projeto o template de workflow semeado, para fixtures que precisam exercitar a
/// execução real do Chief. Desde o C2 (ADR-019) um turno só é enfileirado quando a prontidão
/// canônica autoriza, e `WorkflowReady` faz parte dela: um projeto sem workflow existe, mas não
/// parece pronto para executar. As fixtures declaram esse pré-requisito explicitamente.
/// </summary>
public static class WorkflowTestBinding
{
    public static async Task BindRecommendedAsync(
        HttpClient client,
        string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var templates = await client.GetFromJsonAsync<WorkflowTemplatePage>(
            "/api/v1/workflow-templates?limit=50", cancellationToken)
            ?? throw new InvalidOperationException("Workflow templates were not published.");
        var template = templates.Items.FirstOrDefault(item => item.CurrentVersionId is not null)
            ?? throw new InvalidOperationException(
                "No published workflow template is available to bind.");
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/workflow",
            new LinkWorkflowTemplateRequest(template.Id),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Binding the workflow failed with {(int)response.StatusCode}: " +
                await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
}
