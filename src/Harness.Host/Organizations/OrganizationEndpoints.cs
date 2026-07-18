using Harness.Host.Profiles;
using Harness.Modules.Organizations.Application;
using Harness.Modules.Organizations.Contracts;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Organizations;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Organizations;

public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizations(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/v1/organizations").WithTags("organizations");
        group.MapGet("/", ListAsync).Produces<OrganizationPage>().ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/{organizationId}", GetAsync)
            .Produces<OrganizationResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        group.MapPost("/", CreateAsync)
            .Produces<OrganizationResponse>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        group.MapPatch("/{organizationId}", PatchAsync)
            .Produces<OrganizationResponse>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(404).ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string? cursor,
        int? limit,
        HttpRequest request,
        ILocalProfileStore profiles,
        IOrganizationStore organizations,
        CancellationToken cancellationToken)
    {
        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null)
        {
            return SessionRequired();
        }

        var pageSize = limit ?? 50;
        if (pageSize is < 1 or > 200 ||
            (cursor is not null && !UlidValue.TryParse(cursor, out _)))
        {
            return Problem(400, "invalid_cursor", "Cursor or limit is invalid.");
        }

        var records = await organizations.ListAsync(
            profile.TenantId, cursor, pageSize + 1, cancellationToken);
        var hasMore = records.Count > pageSize;
        var items = records.Take(pageSize).Select(ToResponse).ToArray();
        return Results.Ok(new OrganizationPage(items, hasMore ? items[^1].Id : null));
    }

    private static async Task<IResult> GetAsync(
        string organizationId,
        HttpRequest request,
        ILocalProfileStore profiles,
        IOrganizationStore organizations,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(organizationId, out _))
        {
            return Problem(400, "invalid_organization_id", "Organization ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null)
        {
            return SessionRequired();
        }

        var organization = await organizations.GetAsync(
            profile.TenantId, organizationId, cancellationToken);
        return organization is null
            ? Problem(404, "organization_not_found", "The organization does not exist.")
            : Results.Ok(ToResponse(organization));
    }

    private static async Task<IResult> CreateAsync(
        CreateOrganizationRequest request,
        HttpRequest httpRequest,
        ILocalProfileStore profiles,
        IOrganizationStore organizations,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var profile = await LocalProfileSession.ResolveAsync(httpRequest, profiles, cancellationToken);
        if (profile is null)
        {
            return SessionRequired();
        }

        try
        {
            var occurredAt = clock.UtcNow;
            var organization = OrganizationApplicationService.Create(
                UlidValue.New(occurredAt).ToString(), request, occurredAt);
            var result = await organizations.CreateAsync(
                new OrganizationCreateCommand(
                    profile.TenantId,
                    organization.Id,
                    organization.Name,
                    organization.Slug,
                    organization.Plan,
                    ToRecord(organization.Brand),
                    occurredAt),
                cancellationToken);
            return result.Status switch
            {
                OrganizationMutationStatus.Applied => Results.Created(
                    $"/api/v1/organizations/{organization.Id}", ToResponse(result.Organization!)),
                OrganizationMutationStatus.AlreadyExists => Conflict(),
                _ => throw new InvalidOperationException(
                    $"Unexpected organization creation status: {result.Status}."),
            };
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "invalid_organization", exception.Message);
        }
    }

    private static async Task<IResult> PatchAsync(
        string organizationId,
        UpdateOrganizationRequest patch,
        HttpRequest request,
        ILocalProfileStore profiles,
        IOrganizationStore organizations,
        CancellationToken cancellationToken)
    {
        if (!UlidValue.TryParse(organizationId, out _))
        {
            return Problem(400, "invalid_organization_id", "Organization ID must be a ULID.");
        }

        var profile = await LocalProfileSession.ResolveAsync(request, profiles, cancellationToken);
        if (profile is null)
        {
            return SessionRequired();
        }

        var current = await organizations.GetAsync(profile.TenantId, organizationId, cancellationToken);
        if (current is null)
        {
            return Problem(404, "organization_not_found", "The organization does not exist.");
        }

        try
        {
            var updated = OrganizationApplicationService.Patch(ToContract(current), patch);
            var result = await organizations.UpdateAsync(
                new OrganizationUpdateCommand(
                    profile.TenantId,
                    updated.Id,
                    updated.Name,
                    updated.Slug,
                    updated.Plan,
                    ToRecord(updated.Brand),
                    current.Version),
                cancellationToken);
            return result.Status switch
            {
                OrganizationMutationStatus.Applied => Results.Ok(ToResponse(result.Organization!)),
                OrganizationMutationStatus.AlreadyExists => Conflict(),
                OrganizationMutationStatus.NotFound => Problem(
                    404, "organization_not_found", "The organization does not exist."),
                OrganizationMutationStatus.VersionConflict => Problem(
                    409, "organization_version_conflict", "The organization changed concurrently."),
                _ => throw new InvalidOperationException(
                    $"Unexpected organization update status: {result.Status}."),
            };
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "invalid_organization", exception.Message);
        }
    }

    private static OrganizationContract ToContract(OrganizationRecord organization) =>
        new(
            organization.Id,
            organization.Name,
            organization.Slug,
            organization.Plan,
            ToContract(organization.Brand),
            organization.DefaultWorkflowTemplateIds,
            organization.TemplateKeys,
            organization.Policies.Select(item =>
                new OrganizationPolicyContract(item.Key, item.Description, item.Enabled)).ToArray(),
            organization.CreatedAt,
            organization.Version);

    private static OrganizationResponse ToResponse(OrganizationRecord organization) =>
        new(
            organization.Id,
            organization.Name,
            organization.Slug,
            organization.Plan,
            ToContract(organization.Brand),
            organization.DefaultWorkflowTemplateIds,
            organization.TemplateKeys,
            organization.Policies.Select(item =>
                new OrganizationPolicyResponse(item.Key, item.Description, item.Enabled)).ToArray(),
            organization.CreatedAt);

    private static OrganizationBrandContract ToContract(OrganizationBrandRecord brand) =>
        new(brand.LogoUrl, brand.PrimaryColor, brand.SecondaryColor, brand.Typography);

    private static OrganizationBrandRecord ToRecord(OrganizationBrandContract brand) =>
        new(brand.LogoUrl, brand.PrimaryColor, brand.SecondaryColor, brand.Typography);

    private static IResult SessionRequired() =>
        Problem(401, "local_session_required", "A local profile session is required.");

    private static IResult Conflict() =>
        Problem(409, "organization_already_exists", "An organization with this name or slug already exists.");

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record OrganizationResponse(
    string Id,
    string Name,
    string Slug,
    string Plan,
    OrganizationBrandContract Brand,
    IReadOnlyList<string> DefaultWorkflowTemplateIds,
    IReadOnlyList<string> TemplateKeys,
    IReadOnlyList<OrganizationPolicyResponse> Policies,
    DateTimeOffset CreatedAt);

public sealed record OrganizationPolicyResponse(string Key, string Description, bool Enabled);

public sealed record OrganizationPage(IReadOnlyList<OrganizationResponse> Items, string? NextCursor);
