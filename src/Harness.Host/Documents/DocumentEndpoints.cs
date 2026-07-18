using Harness.Host.Profiles;
using Harness.Modules.Documents.Application;
using Harness.Modules.Documents.Contracts;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Documents;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentCatalog(this IEndpointRouteBuilder endpoints)
    {
        var documents = endpoints.MapGroup("/api/v1/documents").WithTags("documents");
        documents.MapGet("/", ListDocumentsAsync).Produces<DocumentPage>().ProducesProblem(400).ProducesProblem(401);
        documents.MapGet("/{id}", GetDocumentAsync).Produces<DocumentContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        documents.MapPost("/", CreateDocumentAsync).Produces<DocumentContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        documents.MapPost("/{id}/classification", ClassifyDocumentAsync).Produces<DocumentContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        documents.MapPost("/{id}/transitions", TransitionDocumentAsync).Produces<DocumentContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);

        var versions = endpoints.MapGroup("/api/v1/document-versions").WithTags("document-versions");
        versions.MapGet("/", ListVersionsAsync).Produces<DocumentVersionPage>().ProducesProblem(400).ProducesProblem(401);
        versions.MapGet("/{id}", GetVersionAsync).Produces<DocumentVersionContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        versions.MapPost("/", CreateVersionAsync).Produces<DocumentVersionContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        var approvals = endpoints.MapGroup("/api/v1/approvals").WithTags("approvals");
        approvals.MapGet("/", ListApprovalsAsync).Produces<ApprovalPage>().ProducesProblem(400).ProducesProblem(401);
        approvals.MapGet("/{id}", GetApprovalAsync).Produces<ApprovalContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        approvals.MapPost("/", CreateApprovalAsync).Produces<ApprovalContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        approvals.MapPost("/{id}/resolution", ResolveApprovalAsync).Produces<ApprovalContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> ListDocumentsAsync(
        string? projectId, string? cursor, int? limit, HttpRequest request,
        ILocalProfileStore profiles, IDocumentCatalogStore store, CancellationToken token)
    {
        var invalid = Page(cursor, limit, projectId); if (invalid is not null) return invalid;
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var size = limit ?? 100;
        var rows = await store.ListDocumentsAsync(profile.TenantId, projectId, cursor, size + 1, token);
        var more = rows.Count > size; var selected = rows.Take(size).ToArray();
        return Results.Ok(new DocumentPage(selected.Select(ToContract).ToArray(),
            more ? selected[^1].Id : null));
    }

    private static async Task<IResult> GetDocumentAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IDocumentCatalogStore store,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var row = await store.GetDocumentAsync(profile.TenantId, id, token);
        return row is null ? NotFound("document") : Results.Ok(ToContract(row));
    }

    private static async Task<IResult> CreateDocumentAsync(
        CreateDocumentRequest input, HttpRequest request, ILocalProfileStore profiles,
        IProjectStore projects, IDocumentStore authority, IDocumentCatalogStore store,
        IDocumentContentCatalog content, IClock clock, CancellationToken token)
    {
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        try
        {
            if (!Valid(input.ProjectId)) return Problem(400, "invalid_project", "Project ID must be a ULID.");
            if (await projects.GetAsync(profile.TenantId, input.ProjectId, token) is null) return NotFound("project");
            var normalized = DocumentApiApplicationService.Normalize(input); var now = clock.UtcNow;
            var documentId = UlidValue.New(now).ToString(); var versionId = UlidValue.New(now.AddTicks(1)).ToString();
            var prepared = DocumentApiApplicationService.Prepare(profile.TenantId, documentId, versionId, input.Body);
            await content.WriteAsync(prepared.CatalogPath, prepared.Body, prepared.ContentHash, token);
            try
            {
                await authority.CreateAsync(new(profile.TenantId, input.ProjectId, documentId,
                    normalized.Title, normalized.Kind, normalized.Classifications, normalized.PhaseName,
                    versionId, prepared.CatalogPath, prepared.ContentHash, "user", profile.Id,
                    $"api:document:create:{documentId}", now), token);
            }
            catch
            {
                await content.DeleteAsync(prepared.CatalogPath, CancellationToken.None);
                throw;
            }
            var row = await store.GetDocumentAsync(profile.TenantId, documentId, token)
                ?? throw new InvalidOperationException("Created document was not readable.");
            return Results.Created($"/api/v1/documents/{documentId}", ToContract(row));
        }
        catch (ArgumentException exception) { return Problem(400, "invalid_document", exception.Message); }
        catch (Exception exception) when (exception.GetType().Name == "IdempotencyConflictException")
        { return Problem(409, "document_conflict", exception.Message); }
    }

    private static async Task<IResult> ListVersionsAsync(
        string? documentId, string? cursor, int? limit, HttpRequest request,
        ILocalProfileStore profiles, IDocumentCatalogStore store, IDocumentContentCatalog content,
        CancellationToken token)
    {
        var invalid = Page(cursor, limit, documentId); if (invalid is not null) return invalid;
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var size = limit ?? 100;
        var rows = await store.ListVersionsAsync(profile.TenantId, documentId, cursor, size + 1, token);
        var more = rows.Count > size; var selected = rows.Take(size).ToArray();
        var contracts = new List<DocumentVersionContract>();
        foreach (var row in selected) contracts.Add(await ToContractAsync(row, content, token));
        return Results.Ok(new DocumentVersionPage(contracts, more ? selected[^1].Id : null));
    }

    private static async Task<IResult> GetVersionAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IDocumentCatalogStore store,
        IDocumentContentCatalog content, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var row = await store.GetVersionAsync(profile.TenantId, id, token);
        return row is null ? NotFound("document_version") : Results.Ok(await ToContractAsync(row, content, token));
    }

    private static async Task<IResult> CreateVersionAsync(
        CreateDocumentVersionRequest input, HttpRequest request, ILocalProfileStore profiles,
        IDocumentStore authority, IDocumentCatalogStore store, IDocumentContentCatalog content,
        IClock clock, CancellationToken token)
    {
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        if (!Valid(input.DocumentId)) return Problem(400, "invalid_document", "Document ID must be a ULID.");
        var snapshot = await authority.ReadAsync(profile.TenantId, input.DocumentId, token);
        if (snapshot is null) return NotFound("document");
        try
        {
            var now = clock.UtcNow; var versionId = UlidValue.New(now).ToString();
            var prepared = DocumentApiApplicationService.Prepare(profile.TenantId, input.DocumentId, versionId, input.Body);
            await content.WriteAsync(prepared.CatalogPath, prepared.Body, prepared.ContentHash, token);
            DocumentMutationReceipt receipt;
            try
            {
                receipt = await authority.AppendVersionAsync(new(profile.TenantId, input.DocumentId,
                    versionId, prepared.CatalogPath, prepared.ContentHash, "user", profile.Id,
                    snapshot.Version, $"api:document-version:{versionId}", now), token);
            }
            catch
            {
                await content.DeleteAsync(prepared.CatalogPath, CancellationToken.None);
                throw;
            }
            if (receipt.Status is not (DocumentMutationStatus.Applied or DocumentMutationStatus.IdempotentReplay))
            {
                await content.DeleteAsync(prepared.CatalogPath, CancellationToken.None);
                return MutationProblem(receipt.Status);
            }
            var row = await store.GetVersionAsync(profile.TenantId, versionId, token)
                ?? throw new InvalidOperationException("Created document version was not readable.");
            return Results.Created($"/api/v1/document-versions/{versionId}", await ToContractAsync(row, content, token));
        }
        catch (ArgumentException exception) { return Problem(400, "invalid_document_version", exception.Message); }
    }

    private static async Task<IResult> ClassifyDocumentAsync(
        string id, ClassifyDocumentRequest input, HttpRequest request, ILocalProfileStore profiles,
        IDocumentStore authority, IDocumentCatalogStore store, IClock clock, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var current = await authority.ReadAsync(profile.TenantId, id, token); if (current is null) return NotFound("document");
        try
        {
            var value = DocumentApiApplicationService.Classify(input, current.Classifications, current.PhaseName);
            var now = clock.UtcNow; var receipt = await authority.UpdateMetadataAsync(new(
                profile.TenantId, id, value.Classifications, value.PhaseName, current.Inconsistent,
                current.Version, $"api:document-classification:{UlidValue.New(now)}", now), token);
            if (receipt.Status is not (DocumentMutationStatus.Applied or DocumentMutationStatus.IdempotentReplay))
                return MutationProblem(receipt.Status);
            var row = await store.GetDocumentAsync(profile.TenantId, id, token)
                ?? throw new InvalidOperationException("Classified document was not readable.");
            return Results.Ok(ToContract(row));
        }
        catch (ArgumentException exception) { return Problem(400, "invalid_document_classification", exception.Message); }
    }

    private static async Task<IResult> TransitionDocumentAsync(
        string id, TransitionDocumentRequest input, HttpRequest request, ILocalProfileStore profiles,
        IDocumentStore authority, IDocumentCatalogStore store, IClock clock, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var current = await authority.ReadAsync(profile.TenantId, id, token); if (current is null) return NotFound("document");
        try
        {
            var target = DocumentApiApplicationService.ToStoreState(input.ToState);
            // A aprovação F1 faz in_review -> awaiting_approval atomicamente com a solicitação.
            // O frontend envia antes uma intenção de transição; mantemos o agregado válido até o POST /approvals.
            if (target == "awaiting_approval" && current.State == "in_review")
            {
                var unchanged = await store.GetDocumentAsync(profile.TenantId, id, token)
                    ?? throw new InvalidOperationException("Document was not readable.");
                return Results.Ok(ToContract(unchanged));
            }
            var now = clock.UtcNow; var receipt = await authority.TransitionAsync(new(
                profile.TenantId, id, UlidValue.New(now).ToString(), target,
                string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(), "user", profile.Id,
                current.Version, $"api:document-transition:{UlidValue.New(now.AddTicks(1))}", now), token);
            if (receipt.Status is not (DocumentMutationStatus.Applied or DocumentMutationStatus.IdempotentReplay))
                return MutationProblem(receipt.Status);
            var row = await store.GetDocumentAsync(profile.TenantId, id, token)
                ?? throw new InvalidOperationException("Transitioned document was not readable.");
            return Results.Ok(ToContract(row));
        }
        catch (ArgumentException exception) { return Problem(400, "invalid_document_transition", exception.Message); }
    }

    private static async Task<IResult> ListApprovalsAsync(
        string? projectId, string? cursor, int? limit, HttpRequest request,
        ILocalProfileStore profiles, IDocumentCatalogStore store, CancellationToken token)
    {
        var invalid = Page(cursor, limit, projectId); if (invalid is not null) return invalid;
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var size = limit ?? 100;
        var rows = await store.ListApprovalsAsync(profile.TenantId, projectId, cursor, size + 1, token);
        var more = rows.Count > size; var selected = rows.Take(size).ToArray();
        return Results.Ok(new ApprovalPage(selected.Select(ToContract).ToArray(), more ? selected[^1].Id : null));
    }

    private static async Task<IResult> GetApprovalAsync(
        string id, HttpRequest request, ILocalProfileStore profiles, IDocumentCatalogStore store,
        CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var row = await store.GetApprovalAsync(profile.TenantId, id, token);
        return row is null ? NotFound("approval") : Results.Ok(ToContract(row));
    }

    private static async Task<IResult> CreateApprovalAsync(
        CreateApprovalRequest input, HttpRequest request, ILocalProfileStore profiles,
        IDocumentStore authority, IDocumentCatalogStore store, IClock clock, CancellationToken token)
    {
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        try
        {
            var value = DocumentApiApplicationService.Approval(input);
            if (!Valid(input.ProjectId) || !Valid(input.RequestedByAgentId) ||
                new[] { input.DocumentId, input.GateId, input.TaskId }
                    .Any(value => value is not null && !Valid(value)))
                return Problem(400, "invalid_approval", "Approval references must be ULIDs.");
            var now = clock.UtcNow; var approvalId = UlidValue.New(now).ToString();
            ApprovalCatalogRecord row;
            if (input.DocumentId is not null)
            {
                var document = await authority.ReadAsync(profile.TenantId, input.DocumentId, token);
                if (document is null) return NotFound("document");
                if (document.ProjectId != input.ProjectId)
                    return Problem(400, "invalid_approval", "Document does not belong to the project.");
                var receipt = await authority.RequestApprovalAsync(new(profile.TenantId, input.DocumentId,
                    approvalId, UlidValue.New(now.AddTicks(1)).ToString(), value.Title, value.Description,
                    value.Priority, input.DueAt, input.RequestedByAgentId, document.Version,
                    $"api:approval:{approvalId}", now), token);
                if (receipt.Status is not (DocumentMutationStatus.Applied or DocumentMutationStatus.IdempotentReplay))
                    return MutationProblem(receipt.Status);
                row = await store.GetApprovalAsync(profile.TenantId, approvalId, token)
                    ?? throw new InvalidOperationException("Created approval was not readable.");
            }
            else
            {
                row = await store.CreateGeneralApprovalAsync(new(profile.TenantId, approvalId,
                    input.ProjectId, input.GateId, input.TaskId, value.Title, value.Description,
                    value.Priority, input.DueAt, input.RequestedByAgentId, now), token);
            }
            return Results.Created($"/api/v1/approvals/{approvalId}", ToContract(row));
        }
        catch (ApprovalReferenceNotFoundException exception) { return NotFound(exception.Reference); }
        catch (ArgumentException exception) { return Problem(400, "invalid_approval", exception.Message); }
    }

    private static async Task<IResult> ResolveApprovalAsync(
        string id, ResolveApprovalRequest input, HttpRequest request, ILocalProfileStore profiles,
        IDocumentStore authority, IDocumentCatalogStore store, IClock clock, CancellationToken token)
    {
        if (!Valid(id)) return InvalidId();
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        var approval = await store.GetApprovalAsync(profile.TenantId, id, token); if (approval is null) return NotFound("approval");
        try
        {
            var value = DocumentApiApplicationService.Resolution(input);
            var now = clock.UtcNow; ApprovalCatalogRecord? row;
            if (approval.DocumentId is not null)
            {
                var document = await authority.ReadAsync(profile.TenantId, approval.DocumentId, token);
                if (document is null) return NotFound("document");
                var receipt = await authority.ResolveApprovalAsync(new(
                    profile.TenantId, document.DocumentId, id, UlidValue.New(now).ToString(),
                    value.Decision, profile.Id, value.Note, document.Version,
                    $"api:approval-resolution:{UlidValue.New(now.AddTicks(1))}", now), token);
                if (receipt.Status is not (DocumentMutationStatus.Applied or DocumentMutationStatus.IdempotentReplay))
                    return MutationProblem(receipt.Status);
                row = await store.GetApprovalAsync(profile.TenantId, id, token);
            }
            else
            {
                row = await store.ResolveGeneralApprovalAsync(new(profile.TenantId, id,
                    value.Decision, profile.Id, value.Note, now), token);
            }
            var resolved = row ?? throw new InvalidOperationException("Resolved approval was not readable.");
            return Results.Ok(ToContract(resolved));
        }
        catch (ArgumentException exception) { return Problem(400, "invalid_approval_resolution", exception.Message); }
        catch (InvalidOperationException exception) { return Problem(409, "approval_resolution_conflict", exception.Message); }
    }

    private static DocumentContract ToContract(DocumentCatalogRecord value) => new(
        value.Id, value.ProjectId, value.Title, value.Kind,
        DocumentApiApplicationService.ToApiState(value.State), value.CurrentVersion,
        value.Classifications, value.PhaseName, value.Inconsistent, null,
        value.CreatedAt, value.UpdatedAt);

    private static async Task<DocumentVersionContract> ToContractAsync(
        DocumentVersionCatalogRecord value, IDocumentContentCatalog content, CancellationToken token) =>
        new(value.Id, value.DocumentId, value.Version,
            await content.ReadAsync(value.CatalogPath, value.ContentHash, token), value.AuthorKind,
            value.AuthorId, value.CreatedAt);
    private static ApprovalContract ToContract(ApprovalCatalogRecord value) => new(
        value.Id, value.ProjectId, value.GateId, value.TaskId, value.DocumentId, value.Title,
        value.Description, value.Priority, value.DueAt, value.State, value.RequestedByAgentId,
        value.RequestedAt, value.ResolvedByProfileId, value.ResolvedAt, value.ResolutionNote);

    private static IResult? Page(string? cursor, int? limit, params string?[] filters) =>
        ((cursor is not null && !Valid(cursor)) || limit is < 1 or > 200 ||
         filters.Any(value => value is not null && !Valid(value)))
            ? Problem(400, "invalid_cursor", "Filter, cursor, or limit is invalid.") : null;
    private static IResult MutationProblem(DocumentMutationStatus status) => status switch
    {
        DocumentMutationStatus.NotFound => NotFound("document"),
        _ => Problem(409, "document_mutation_conflict", $"Document command was rejected: {status}.")
    };
    private static Task<LocalProfileRecord?> Session(HttpRequest request, ILocalProfileStore profiles,
        CancellationToken token) => LocalProfileSession.ResolveAsync(request, profiles, token);
    private static bool Valid(string id) => UlidValue.TryParse(id, out _);
    private static IResult InvalidId() => Problem(400, "invalid_id", "ID must be a ULID.");
    private static IResult Unauthorized() => Problem(401, "local_session_required", "A local profile session is required.");
    private static IResult NotFound(string resource) => Problem(404, $"{resource}_not_found", "The resource does not exist.");
    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record DocumentPage(IReadOnlyList<DocumentContract> Items, string? NextCursor);
public sealed record DocumentVersionPage(IReadOnlyList<DocumentVersionContract> Items, string? NextCursor);
public sealed record ApprovalPage(IReadOnlyList<ApprovalContract> Items, string? NextCursor);
