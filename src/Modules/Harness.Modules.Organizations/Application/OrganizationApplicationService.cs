using Harness.Modules.Organizations.Contracts;
using Harness.Modules.Organizations.Domain;

namespace Harness.Modules.Organizations.Application;

public static class OrganizationApplicationService
{
    public static OrganizationContract Create(
        string organizationId,
        CreateOrganizationRequest request,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        var brand = ToDomain(request.Brand ?? OrganizationBrandContract.Empty);
        return ToContract(Organization.Create(
            organizationId,
            request.Name,
            request.Slug,
            request.Plan,
            brand,
            occurredAt));
    }

    public static OrganizationContract Patch(
        OrganizationContract current,
        UpdateOrganizationRequest patch)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(patch);
        if (!patch.NameSpecified && !patch.SlugSpecified &&
            !patch.PlanSpecified && !patch.BrandSpecified)
        {
            throw new ArgumentException("Organization patch cannot be empty.", nameof(patch));
        }

        var organization = new Organization(
            current.Id,
            current.Name,
            current.Slug,
            current.Plan,
            ToDomain(current.Brand),
            current.DefaultWorkflowTemplateIds,
            current.TemplateKeys,
            current.Policies.Select(item =>
                new OrganizationPolicy(item.Key, item.Description, item.Enabled)).ToArray(),
            current.CreatedAt,
            current.Version);
        return ToContract(organization.Update(
            patch.NameSpecified ? patch.Name! : current.Name,
            patch.SlugSpecified ? patch.Slug! : current.Slug,
            patch.PlanSpecified ? patch.Plan! : current.Plan,
            patch.BrandSpecified
                ? ToDomain(patch.Brand ?? throw new ArgumentException("Brand cannot be null.", nameof(patch)))
                : organization.Brand));
    }

    private static OrganizationBrand ToDomain(OrganizationBrandContract brand) =>
        OrganizationBrand.Create(
            brand.LogoUrl,
            brand.PrimaryColor,
            brand.SecondaryColor,
            brand.Typography);

    private static OrganizationContract ToContract(Organization organization) =>
        new(
            organization.Id,
            organization.Name,
            organization.Slug,
            organization.Plan,
            new OrganizationBrandContract(
                organization.Brand.LogoUrl,
                organization.Brand.PrimaryColor,
                organization.Brand.SecondaryColor,
                organization.Brand.Typography),
            organization.DefaultWorkflowTemplateIds,
            organization.TemplateKeys,
            organization.Policies.Select(item =>
                new OrganizationPolicyContract(item.Key, item.Description, item.Enabled)).ToArray(),
            organization.CreatedAt,
            organization.Version);
}
