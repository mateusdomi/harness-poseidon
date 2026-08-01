using System.Text.RegularExpressions;
using Harness.Host.Agents;
using Harness.Modules.Documents.Application;
using Harness.Modules.Execution.Infrastructure.Git;
using Harness.Modules.Workflows.Application;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Time;

namespace Harness.Host.Documents;

public sealed record ApprovedDocumentPublishResult(
    bool Published,
    string ReasonCode,
    string? DocumentId = null,
    string? DocumentVersionId = null,
    string? SourcePath = null);

public sealed record SupersededDocumentProof(
    string DocumentId,
    string DocumentVersionId,
    string AttemptId,
    string ReviewId,
    string SourcePath,
    string EvidenceReference);

/// <summary>
/// Materializa no catálogo do Poseidon o Markdown produzido e aprovado por outro profissional.
/// Git continua sendo a evidência da entrega; o catálogo vira a projeção navegável e versionada.
/// IDs iguais aos da cadeia (documento inicial=card, versão=tentativa) tornam o elo idempotente e
/// permitem navegar card → tentativa → versão sem tabela ou estado paralelo inventado.
/// </summary>
public sealed partial class ApprovedDocumentCatalogPublisher(
    IDocumentStore documents,
    IDocumentCatalogStore catalog,
    IDocumentContentCatalog content,
    IWorkflowDocumentTemplateStore templates,
    IWorkChainStore chain,
    IClock clock)
{
    private static readonly Regex TemplateMarker = new(
        @"(?m)^- Template:\s*(?<code>[A-Za-z0-9_-]{2,4})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<ApprovedDocumentPublishResult> PublishAsync(
        string tenantId,
        ProjectRecord project,
        BoardTaskRecord task,
        IWorkBoardStore board,
        string controlledRoot,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(task.CardType, "documento", StringComparison.Ordinal))
        {
            return new(false, "document.card_type_not_document");
        }

        var attempts = await board.ListAttemptsAsync(tenantId, task.Id, null, 100, cancellationToken);
        // BoardAttemptRecord.State é a projeção OPERACIONAL (completed/failed/cancelled), não o
        // estado lógico interno (awaiting_review/approved/rejected). A aprovação já está provada
        // pelo estado `approved` do card que chama este método; aqui selecionamos a entrega
        // operacional concluída correspondente. Procurar "approved" tornava a publicação
        // impossível por construção.
        var attempt = SelectDeliveredAttempt(attempts);
        if (attempt is null)
        {
            return new(false, "document.approved_attempt_missing");
        }

        var instructions = await board.ListInstructionsAsync(tenantId, task.Id, null, 100, cancellationToken);
        if (instructions.Count == 0)
        {
            return new(false, "document.instruction_missing");
        }

        var branch = $"task/agent-run-{attempt.Id.ToLowerInvariant()}";
        using var git = await GitWorktreeManager.OpenAsync(
            Path.GetFullPath(project.RepositoryUrl!), controlledRoot, cancellationToken);
        var artifacts = SelectDocumentArtifacts(
            await git.ListBranchChangedFilesAsync(branch, cancellationToken));
        if (artifacts.Count != 1)
        {
            return new(false, artifacts.Count == 0
                ? "document.artifact_missing"
                : "document.artifact_ambiguous");
        }

        var sourcePath = artifacts[0];
        var body = await git.ReadDocumentFromBranchAsync(branch, sourcePath, cancellationToken);
        if (ChiefBacklogLoopService.ForbiddenDeliveryPlaceholders(body).Count > 0)
        {
            return new(false, "document.delivery_placeholder", SourcePath: sourcePath);
        }

        var templateCode = ParseTemplateCode(instructions[^1].Body);
        if (templateCode is not null)
        {
            var template = (await templates.ListAsync(cancellationToken)).FirstOrDefault(candidate =>
                string.Equals(candidate.Code, templateCode, StringComparison.OrdinalIgnoreCase));
            if (template is null)
            {
                return new(false, "document.template_unknown", SourcePath: sourcePath);
            }

            var compliance = DocumentTemplateCompliance.Check(body, template.RequiredFieldsJson);
            if (!compliance.IsCompliant)
            {
                return new(false, "document.template_not_satisfied", SourcePath: sourcePath);
            }
        }

        var matching = (await catalog.ListDocumentsAsync(
                tenantId, project.Id, null, 500, cancellationToken))
            .Where(candidate =>
                string.Equals(candidate.PhaseName, task.PhaseName, StringComparison.Ordinal) &&
                string.Equals(candidate.TemplateCode, templateCode, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matching.Length > 1)
        {
            return new(false, "document.catalog_ambiguous", SourcePath: sourcePath);
        }

        var documentId = matching.Length == 1 ? matching[0].Id : task.Id;
        var versionId = attempt.Id;
        var prepared = DocumentApiApplicationService.Prepare(tenantId, documentId, versionId, body);
        await content.WriteAsync(prepared.CatalogPath, prepared.Body, prepared.ContentHash, cancellationToken);
        try
        {
            if (matching.Length == 0)
            {
                _ = await documents.CreateAsync(
                    new DocumentCreateCommand(
                        tenantId,
                        project.Id,
                        documentId,
                        ExtractTitle(body, task.Title),
                        InferKind(task.PhaseName, templateCode, sourcePath),
                        ["playbook"],
                        task.PhaseName,
                        versionId,
                        prepared.CatalogPath,
                        prepared.ContentHash,
                        "agent",
                        attempt.AgentId,
                        $"approved-document:{attempt.Id}",
                        clock.UtcNow,
                        OriginReference: task.Id,
                        TemplateCode: templateCode),
                    cancellationToken);
            }
            else
            {
                var snapshot = await documents.ReadAsync(tenantId, documentId, cancellationToken)
                    ?? throw new InvalidOperationException("The catalog document disappeared before append.");
                if (snapshot.Versions.Any(version =>
                        string.Equals(version.DocumentVersionId, versionId, StringComparison.Ordinal)))
                {
                    var converged = await EnsureReviewedDocumentApprovedAsync(
                        tenantId, task, documentId, cancellationToken);
                    return converged
                        ? new(true, "document.version_already_published", documentId, versionId, sourcePath)
                        : new(false, "document.approved_state_not_converged", documentId, versionId, sourcePath);
                }

                var receipt = await documents.AppendVersionAsync(
                    new DocumentVersionAppendCommand(
                        tenantId,
                        documentId,
                        versionId,
                        prepared.CatalogPath,
                        prepared.ContentHash,
                        "agent",
                        attempt.AgentId,
                        snapshot.Version,
                        $"approved-document:{attempt.Id}",
                        clock.UtcNow),
                    cancellationToken);
                if (receipt.Status is not (DocumentMutationStatus.Applied or DocumentMutationStatus.IdempotentReplay))
                {
                    throw new InvalidOperationException($"Document append was refused: {receipt.Status}.");
                }
            }
        }
        catch
        {
            await content.DeleteAsync(prepared.CatalogPath, CancellationToken.None);
            throw;
        }

        return await EnsureReviewedDocumentApprovedAsync(
                tenantId, task, documentId, cancellationToken)
            ? new(true, "document.published", documentId, versionId, sourcePath)
            : new(false, "document.approved_state_not_converged", documentId, versionId, sourcePath);
    }

    /// <summary>
    /// Converge a projeção documental com o fato já provado na cadeia: card documental aprovado
    /// por revisor distinto. Serve também para documentos publicados por uma versão anterior que
    /// ficaram incorretamente em `in_elaboration` após o card ser integrado.
    /// </summary>
    internal async Task<bool> EnsureReviewedDocumentApprovedAsync(
        string tenantId,
        BoardTaskRecord task,
        string documentId,
        CancellationToken cancellationToken)
    {
        var snapshot = await documents.ReadAsync(tenantId, documentId, cancellationToken);
        if (snapshot is null)
        {
            return false;
        }

        if (string.Equals(snapshot.State, "approved", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(snapshot.State, "in_elaboration", StringComparison.Ordinal) ||
            snapshot.Versions.Count == 0)
        {
            return false;
        }

        var reviewedVersion = snapshot.Versions[^1];
        var aggregate = await chain.ReadAggregateAsync(
            tenantId, task.BackingSolicitationId, cancellationToken);
        var review = SelectApprovedReview(aggregate, task.Id, reviewedVersion.DocumentVersionId);
        if (review is null)
        {
            return false;
        }

        var receipt = await documents.TransitionAsync(
            new DocumentTransitionCommand(
                tenantId,
                documentId,
                review.ReviewId,
                "approved",
                $"approved-card:{task.Id};review-attempt:{review.ReviewId}",
                "system",
                null,
                snapshot.Version,
                $"approved-document-review:{review.ReviewId}",
                review.CreatedAt),
            cancellationToken);
        return receipt.Status is DocumentMutationStatus.Applied or DocumentMutationStatus.IdempotentReplay;
    }

    internal static WorkReviewSnapshot? SelectApprovedReview(
        WorkChainAggregateSnapshot? aggregate,
        string taskId,
        string attemptId)
    {
        var attempt = aggregate?.Demands
            .SelectMany(demand => demand.Tasks)
            .SingleOrDefault(task => string.Equals(task.TaskId, taskId, StringComparison.Ordinal))?
            .Attempts
            .SingleOrDefault(candidate =>
                string.Equals(candidate.AttemptId, attemptId, StringComparison.Ordinal));
        return attempt?.Review is { Decision: "approved" } review &&
               !string.Equals(attempt.ProducerAgentId, review.ReviewerAgentId, StringComparison.Ordinal)
            ? review
            : null;
    }

    /// <summary>
    /// Prova estrita para recuperar cards documentais legados que nasceram como `agent_task` e
    /// cujo branch aprovado foi substituído por uma versão posterior também revisada. Só aceita
    /// quando o conteúdo atualmente publicado no Git é byte a byte o blob aprovado do catálogo.
    /// </summary>
    internal async Task<SupersededDocumentProof?> ProveSupersededLegacyDocumentAsync(
        string tenantId,
        ProjectRecord project,
        BoardTaskRecord task,
        IWorkBoardStore board,
        string controlledRoot,
        CancellationToken cancellationToken)
    {
        if (string.Equals(task.CardType, "documento", StringComparison.Ordinal) ||
            !string.Equals(task.InternalState, "approved", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(task.PhaseName) ||
            string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            return null;
        }

        var instructions = await board.ListInstructionsAsync(
            tenantId, task.Id, null, 100, cancellationToken);
        var latestInstruction = instructions.Count == 0 ? null : instructions[^1].Body;
        if (latestInstruction is null ||
            !latestInstruction.Contains("Tipo de card: documento", StringComparison.Ordinal) ||
            ParseTemplateCode(latestInstruction) is not { } templateCode)
        {
            return null;
        }

        var attempts = await board.ListAttemptsAsync(
            tenantId, task.Id, null, 100, cancellationToken);
        var attempt = SelectDeliveredAttempt(attempts);
        if (attempt is null)
        {
            return null;
        }

        var aggregate = await chain.ReadAggregateAsync(
            tenantId, task.BackingSolicitationId, cancellationToken);
        var review = SelectApprovedReview(aggregate, task.Id, attempt.Id);
        if (review is null)
        {
            return null;
        }

        var candidates = (await catalog.ListDocumentsAsync(
                tenantId, project.Id, null, 500, cancellationToken))
            .Where(document =>
                !string.Equals(document.Id, task.Id, StringComparison.Ordinal) &&
                string.Equals(document.State, "approved", StringComparison.Ordinal) &&
                string.Equals(document.PhaseName, task.PhaseName, StringComparison.Ordinal) &&
                string.Equals(document.TemplateCode, templateCode, StringComparison.OrdinalIgnoreCase) &&
                document.UpdatedAt >= review.CreatedAt)
            .ToArray();
        if (candidates.Length != 1)
        {
            return null;
        }

        var approvedDocument = candidates[0];
        var versions = await catalog.PageVersionsAsync(
            tenantId, approvedDocument.Id, 0, 1, cancellationToken);
        var currentVersion = versions.Items.SingleOrDefault();
        if (currentVersion is null || currentVersion.Version != approvedDocument.CurrentVersion)
        {
            return null;
        }

        var branch = $"task/agent-run-{attempt.Id.ToLowerInvariant()}";
        using var git = await GitWorktreeManager.OpenAsync(
            Path.GetFullPath(project.RepositoryUrl!), controlledRoot, cancellationToken);
        var artifacts = SelectDocumentArtifacts(
            await git.ListBranchChangedFilesAsync(branch, cancellationToken));
        if (artifacts.Count != 1)
        {
            return null;
        }

        var sourcePath = artifacts[0];
        var oldBody = await git.ReadDocumentFromBranchAsync(branch, sourcePath, cancellationToken);
        var publishedBody = await git.ReadPublishedDocumentAsync(
            project.DefaultBranch ?? "main", sourcePath, cancellationToken);
        var catalogBody = await content.ReadAsync(
            currentVersion.CatalogPath, currentVersion.ContentHash, cancellationToken);
        if (string.Equals(oldBody, publishedBody, StringComparison.Ordinal) ||
            !string.Equals(publishedBody, catalogBody, StringComparison.Ordinal))
        {
            return null;
        }

        return new SupersededDocumentProof(
            approvedDocument.Id,
            currentVersion.Id,
            attempt.Id,
            review.ReviewId,
            sourcePath,
            $"document:{approvedDocument.Id}:version:{currentVersion.Id}:hash:{currentVersion.ContentHash}");
    }

    internal static IReadOnlyList<string> SelectDocumentArtifacts(IEnumerable<string> changedFiles) =>
        [.. changedFiles
            .Where(path => path.StartsWith("docs/", StringComparison.Ordinal) &&
                           path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    internal static BoardAttemptRecord? SelectDeliveredAttempt(
        IEnumerable<BoardAttemptRecord> attempts) =>
        attempts.LastOrDefault(candidate =>
            string.Equals(candidate.State, "completed", StringComparison.Ordinal));

    internal static string? ParseTemplateCode(string instruction)
    {
        var match = TemplateMarker.Match(instruction);
        return match.Success ? match.Groups["code"].Value : null;
    }

    internal static string ExtractTitle(string body, string fallback)
    {
        var heading = body.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal));
        var title = heading is null ? fallback : heading[2..].Trim();
        return title.Length <= 500 ? title : title[..500];
    }

    internal static string InferKind(string? phaseName, string? templateCode, string sourcePath)
    {
        if (string.Equals(templateCode, "01", StringComparison.OrdinalIgnoreCase) ||
            sourcePath.Contains("prd", StringComparison.OrdinalIgnoreCase))
        {
            return "prd";
        }

        if (phaseName?.Contains("Arquitetura", StringComparison.OrdinalIgnoreCase) == true ||
            sourcePath.Contains("adr", StringComparison.OrdinalIgnoreCase) ||
            sourcePath.Contains("design", StringComparison.OrdinalIgnoreCase))
        {
            return "design";
        }

        return sourcePath.Contains("runbook", StringComparison.OrdinalIgnoreCase)
            ? "runbook"
            : "report";
    }
}
