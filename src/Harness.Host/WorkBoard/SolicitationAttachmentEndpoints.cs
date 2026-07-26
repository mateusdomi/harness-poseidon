using Harness.Host.Profiles;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Coordination.Domain;
using Harness.Modules.Governance.Memory;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Memory;
using Harness.SharedKernel.Security;
using Harness.SharedKernel.Time;
using Microsoft.Data.Sqlite;

namespace Harness.Host.WorkBoard;

public sealed class SolicitationAttachmentStorage(string rootPath)
{
    private readonly string _rootPath = Path.GetFullPath(rootPath);

    public async Task<string> SaveAsync(
        string tenantId,
        string attachmentId,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var tenantSegment = SafeUlidSegment(tenantId, nameof(tenantId));
        var attachmentSegment = SafeUlidSegment(attachmentId, nameof(attachmentId));
        var relative = Path.Combine(tenantSegment, attachmentSegment);
        var absolute = Path.GetFullPath(Path.Combine(_rootPath, relative));
        EnsureConfined(absolute);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, content.ToArray(), cancellationToken);
        return relative;
    }

    public string Resolve(string relativePath)
    {
        var absolute = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        EnsureConfined(absolute);
        return absolute;
    }

    private static string SafeUlidSegment(string value, string parameterName)
    {
        var segment = Path.GetFileName(value);
        if (!UlidValue.TryParse(value, out _) || !string.Equals(segment, value, StringComparison.Ordinal))
            throw new ArgumentException("Attachment path segment must be a ULID.", parameterName);
        return segment;
    }

    private void EnsureConfined(string absolute)
    {
        if (!absolute.StartsWith($"{_rootPath}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException("Attachment storage refused a path outside its root.");
    }
}

public static class SolicitationAttachmentEndpoints
{
    public static IEndpointRouteBuilder MapSolicitationAttachments(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/solicitations/{solicitationId}/attachments")
            .WithTags("po-assistant");
        group.MapGet("/", ListAsync)
            .Produces<SolicitationAttachmentPage>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        group.MapPost("/", UploadAsync)
            .Produces<SolicitationAttachmentContract>(201)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string solicitationId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IWorkBoardStore board,
        ISolicitationAttachmentStore store,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(solicitationId, out _))
        {
            return Problem(400, "invalid_solicitation_id", "Solicitation ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        if (await board.GetSolicitationAsync(profile.TenantId, solicitationId, token) is null)
        {
            return Problem(404, "solicitation_not_found", "The requested resource does not exist.");
        }

        var items = await store.ListAsync(profile.TenantId, solicitationId, token);
        return Results.Ok(new SolicitationAttachmentPage(
            items.Select(ToContract).ToArray(),
            null));
    }

    private static async Task<IResult> UploadAsync(
        string solicitationId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IWorkBoardStore board,
        ISolicitationAttachmentStore store,
        SolicitationAttachmentStorage storage,
        IMultimodalIntakeService intake,
        IVectorIndex vectors,
        IAuditEventStore audit,
        IClock clock,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(solicitationId, out _))
        {
            return Problem(400, "invalid_solicitation_id", "Solicitation ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        var solicitation = await board.GetSolicitationAsync(profile.TenantId, solicitationId, token);
        if (solicitation is null)
        {
            return Problem(404, "solicitation_not_found", "The requested resource does not exist.");
        }

        if (!request.HasFormContentType)
        {
            return Problem(400, "multipart_required", "The attachment upload requires multipart/form-data.");
        }

        var form = await request.ReadFormAsync(token);
        var file = form.Files.Count == 1 ? form.Files[0] : null;
        if (file is null)
        {
            return Problem(400, "single_file_required", "Exactly one attachment file is required per upload.");
        }

        if (file.Length > AttachmentIngestPolicy.MaximumSizeBytes)
        {
            return await RejectAsync(
                audit,
                profile.TenantId,
                solicitationId,
                file.FileName,
                new AttachmentIngestDecision(
                    false,
                    "size_exceeded",
                    $"O anexo excede o limite de {AttachmentIngestPolicy.MaximumSizeBytes} bytes."),
                clock,
                token);
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, token);
        var content = new ReadOnlyMemory<byte>(buffer.ToArray());
        var decision = AttachmentIngestPolicy.Evaluate(
            new AttachmentIngestRequest(file.FileName, file.ContentType, content));
        if (!decision.Accepted)
        {
            return await RejectAsync(
                audit, profile.TenantId, solicitationId, file.FileName, decision, clock, token);
        }

        // Fase 8 — intake multimodal: o anexo aceito pela política de segurança passa pelo
        // serviço de intake, que produz o checksum canônico, o scanner de mime declarado e o
        // preview estruturado que torna o insumo rastreável até a demanda. Um mime declarado
        // fora da allowlist é rejeição auditada — defesa em profundidade sobre o gate de
        // extensão (um `.md` declarado como executável não passa).
        var processed = await intake.ProcessAttachmentAsync(
            profile.TenantId,
            solicitationId,
            file.FileName,
            file.ContentType,
            content.ToArray(),
            AttachmentIngestPolicy.MaximumSizeBytes,
            token);
        if (!processed.IsAllowedType)
        {
            return await RejectAsync(
                audit,
                profile.TenantId,
                solicitationId,
                file.FileName,
                new AttachmentIngestDecision(
                    false,
                    "mime_not_allowed",
                    $"O content type declarado '{file.ContentType}' não pertence à allowlist de intake."),
                clock,
                token);
        }

        var occurredAt = clock.UtcNow;
        var attachmentId = UlidValue.New(occurredAt).ToString();
        var sha256 = processed.Sha256Hash.ToUpperInvariant();
        var storagePath = await storage.SaveAsync(profile.TenantId, attachmentId, content, token);
        SolicitationAttachmentRecord record;
        try
        {
            record = await store.CreateAsync(
                new SolicitationAttachmentCreateCommand(
                    profile.TenantId,
                    attachmentId,
                    solicitationId,
                    file.FileName,
                    file.ContentType,
                    content.Length,
                    sha256,
                    storagePath,
                    occurredAt),
                token);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            File.Delete(storage.Resolve(storagePath));
            return Problem(
                409,
                "attachment_duplicate",
                "An identical attachment already exists for this solicitation.");
        }

        // Fase 6 — memória semântica alimentada pelo fluxo REAL: o preview redigido do anexo
        // aceito entra no índice vetorial derivado (embedding local determinístico), com
        // proveniência completa nos metadados. O índice NUNCA é fonte da verdade — o registro
        // durável acima é — e pode ser reconstruído a qualquer momento.
        var memoryContent = SecretTextProtector.Redact(
            $"{file.FileName}: {processed.PreviewSnippet}");
        await vectors.IndexAsync(
            new VectorDocumentRecord(
                attachmentId,
                profile.TenantId,
                solicitation.ProjectId,
                "solicitation_attachment",
                memoryContent,
                DeterministicLocalEmbedding.Embed(memoryContent),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["solicitationId"] = solicitationId,
                    ["fileName"] = file.FileName,
                    ["sha256"] = sha256,
                    ["contentType"] = file.ContentType,
                },
                occurredAt),
            token);

        // O preview do intake entra no ledger REDIGIDO: um anexo de texto pode carregar
        // segredo, e o ledger é append-only — o que entra, fica.
        await audit.AppendAsync(
            new AuditEventAppendCommand(
                profile.TenantId,
                "user",
                profile.Id,
                "solicitation.attachmentAccepted",
                "solicitation",
                solicitationId,
                SecretTextProtector.Redact(
                    $"{file.FileName} ({content.Length} bytes, sha256 {sha256}); " +
                    $"scan={processed.SecurityScanStatus}; preview={processed.PreviewSnippet}"),
                occurredAt),
            token);
        return Results.Created(
            $"/api/v1/solicitations/{solicitationId}/attachments/{record.Id}",
            ToContract(record));
    }

    private static async Task<IResult> RejectAsync(
        IAuditEventStore audit,
        string tenantId,
        string solicitationId,
        string fileName,
        AttachmentIngestDecision decision,
        IClock clock,
        CancellationToken token)
    {
        await audit.AppendAsync(
            new AuditEventAppendCommand(
                tenantId,
                "user",
                null,
                "solicitation.attachmentRejected",
                "solicitation",
                solicitationId,
                $"{decision.Code}: {Sanitize(fileName)} — {decision.Detail}",
                clock.UtcNow),
            token);
        return Problem(400, decision.Code, decision.Detail);
    }

    private static string Sanitize(string fileName)
    {
        var printable = new string(fileName.Where(character => !char.IsControl(character)).ToArray());
        return printable.Length > 120 ? printable[..120] : printable;
    }

    private static SolicitationAttachmentContract ToContract(SolicitationAttachmentRecord value) => new(
        value.Id,
        value.SolicitationId,
        value.FileName,
        value.ContentType,
        value.SizeBytes,
        value.Sha256,
        value.State,
        value.CreatedAt);

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record SolicitationAttachmentContract(
    string Id,
    string SolicitationId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string State,
    DateTimeOffset CreatedAt);

public sealed record SolicitationAttachmentPage(
    IReadOnlyList<SolicitationAttachmentContract> Items,
    string? NextCursor);
