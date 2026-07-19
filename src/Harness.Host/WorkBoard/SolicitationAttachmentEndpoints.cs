using System.Security.Cryptography;
using Harness.Host.Profiles;
using Harness.Modules.Coordination.Domain;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.SharedKernel.Identifiers;
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
        var relative = Path.Combine(tenantId, attachmentId);
        var absolute = Path.GetFullPath(Path.Combine(_rootPath, relative));
        if (!absolute.StartsWith($"{_rootPath}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Attachment storage refused a path outside its root.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, content.ToArray(), cancellationToken);
        return relative;
    }

    public string Resolve(string relativePath) => Path.GetFullPath(Path.Combine(_rootPath, relativePath));
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

        if (await board.GetSolicitationAsync(profile.TenantId, solicitationId, token) is null)
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

        var occurredAt = clock.UtcNow;
        var attachmentId = UlidValue.New(occurredAt).ToString();
        var sha256 = Convert.ToHexString(SHA256.HashData(content.Span));
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

        await audit.AppendAsync(
            new AuditEventAppendCommand(
                profile.TenantId,
                "user",
                profile.Id,
                "solicitation.attachmentAccepted",
                "solicitation",
                solicitationId,
                $"{file.FileName} ({content.Length} bytes, sha256 {sha256})",
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
