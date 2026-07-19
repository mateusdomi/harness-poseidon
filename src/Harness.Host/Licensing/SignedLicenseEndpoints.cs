using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Host.Profiles;
using Harness.Modules.Licensing.Domain;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Licensing;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.Configuration;

namespace Harness.Host.Licensing;

public static class SignedLicenseEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSignedLicenses(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/licenses/signed").WithTags("licensing");
        group.MapGet("/", GetAsync)
            .Produces<SignedLicenseContract>()
            .ProducesProblem(401)
            .ProducesProblem(404);
        group.MapPost("/activation", ActivateAsync)
            .Produces<SignedLicenseContract>(201)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(409);
        group.MapPost("/revocations", ImportRevocationsAsync)
            .Produces<int>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpRequest request,
        ILocalProfileStore profiles,
        ISignedLicenseStore store,
        IClock clock,
        CancellationToken token)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        var record = await store.GetCurrentAsync(profile.TenantId, token);
        if (record is null)
        {
            return Problem(404, "signed_license_not_found", "No signed license is activated.");
        }

        var revoked = await store.IsRevokedAsync(profile.TenantId, record.LicenseId, token);
        return Results.Ok(ToContract(record, revoked, clock.UtcNow));
    }

    private static async Task<IResult> ActivateAsync(
        SignedLicenseActivationRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        ISignedLicenseStore store,
        IAuditEventStore audit,
        IConfiguration configuration,
        IClock clock,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        var publicKey = configuration["Harness:Licensing:PublicKey"];
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            return Problem(
                409,
                "licensing_public_key_missing",
                "A chave pública de licenciamento não está configurada nesta instalação.");
        }

        var document = new LicenseDocument(
            input.Document.LicenseId,
            input.Document.Product,
            input.Document.Licensee,
            input.Document.DeviceFingerprint,
            input.Document.IssuedAt,
            input.Document.ExpiresAt,
            input.Document.GraceDays,
            input.Document.Entitlements,
            input.Document.Version);
        try
        {
            LicenseDocumentValidator.Validate(document);
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "license_document_invalid", exception.Message);
        }

        var occurredAt = clock.UtcNow;
        if (!LicenseSigning.Verify(document, input.Signature, publicKey))
        {
            await audit.AppendAsync(
                new AuditEventAppendCommand(
                    profile.TenantId,
                    "user",
                    profile.Id,
                    "license.signedActivationRejected",
                    "license",
                    document.LicenseId,
                    "Assinatura Ed25519 inválida para o documento apresentado.",
                    occurredAt),
                token);
            return Problem(
                400,
                "license_signature_invalid",
                "A assinatura do documento de licença é inválida.");
        }

        if (await store.IsRevokedAsync(profile.TenantId, document.LicenseId, token))
        {
            return Problem(400, "license_revoked", "A licença apresentada foi revogada.");
        }

        var record = await store.SaveAsync(
            new SignedLicenseSaveCommand(
                profile.TenantId,
                document.LicenseId,
                document.Licensee,
                document.DeviceFingerprint,
                document.IssuedAt,
                document.ExpiresAt,
                document.GraceDays,
                document.Entitlements,
                JsonSerializer.Serialize(document, JsonOptions),
                input.Signature,
                occurredAt),
            token);
        await audit.AppendAsync(
            new AuditEventAppendCommand(
                profile.TenantId,
                "user",
                profile.Id,
                "license.signedActivated",
                "license",
                document.LicenseId,
                $"Licença assinada de {document.Licensee} ativada offline.",
                occurredAt),
            token);
        return Results.Created(
            "/api/v1/licenses/signed",
            ToContract(record, revoked: false, occurredAt));
    }

    private static async Task<IResult> ImportRevocationsAsync(
        SignedRevocationImportRequest input,
        HttpRequest request,
        ILocalProfileStore profiles,
        ISignedLicenseStore store,
        IAuditEventStore audit,
        IConfiguration configuration,
        IClock clock,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, token);
        if (profile is null)
        {
            return Problem(401, "local_session_required", "A local profile session is required.");
        }

        var publicKey = configuration["Harness:Licensing:PublicKey"];
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            return Problem(
                409,
                "licensing_public_key_missing",
                "A chave pública de licenciamento não está configurada nesta instalação.");
        }

        var list = new LicenseRevocationList(input.List.RevokedLicenseIds, input.List.IssuedAt);
        if (list.RevokedLicenseIds is not { Count: > 0 } ||
            list.RevokedLicenseIds.Any(id => id.Length != 26))
        {
            return Problem(
                400,
                "revocation_list_invalid",
                "A lista de revogação exige IDs de licença ULID.");
        }

        if (!LicenseSigning.Verify(list, input.Signature, publicKey))
        {
            return Problem(
                400,
                "revocation_signature_invalid",
                "A assinatura da lista de revogação é inválida.");
        }

        var occurredAt = clock.UtcNow;
        var added = await store.AddRevocationsAsync(
            profile.TenantId,
            list.RevokedLicenseIds,
            occurredAt,
            token);
        await audit.AppendAsync(
            new AuditEventAppendCommand(
                profile.TenantId,
                "user",
                profile.Id,
                "license.revocationsImported",
                "license",
                null,
                $"{added} revogação(ões) nova(s) importada(s) de lista assinada.",
                occurredAt),
            token);
        return Results.Ok(added);
    }

    private static SignedLicenseContract ToContract(
        SignedLicenseRecord record,
        bool revoked,
        DateTimeOffset now)
    {
        var document = new LicenseDocument(
            record.LicenseId,
            LicenseDocumentValidator.ExpectedProduct,
            record.Licensee,
            record.DeviceFingerprint,
            record.IssuedAt,
            record.ExpiresAt,
            record.GraceDays,
            record.Entitlements,
            1);
        var state = LicenseDocumentValidator.DeriveState(document, revoked, now) switch
        {
            SignedLicenseState.Active => "active",
            SignedLicenseState.GracePeriod => "gracePeriod",
            SignedLicenseState.Expired => "expired",
            SignedLicenseState.Revoked => "revoked",
            _ => "expired",
        };
        return new SignedLicenseContract(
            record.LicenseId,
            record.Licensee,
            record.DeviceFingerprint,
            state,
            record.IssuedAt,
            record.ExpiresAt,
            record.GraceDays,
            record.Entitlements,
            record.ActivatedAt);
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SignedLicenseActivationRequest(
    LicenseDocumentPayload Document,
    string Signature);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LicenseDocumentPayload(
    string LicenseId,
    string Product,
    string Licensee,
    string? DeviceFingerprint,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int GraceDays,
    IReadOnlyList<string> Entitlements,
    int Version);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SignedRevocationImportRequest(
    RevocationListPayload List,
    string Signature);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RevocationListPayload(
    IReadOnlyList<string> RevokedLicenseIds,
    DateTimeOffset IssuedAt);

public sealed record SignedLicenseContract(
    string LicenseId,
    string Licensee,
    string? DeviceFingerprint,
    string State,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int GraceDays,
    IReadOnlyList<string> Entitlements,
    DateTimeOffset ActivatedAt);
