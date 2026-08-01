using Harness.Host.Profiles;
using Harness.Modules.Documents.Application;
using Harness.Modules.Documents.Contracts;
using Harness.Persistence.Abstractions.Documents;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Documents;

public static class DocumentEndpoints
{
    private static readonly HashSet<string> DocumentKinds = new(
        ["prd", "spec", "design", "runbook", "note", "report"], StringComparer.Ordinal);
    private static readonly HashSet<string> ApprovalStates = new(
        ["pending", "approved", "rejected", "cancelled"], StringComparer.Ordinal);
    private static readonly HashSet<string> Priorities = new(
        ["low", "medium", "high", "critical"], StringComparer.Ordinal);
    private static readonly HashSet<string> DueFilters = new(
        ["all", "overdue", "week", "none"], StringComparer.Ordinal);

    public static IEndpointRouteBuilder MapDocumentCatalog(this IEndpointRouteBuilder endpoints)
    {
        var documents = endpoints.MapGroup("/api/v1/documents").WithTags("documents");
        documents.MapGet("/", ListDocumentsAsync).Produces<DocumentPage>().ProducesProblem(400).ProducesProblem(401);
        documents.MapGet("/{id}", GetDocumentAsync).Produces<DocumentContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        documents.MapPost("/", CreateDocumentAsync).Produces<DocumentContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        documents.MapPost("/{id}/classification", ClassifyDocumentAsync).Produces<DocumentContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        documents.MapPost("/{id}/transitions", TransitionDocumentAsync).Produces<DocumentContract>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);
        documents.MapPost("/{id}/versions", SaveDocumentVersionAsync).Produces<DocumentVersionContract>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409);

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
        string? projectId, string? q, string? kind, string? state, string? phaseName,
        string? classification, bool? orphan, bool? inconsistent, int? page, int? pageSize,
        string? cursor, int? limit, HttpRequest request,
        ILocalProfileStore profiles, IDocumentCatalogStore store, CancellationToken token)
    {
        var invalid = DocumentPageValidation(projectId, q, kind, state, phaseName, classification,
            orphan, inconsistent, page, pageSize, cursor, limit);
        if (invalid is not null) return invalid;
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        if (cursor is not null || limit is not null)
        {
            var size = limit ?? 100;
            var rows = await store.ListDocumentsAsync(
                profile.TenantId, projectId, cursor, size + 1, token);
            var more = rows.Count > size; var selected = rows.Take(size).ToArray();
            return Results.Ok(new DocumentPage(selected.Select(ToContract).ToArray(),
                more ? selected[^1].Id : null, selected.Length, 1, size));
        }

        string? storeState = null;
        try { if (state is not null) storeState = DocumentApiApplicationService.ToStoreState(state); }
        catch (ArgumentException exception)
        { return Problem(400, "invalid_document_page", exception.Message); }
        var requestedPage = page ?? 1; var requestedSize = pageSize ?? 15;
        var search = string.IsNullOrWhiteSpace(q)
            ? null
            : $"%{EscapeLike(q.Trim().ToLowerInvariant())}%";
        var result = await store.PageDocumentsAsync(profile.TenantId, new(
            projectId, search, kind, storeState, phaseName, classification, orphan == true,
            inconsistent, checked((requestedPage - 1) * requestedSize), requestedSize), token);
        return Results.Ok(new DocumentPage(result.Items.Select(ToContract).ToArray(), null,
            result.Total, requestedPage, requestedSize));
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
        IDocumentContentCatalog content, IWorkflowDocumentTemplateStore templates,
        IClock clock, CancellationToken token)
    {
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        try
        {
            if (!Valid(input.ProjectId)) return Problem(400, "invalid_project", "Project ID must be a ULID.");
            if (await projects.GetAsync(profile.TenantId, input.ProjectId, token) is null) return NotFound("project");

            // Fase 2A.1: quando o documento declara realizar um template do playbook, a ESTRUTURA
            // é verificada aqui — antes de qualquer escrita. Os campos obrigatórios existiam desde
            // a 0082 e ninguém os lia: uma GMUD sem janela e sem rollback entrava no acervo como se
            // estivesse pronta. O que se verifica é a estrutura e a ordem; a prosa é livre.
            if (!string.IsNullOrWhiteSpace(input.TemplateCode))
            {
                var template = (await templates.ListAsync(token)).FirstOrDefault(item =>
                    string.Equals(item.Code, input.TemplateCode, StringComparison.OrdinalIgnoreCase));
                if (template is null)
                {
                    return Problem(400, "unknown_template",
                        $"O template '{input.TemplateCode}' não existe no catálogo do playbook.");
                }

                var compliance = DocumentTemplateCompliance.Check(input.Body, template.RequiredFieldsJson);
                if (!compliance.IsCompliant)
                {
                    return Problem(400, "template_not_satisfied",
                        $"O documento não cumpre o template '{template.Code} — {template.Name}': {compliance.Describe()}.");
                }
            }
            var normalized = DocumentApiApplicationService.Normalize(input); var now = clock.UtcNow;
            var documentId = UlidValue.New(now).ToString(); var versionId = UlidValue.New(now.AddTicks(1)).ToString();
            var prepared = DocumentApiApplicationService.Prepare(profile.TenantId, documentId, versionId, input.Body);
            await content.WriteAsync(prepared.CatalogPath, prepared.Body, prepared.ContentHash, token);
            try
            {
                // Criação interativa por um humano na própria sessão ('user') é, ela própria, um
                // fluxo legítimo — a origem programática é dispensada aqui. O guardrail anti-
                // proliferação que EXIGE origem para criação de 'chief'/'agent' vive no
                // DocumentCreateValidator, o ponto de estrangulamento comum a todos os stores.
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
        string? documentId, int? page, int? pageSize, string? cursor, int? limit, HttpRequest request,
        ILocalProfileStore profiles, IDocumentCatalogStore store, IDocumentContentCatalog content,
        CancellationToken token)
    {
        var invalid = StandardPageValidation(documentId, page, pageSize, cursor, limit, "version");
        if (invalid is not null) return invalid;
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        if (cursor is not null || limit is not null)
        {
            var size = limit ?? 100;
            var rows = await store.ListVersionsAsync(profile.TenantId, documentId, cursor, size + 1, token);
            var more = rows.Count > size; var selected = rows.Take(size).ToArray();
            var legacyContracts = new List<DocumentVersionContract>();
            foreach (var row in selected) legacyContracts.Add(await ToContractAsync(row, content, token));
            return Results.Ok(new DocumentVersionPage(legacyContracts,
                more ? selected[^1].Id : null, selected.Length, 1, size));
        }
        var requestedPage = page ?? 1; var requestedSize = pageSize ?? 15;
        var result = await store.PageVersionsAsync(profile.TenantId, documentId,
            checked((requestedPage - 1) * requestedSize), requestedSize, token);
        var contracts = new List<DocumentVersionContract>();
        foreach (var row in result.Items) contracts.Add(await ToContractAsync(row, content, token));
        return Results.Ok(new DocumentVersionPage(
            contracts, null, result.Total, requestedPage, requestedSize));
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
        => await CreateVersionCoreAsync(input.DocumentId, input.Body, request, profiles, authority,
            store, content, clock, token);

    private static async Task<IResult> SaveDocumentVersionAsync(
        string id, SaveDocumentVersionRequest input, HttpRequest request,
        ILocalProfileStore profiles, IDocumentStore authority, IDocumentCatalogStore store,
        IDocumentContentCatalog content, IClock clock, CancellationToken token)
        => await CreateVersionCoreAsync(id, input.Body, request, profiles, authority, store,
            content, clock, token);

    private static async Task<IResult> CreateVersionCoreAsync(
        string documentId, string body, HttpRequest request, ILocalProfileStore profiles,
        IDocumentStore authority, IDocumentCatalogStore store, IDocumentContentCatalog content,
        IClock clock, CancellationToken token)
    {
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        if (!Valid(documentId)) return Problem(400, "invalid_document", "Document ID must be a ULID.");
        var snapshot = await authority.ReadAsync(profile.TenantId, documentId, token);
        if (snapshot is null) return NotFound("document");
        try
        {
            var now = clock.UtcNow; var versionId = UlidValue.New(now).ToString();
            var prepared = DocumentApiApplicationService.Prepare(profile.TenantId, documentId, versionId, body);
            await content.WriteAsync(prepared.CatalogPath, prepared.Body, prepared.ContentHash, token);
            DocumentMutationReceipt receipt;
            try
            {
                receipt = await authority.AppendVersionAsync(new(profile.TenantId, documentId,
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
        string? projectId, string? state, string? priority, string? due,
        int? page, int? pageSize, string? cursor, int? limit, HttpRequest request,
        ILocalProfileStore profiles, IDocumentCatalogStore store, IClock clock,
        CancellationToken token)
    {
        var invalid = ApprovalPageValidation(
            projectId, state, priority, due, page, pageSize, cursor, limit);
        if (invalid is not null) return invalid;
        var profile = await Session(request, profiles, token); if (profile is null) return Unauthorized();
        if (cursor is not null || limit is not null)
        {
            var size = limit ?? 100;
            var rows = await store.ListApprovalsAsync(
                profile.TenantId, projectId, cursor, size + 1, token);
            var more = rows.Count > size; var selected = rows.Take(size).ToArray();
            return Results.Ok(new ApprovalPage(selected.Select(ToContract).ToArray(),
                more ? selected[^1].Id : null, selected.Length, 1, size));
        }
        var requestedPage = page ?? 1; var requestedSize = pageSize ?? 15;
        var result = await store.PageApprovalsAsync(profile.TenantId, new(
            projectId, state, priority, due ?? "all", clock.UtcNow,
            checked((requestedPage - 1) * requestedSize), requestedSize), token);
        return Results.Ok(new ApprovalPage(result.Items.Select(ToContract).ToArray(), null,
            result.Total, requestedPage, requestedSize));
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
        IDocumentStore authority, IDocumentCatalogStore store, IWorkflowCatalogStore workflows,
        IWorkflowStore runs, IClock clock, CancellationToken token)
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

                // O DEGRAU HUMANO: aprovar o documento aqui é a decisão que o condutor de fase se
                // recusa a dar sozinho — e que até agora nenhuma tela dava. A aprovação já está
                // durável acima; se a esteira não tiver objetivo correspondente, ou se o objetivo
                // ainda não estiver conferido, nada é inventado e a decisão do dono não se perde.
                if (string.Equals(value.Decision, "approved", StringComparison.Ordinal))
                {
                    _ = await DocumentApprovalPhaseLink.ApproveAsync(
                        workflows, runs, clock, profile.TenantId, document.ProjectId,
                        document.DocumentId, document.Title, document.PhaseName, token);
                }

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
        value.RequestedAt, value.ResolvedByProfileId, value.ResolvedAt, value.ResolutionNote,
        null, null);

    private static IResult? Page(string? cursor, int? limit, params string?[] filters) =>
        ((cursor is not null && !Valid(cursor)) || limit is < 1 or > 200 ||
         filters.Any(value => value is not null && !Valid(value)))
            ? Problem(400, "invalid_cursor", "Filter, cursor, or limit is invalid.") : null;
    private static IResult? DocumentPageValidation(
        string? projectId, string? query, string? kind, string? state, string? phaseName,
        string? classification, bool? orphan, bool? inconsistent, int? page, int? pageSize,
        string? cursor, int? limit)
    {
        if (projectId is not null && !Valid(projectId) || (query?.Length ?? 0) > 200 ||
            kind is not null && !DocumentKinds.Contains(kind) || (phaseName?.Length ?? 0) > 200 ||
            (classification?.Length ?? 0) > 100 || orphan == true && phaseName is not null ||
            page is < 1 or > 100_000 || pageSize is < 1 or > 200)
            return Problem(400, "invalid_document_page", "Document filters or pagination bounds are invalid.");
        if (state is not null)
        {
            try { _ = DocumentApiApplicationService.ToStoreState(state); }
            catch (ArgumentException exception)
            { return Problem(400, "invalid_document_page", exception.Message); }
        }
        if (cursor is not null || limit is not null)
        {
            if (page is not null || pageSize is not null || query is not null || kind is not null ||
                state is not null || phaseName is not null || classification is not null ||
                orphan is not null || inconsistent is not null)
                return Problem(400, "mixed_document_pagination", "Cursor and page pagination cannot be combined.");
            return Page(cursor, limit, projectId);
        }
        return null;
    }
    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
    private static IResult? ApprovalPageValidation(
        string? projectId, string? state, string? priority, string? due,
        int? page, int? pageSize, string? cursor, int? limit)
    {
        if (projectId is not null && !Valid(projectId) ||
            state is not null && !ApprovalStates.Contains(state) ||
            priority is not null && !Priorities.Contains(priority) ||
            due is not null && !DueFilters.Contains(due) ||
            page is < 1 or > 100_000 || pageSize is < 1 or > 200)
            return Problem(400, "invalid_approval_page", "Approval filters or pagination bounds are invalid.");
        if (cursor is not null || limit is not null)
        {
            if (page is not null || pageSize is not null || state is not null ||
                priority is not null || due is not null)
                return Problem(400, "mixed_approval_pagination", "Cursor and page pagination cannot be combined.");
            return Page(cursor, limit, projectId);
        }
        return null;
    }
    private static IResult? StandardPageValidation(
        string? filterId, int? page, int? pageSize, string? cursor, int? limit, string resource)
    {
        if (filterId is not null && !Valid(filterId) || page is < 1 or > 100_000 ||
            pageSize is < 1 or > 200)
            return Problem(400, $"invalid_{resource}_page", "Filter or pagination bounds are invalid.");
        if (cursor is not null || limit is not null)
        {
            if (page is not null || pageSize is not null)
                return Problem(400, $"mixed_{resource}_pagination", "Cursor and page pagination cannot be combined.");
            return Page(cursor, limit, filterId);
        }
        return null;
    }
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

public sealed record DocumentPage(
    IReadOnlyList<DocumentContract> Items, string? NextCursor, int Total = 0,
    int Page = 1, int PageSize = 15);
public sealed record DocumentVersionPage(
    IReadOnlyList<DocumentVersionContract> Items, string? NextCursor, int Total = 0,
    int Page = 1, int PageSize = 15);
public sealed record ApprovalPage(
    IReadOnlyList<ApprovalContract> Items, string? NextCursor, int Total = 0,
    int Page = 1, int PageSize = 15);
