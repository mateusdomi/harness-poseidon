using System.Security.Cryptography;
using Harness.Host.Profiles;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Domain;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Prototyping;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;
using Microsoft.Data.Sqlite;

namespace Harness.Host.Prototyping;

public static class VisualReferenceAssetEndpoints
{
    private static readonly HashSet<string> AllowedExtensions =
        new([".png", ".jpg", ".jpeg", ".zip"], StringComparer.OrdinalIgnoreCase);

    public static IEndpointRouteBuilder MapVisualReferenceAssets(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/visual-references/{referenceId}/assets")
            .WithTags("prototyping");
        group.MapGet("/", ListAsync)
            .Produces<VisualReferenceAssetPage>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        group.MapPost("/", UploadAsync)
            .Produces<VisualReferenceAssetContract>(201)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404)
            .ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string referenceId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IPrototypeStore prototypes,
        IVisualReferenceAssetStore store,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(referenceId, out _))
        {
            return Problem(400, "invalid_reference_id", "Reference ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        if (await prototypes.GetReferenceAsync(profile.TenantId, referenceId, token) is null)
        {
            return Problem(404, "visual_reference_not_found", "The requested resource does not exist.");
        }

        var items = await store.ListAsync(profile.TenantId, referenceId, token);
        return Results.Ok(new VisualReferenceAssetPage(items.Select(ToContract).ToArray(), null));
    }

    private static async Task<IResult> UploadAsync(
        string referenceId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IPrototypeStore prototypes,
        IVisualReferenceAssetStore store,
        SolicitationAttachmentStorage storage,
        IAuditEventStore audit,
        IClock clock,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(referenceId, out _))
        {
            return Problem(400, "invalid_reference_id", "Reference ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        if (await prototypes.GetReferenceAsync(profile.TenantId, referenceId, token) is null)
        {
            return Problem(404, "visual_reference_not_found", "The requested resource does not exist.");
        }

        if (!request.HasFormContentType)
        {
            return Problem(400, "multipart_required", "The asset upload requires multipart/form-data.");
        }

        var form = await request.ReadFormAsync(token);
        var file = form.Files.Count == 1 ? form.Files[0] : null;
        if (file is null)
        {
            return Problem(400, "single_file_required", "Exactly one asset file is required per upload.");
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, token);
        var content = new ReadOnlyMemory<byte>(buffer.ToArray());
        var decision = AttachmentIngestPolicy.Evaluate(
            new AttachmentIngestRequest(file.FileName, file.ContentType, content));
        if (decision.Accepted && !AllowedExtensions.Contains(Path.GetExtension(file.FileName)))
        {
            decision = AttachmentIngestDecision.Deny(
                "unsupported_type",
                "Referências visuais aceitam somente imagens PNG/JPEG ou ZIP de referências.");
        }

        var occurredAt = clock.UtcNow;
        if (!decision.Accepted)
        {
            await audit.AppendAsync(
                new AuditEventAppendCommand(
                    profile.TenantId,
                    "user",
                    profile.Id,
                    "prototype.assetRejected",
                    "visual_reference",
                    referenceId,
                    $"{decision.Code}: {decision.Detail}",
                    occurredAt),
                token);
            return Problem(400, decision.Code, decision.Detail);
        }

        var assetId = UlidValue.New(occurredAt).ToString();
        var sha256 = Convert.ToHexString(SHA256.HashData(content.Span));
        var storagePath = await storage.SaveAsync(
            profile.TenantId,
            assetId,
            content,
            token);
        VisualReferenceAssetRecord record;
        try
        {
            record = await store.CreateAsync(
                new VisualReferenceAssetCreateCommand(
                    profile.TenantId,
                    assetId,
                    referenceId,
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
                "asset_duplicate",
                "An identical asset already exists for this reference.");
        }

        await audit.AppendAsync(
            new AuditEventAppendCommand(
                profile.TenantId,
                "user",
                profile.Id,
                "prototype.assetAccepted",
                "visual_reference",
                referenceId,
                $"{file.FileName} ({content.Length} bytes, sha256 {sha256})",
                occurredAt),
            token);
        return Results.Created(
            $"/api/v1/visual-references/{referenceId}/assets/{record.Id}",
            ToContract(record));
    }

    private static VisualReferenceAssetContract ToContract(VisualReferenceAssetRecord value) => new(
        value.Id,
        value.ReferenceId,
        value.FileName,
        value.ContentType,
        value.SizeBytes,
        value.Sha256,
        value.CreatedAt);

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record VisualReferenceAssetContract(
    string Id,
    string ReferenceId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CreatedAt);

public sealed record VisualReferenceAssetPage(
    IReadOnlyList<VisualReferenceAssetContract> Items,
    string? NextCursor);
