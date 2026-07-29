using System.IO.Compression;
using System.Security.Cryptography;
using Harness.Host.Profiles;
using Harness.Host.WorkBoard;
using Harness.Modules.Coordination.Application;
using Harness.Modules.Coordination.Domain;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Prototyping;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Prototyping;

/// <summary>
/// Recebe o ZIP React com a intenção explícita de torná-lo o design system do projeto.
/// Referências visuais comuns continuam aceitando seus próprios ZIPs sem esta validação.
/// </summary>
public static class DesignSystemBundleEndpoints
{
    public static IEndpointRouteBuilder MapDesignSystemBundles(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/projects/{id}/design-system-bundle", UploadAsync)
            .WithTags("prototyping")
            .Produces<DesignSystemBundleContract>(201)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);
        return endpoints;
    }

    private static async Task<IResult> UploadAsync(
        string id,
        HttpRequest request,
        ILocalProfileStore profiles,
        IProjectStore projects,
        IPrototypeStore prototypes,
        IVisualReferenceAssetStore assets,
        SolicitationAttachmentStorage storage,
        IMultimodalIntakeService intake,
        IAuditEventStore audit,
        IClock clock,
        CancellationToken token)
    {
        if (!UlidValue.TryParse(id, out _))
        {
            return Problem(400, "invalid_project_id", "Project ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        if (await projects.GetAsync(profile.TenantId, id, token) is null)
        {
            return Problem(404, "project_not_found", "The requested resource does not exist.");
        }

        if (!request.HasFormContentType)
        {
            return Problem(
                400,
                "multipart_required",
                "Envie o pacote de telas como um arquivo ZIP.");
        }

        var form = await request.ReadFormAsync(token);
        var file = form.Files.Count == 1 ? form.Files[0] : null;
        if (file is null)
        {
            return Problem(
                400,
                "single_file_required",
                "Envie um único pacote ZIP por vez.");
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, token);
        var bytes = buffer.ToArray();
        var content = new ReadOnlyMemory<byte>(bytes);
        var ingest = AttachmentIngestPolicy.Evaluate(
            new AttachmentIngestRequest(file.FileName, file.ContentType, content));
        if (!ingest.Accepted)
        {
            await RecordRejectionAsync(
                audit, profile, id, ingest.Code, ingest.Detail, clock.UtcNow, token);
            return Problem(400, ingest.Code, ingest.Detail);
        }

        IReadOnlyList<string> entryNames;
        try
        {
            using var archive = new ZipArchive(
                new MemoryStream(bytes, writable: false),
                ZipArchiveMode.Read);
            entryNames = archive.Entries
                .Where(entry => !entry.FullName.EndsWith('/'))
                .Select(entry => entry.FullName)
                .ToArray();
        }
        catch (InvalidDataException)
        {
            const string detail = "Não consegui abrir esse pacote. Confira o ZIP e envie novamente.";
            await RecordRejectionAsync(
                audit, profile, id, "bundle_corrupted", detail, clock.UtcNow, token);
            return Problem(400, "bundle_corrupted", detail);
        }

        var validation = ReactBundleValidator.Validate(
            new IntakeAttachment(file.FileName, file.ContentType, file.Length),
            entryNames);
        if (!validation.IsValid)
        {
            await RecordRejectionAsync(
                audit,
                profile,
                id,
                $"bundle_{validation.Rejection.ToString().ToLowerInvariant()}",
                validation.BusinessMessage,
                clock.UtcNow,
                token);
            return Problem(
                400,
                $"bundle_{validation.Rejection.ToString().ToLowerInvariant()}",
                validation.BusinessMessage);
        }

        var scan = await intake.ProcessAttachmentAsync(
            profile.TenantId,
            id,
            file.FileName,
            file.ContentType,
            bytes,
            ReactBundleValidator.MaxSizeBytes,
            token);
        if (!scan.IsAllowedType)
        {
            const string detail = "Não reconheci esse arquivo como um pacote ZIP de telas.";
            await RecordRejectionAsync(
                audit, profile, id, "bundle_content_type", detail, clock.UtcNow, token);
            return Problem(400, "bundle_content_type", detail);
        }

        var occurredAt = clock.UtcNow;
        var referenceId = UlidValue.New(occurredAt).ToString();
        var assetId = UlidValue.New(occurredAt.AddMilliseconds(1)).ToString();
        var safeTitle = string.IsNullOrWhiteSpace(form["title"])
            ? Path.GetFileNameWithoutExtension(file.FileName)
            : form["title"].ToString().Trim();
        if (safeTitle.Length > 200)
        {
            return Problem(400, "invalid_bundle_title", "O nome do pacote deve ter até 200 caracteres.");
        }

        VisualReferenceRecord reference;
        try
        {
            reference = await prototypes.CreateReferenceAsync(
                new VisualReferenceCreateCommand(
                    profile.TenantId,
                    profile.Id,
                    referenceId,
                    id,
                    null,
                    safeTitle,
                    $"zip:{file.FileName}",
                    "upload",
                    [PrototypingStageEndpoints.DesignSystemTag],
                    occurredAt),
                token);
        }
        catch (PrototypeValidationException exception)
        {
            return Problem(400, "invalid_design_system_bundle", exception.Message);
        }

        var storagePath = await storage.SaveAsync(
            profile.TenantId,
            assetId,
            content,
            token);
        var sha256 = Convert.ToHexString(SHA256.HashData(content.Span));
        var asset = await assets.CreateAsync(
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

        await audit.AppendAsync(
            new AuditEventAppendCommand(
                profile.TenantId,
                "user",
                profile.Id,
                "prototype.designSystemBundleAccepted",
                "project",
                id,
                $"{file.FileName} ({content.Length} bytes, sha256 {sha256})",
                occurredAt),
            token);

        return Results.Created(
            $"/api/v1/visual-references/{reference.Id}",
            new DesignSystemBundleContract(
                reference.Id,
                asset.Id,
                id,
                reference.Title,
                asset.FileName,
                asset.ContentType,
                asset.SizeBytes,
                asset.Sha256,
                validation.BusinessMessage,
                reference.CreatedAt));
    }

    private static Task<AuditEventRecord> RecordRejectionAsync(
        IAuditEventStore audit,
        LocalProfileRecord profile,
        string projectId,
        string code,
        string detail,
        DateTimeOffset occurredAt,
        CancellationToken token) =>
        audit.AppendAsync(
            new AuditEventAppendCommand(
                profile.TenantId,
                "user",
                profile.Id,
                "prototype.designSystemBundleRejected",
                "project",
                projectId,
                $"{code}: {detail}",
                occurredAt),
            token);

    /// <summary>
    /// Um ZIP genérico do intake continua sendo um anexo normal. Quando ele é reconhecido como
    /// pacote React, esta promoção adicional o registra como design system sem duplicar os bytes
    /// já armazenados pelo intake.
    /// </summary>
    internal static async Task<bool> TryPromoteIntakeBundleAsync(
        LocalProfileRecord profile,
        string projectId,
        string fileName,
        string contentType,
        ReadOnlyMemory<byte> content,
        string storagePath,
        IPrototypeStore prototypes,
        IVisualReferenceAssetStore assets,
        IAuditEventStore audit,
        DateTimeOffset occurredAt,
        CancellationToken token)
    {
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        IReadOnlyList<string> entries;
        try
        {
            using var archive = new ZipArchive(
                new MemoryStream(content.ToArray(), writable: false),
                ZipArchiveMode.Read);
            entries = archive.Entries
                .Where(entry => !entry.FullName.EndsWith('/'))
                .Select(entry => entry.FullName)
                .ToArray();
        }
        catch (InvalidDataException)
        {
            return false;
        }

        var validation = ReactBundleValidator.Validate(
            new IntakeAttachment(fileName, contentType, content.Length),
            entries);
        if (!validation.IsValid)
        {
            return false;
        }

        var referenceId = UlidValue.New(occurredAt.AddMilliseconds(1)).ToString();
        var assetId = UlidValue.New(occurredAt.AddMilliseconds(2)).ToString();
        var reference = await prototypes.CreateReferenceAsync(
            new VisualReferenceCreateCommand(
                profile.TenantId,
                profile.Id,
                referenceId,
                projectId,
                null,
                Path.GetFileNameWithoutExtension(fileName),
                $"zip:{fileName}",
                "upload",
                [PrototypingStageEndpoints.DesignSystemTag],
                occurredAt),
            token);
        var sha256 = Convert.ToHexString(SHA256.HashData(content.Span));
        await assets.CreateAsync(
            new VisualReferenceAssetCreateCommand(
                profile.TenantId,
                assetId,
                reference.Id,
                fileName,
                contentType,
                content.Length,
                sha256,
                storagePath,
                occurredAt),
            token);
        await audit.AppendAsync(
            new AuditEventAppendCommand(
                profile.TenantId,
                "user",
                profile.Id,
                "prototype.designSystemBundlePromotedFromIntake",
                "project",
                projectId,
                $"{fileName} ({content.Length} bytes, sha256 {sha256})",
                occurredAt),
            token);
        return true;
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record DesignSystemBundleContract(
    string ReferenceId,
    string AssetId,
    string ProjectId,
    string Title,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string BusinessMessage,
    DateTimeOffset CreatedAt);
