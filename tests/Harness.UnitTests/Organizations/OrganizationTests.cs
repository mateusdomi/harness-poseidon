using Harness.Modules.Organizations.Application;
using Harness.Modules.Organizations.Contracts;

namespace Harness.UnitTests.Organizations;

public sealed class OrganizationTests
{
    private static readonly DateTimeOffset Initial =
        new(2026, 7, 18, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateNormalizesDefaultsAndValidatesOrganization()
    {
        var organization = OrganizationApplicationService.Create(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            new CreateOrganizationRequest
            {
                Name = "  Poseidon  ",
                Slug = "POSEIDON-BACKEND",
                Brand = new OrganizationBrandContract(null, "  #123456  ", null, "Inter"),
            },
            Initial);

        Assert.Equal("Poseidon", organization.Name);
        Assert.Equal("poseidon-backend", organization.Slug);
        Assert.Equal("personal", organization.Plan);
        Assert.Equal("#123456", organization.Brand.PrimaryColor);
        Assert.Empty(organization.DefaultWorkflowTemplateIds);
        Assert.Empty(organization.TemplateKeys);
        Assert.Empty(organization.Policies);
        Assert.Equal(1, organization.Version);

        Assert.Throws<ArgumentException>(() => OrganizationApplicationService.Create(
            organization.Id,
            new CreateOrganizationRequest { Name = "Poseidon", Slug = "invalid slug" },
            Initial));
    }

    [Fact]
    public void PatchPreservesMissingFieldsAndRejectsNullBrandAndEmptyPatch()
    {
        var organization = OrganizationApplicationService.Create(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            new CreateOrganizationRequest
            {
                Name = "Poseidon",
                Slug = "poseidon",
                Plan = "team",
            },
            Initial);
        var updated = OrganizationApplicationService.Patch(
            organization,
            new UpdateOrganizationRequest { Name = "Poseidon Labs" });

        Assert.Equal("Poseidon Labs", updated.Name);
        Assert.Equal("poseidon", updated.Slug);
        Assert.Equal("team", updated.Plan);
        Assert.Equal(2, updated.Version);
        Assert.Throws<ArgumentException>(() => OrganizationApplicationService.Patch(
            organization,
            new UpdateOrganizationRequest()));
        Assert.Throws<ArgumentException>(() => OrganizationApplicationService.Patch(
            organization,
            new UpdateOrganizationRequest { Brand = null }));
    }
}
