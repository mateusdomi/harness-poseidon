using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Workflows;

/// <summary>
/// Resolve o template de workflow recomendado (o "Software Delivery Standard") e vincula um
/// template a um projeto. Esta é a única implementação da regra de vínculo, compartilhada pelo
/// endpoint explícito <c>POST /api/v1/projects/{id}/workflow</c> e pela pré-seleção automática na
/// criação do projeto (GP-09), para que ambos emitam exatamente os mesmos efeitos.
/// </summary>
public static class ProjectWorkflowLinker
{
    public enum LinkOutcome
    {
        Applied,
        TemplateArchived,
        TemplateUnpublished,
        VersionInvalid,
        ModeInvalid,
        AlreadyExists,
        ReferenceNotFound,
        LifecycleConflict,
    }

    public sealed record LinkResult(
        LinkOutcome Outcome,
        WorkflowBindingCatalogRecord? Binding = null,
        string? Reference = null,
        string? Detail = null);

    /// <summary>
    /// Resolve o template recomendado publicado para o tenant. Se nenhum template publicável
    /// existir ainda, semeia os canônicos e tenta de novo, garantindo que o caminho dourado
    /// sempre tenha um workflow para pré-selecionar.
    /// </summary>
    public static async Task<WorkflowTemplateCatalogRecord?> ResolveRecommendedAsync(
        IWorkflowCatalogStore store,
        WorkflowTemplateSeeder seeder,
        string tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(seeder);
        var recommended = await FindRecommendedAsync(store, tenantId, cancellationToken);
        if (recommended is not null)
        {
            return recommended;
        }

        await seeder.EnsureSeededAsync(tenantId, cancellationToken);
        return await FindRecommendedAsync(store, tenantId, cancellationToken);
    }

    private static async Task<WorkflowTemplateCatalogRecord?> FindRecommendedAsync(
        IWorkflowCatalogStore store,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var templates = await store.ListTemplatesAsync(tenantId, null, 200, cancellationToken);
        static bool Publishable(WorkflowTemplateCatalogRecord template) =>
            template.CurrentVersionId is not null && template.State != "archived";
        return templates.FirstOrDefault(template => Publishable(template)
                && string.Equals(
                    template.Name,
                    CanonicalWorkflowTemplates.Recommended.Name,
                    StringComparison.Ordinal))
            ?? templates.FirstOrDefault(Publishable);
    }

    /// <summary>
    /// Vincula <paramref name="template"/> ao projeto, resolvendo a versão publicada e o modo de
    /// operação padrão exatamente como o endpoint explícito faz.
    /// </summary>
    public static async Task<LinkResult> LinkAsync(
        IWorkflowCatalogStore store,
        string tenantId,
        string projectId,
        WorkflowTemplateCatalogRecord template,
        string? versionId,
        string actorProfileId,
        IClock clock,
        CancellationToken cancellationToken,
        string? projectOperationMode = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(clock);
        if (template.State == "archived")
        {
            return new LinkResult(LinkOutcome.TemplateArchived);
        }

        var resolvedVersionId = versionId ?? template.CurrentVersionId;
        if (resolvedVersionId is null)
        {
            return new LinkResult(LinkOutcome.TemplateUnpublished);
        }

        var version = await store.GetVersionAsync(tenantId, resolvedVersionId, cancellationToken);
        if (version is null || version.TemplateId != template.Id || version.State != "published")
        {
            return new LinkResult(LinkOutcome.VersionInvalid);
        }

        // Fase 1E: quando a VERSÃO do workflow não declara um modo, o vínculo HERDA o modo do
        // projeto. Antes forçava "manual", e o efeito era invisível e definitivo: as versões
        // canônicas publicadas pelo seeder não declaram modo, então TODO projeto nascia vinculado
        // em manual — inclusive um projeto criado como autônomo, que parava no primeiro portão sem
        // que nada explicasse por quê. "Manual" só sobra como último recurso, quando nem a versão
        // nem o projeto disseram nada, e aí devolver a decisão ao humano é a escolha segura.
        var mode = version.DefaultOperationMode ?? projectOperationMode ?? "manual";
        if (mode is not ("manual" or "semiautonomous" or "autonomous"))
        {
            return new LinkResult(LinkOutcome.ModeInvalid);
        }

        try
        {
            var now = clock.UtcNow;
            var workflowId = UlidValue.New(now).ToString();
            var row = await store.LinkTemplateAsync(
                new WorkflowTemplateLinkCommand(
                    tenantId, workflowId, projectId, template.Id, version.Id, mode,
                    actorProfileId, now),
                cancellationToken);
            return new LinkResult(LinkOutcome.Applied, row);
        }
        catch (WorkflowCatalogReferenceNotFoundException exception)
        {
            return new LinkResult(LinkOutcome.ReferenceNotFound, Reference: exception.Reference);
        }
        catch (WorkflowCatalogLifecycleException exception)
        {
            return new LinkResult(LinkOutcome.LifecycleConflict, Detail: exception.Message);
        }
        catch (WorkflowBindingAlreadyExistsException)
        {
            return new LinkResult(LinkOutcome.AlreadyExists);
        }
    }

    /// <summary>Mapeia uma falha de vínculo para a mesma resposta HTTP do endpoint explícito.</summary>
    public static IResult ToProblem(LinkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            LinkOutcome.TemplateArchived => Results.Problem(
                statusCode: 409, title: "workflow_lifecycle_conflict",
                detail: "An archived workflow template cannot be linked."),
            LinkOutcome.TemplateUnpublished => Results.Problem(
                statusCode: 409, title: "workflow_template_unpublished",
                detail: "Publish a workflow version before linking the template."),
            LinkOutcome.VersionInvalid => Results.Problem(
                statusCode: 409, title: "workflow_version_invalid",
                detail: "The workflow version must be active, published, and belong to the template."),
            LinkOutcome.ModeInvalid => Results.Problem(
                statusCode: 409, title: "workflow_version_invalid",
                detail: "The workflow version has an invalid default operation mode."),
            LinkOutcome.AlreadyExists => Results.Problem(
                statusCode: 409, title: "workflow_already_exists",
                detail: "The project already has a workflow."),
            LinkOutcome.ReferenceNotFound => Results.Problem(
                statusCode: 404, title: $"{result.Reference}_not_found",
                detail: "The resource does not exist."),
            LinkOutcome.LifecycleConflict => Results.Problem(
                statusCode: 409, title: "workflow_lifecycle_conflict",
                detail: result.Detail ?? "The workflow template lifecycle conflicts."),
            _ => throw new InvalidOperationException($"Unexpected link outcome {result.Outcome}."),
        };
    }
}
