using Harness.Modules.Projects.Contracts;
using Harness.Modules.Projects.Domain;

namespace Harness.Modules.Projects.Application;

public static class ProjectApplicationService
{
    public static ProjectContract Create(string id, string chiefAgentId, string ownerProfileId, CreateProjectRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ToContract(Project.Create(id, request.OrganizationId, chiefAgentId, ownerProfileId,
            request.Name, request.Key, request.Description, request.Criticality, request.RepositoryUrl,
            request.RepositoryProvider, request.DefaultBranch, request.Technologies,
            ToDomain(request.Brand ?? ProjectBrandContract.Empty), request.MemberProfileIds, now));
    }

    public static ProjectContract Patch(ProjectContract current, UpdateProjectRequest patch, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(patch);
        if (!patch.AnySpecified) throw new ArgumentException("Project patch cannot be empty.", nameof(patch));
        var project = ToDomain(current);
        return ToContract(project.Update(
            patch.NameSpecified ? patch.Name! : current.Name,
            patch.DescriptionSpecified ? patch.Description! : current.Description,
            patch.StateSpecified ? patch.State! : current.State,
            patch.CriticalitySpecified ? patch.Criticality! : current.Criticality,
            patch.RepositoryUrlSpecified ? patch.RepositoryUrl : current.RepositoryUrl,
            patch.RepositoryProviderSpecified ? patch.RepositoryProvider! : current.RepositoryProvider,
            patch.DefaultBranchSpecified ? patch.DefaultBranch! : current.DefaultBranch,
            patch.TechnologiesSpecified ? patch.Technologies ?? throw new ArgumentException("Technologies cannot be null.") : current.Technologies,
            patch.BrandSpecified ? ToDomain(patch.Brand ?? throw new ArgumentException("Brand cannot be null.")) : project.Brand,
            patch.MemberProfileIdsSpecified ? patch.MemberProfileIds ?? throw new ArgumentException("Members cannot be null.") : current.MemberProfileIds,
            patch.ConfigurationSpecified, now));
    }

    private static Project ToDomain(ProjectContract value) => new(
        value.Id, value.OrganizationId, value.Name, value.Key, value.Description, value.State,
        value.Criticality, value.RepositoryUrl, value.RepositoryProvider, value.DefaultBranch,
        value.Technologies, ToDomain(value.Brand), value.MemberProfileIds, value.ConfigVersion,
        value.ChiefAgentId, value.OperationMode, value.CreatedAt, value.LastActivityAt, value.Version);

    private static ProjectBrand ToDomain(ProjectBrandContract value) =>
        new(value.LogoUrl, value.PrimaryColor, value.SecondaryColor, value.Typography);

    private static ProjectContract ToContract(Project value) => new(
        value.Id, value.OrganizationId, value.Name, value.Key, value.Description, value.State,
        value.Criticality, value.RepositoryUrl, value.RepositoryProvider, value.DefaultBranch,
        value.Technologies, new ProjectBrandContract(value.Brand.LogoUrl, value.Brand.PrimaryColor,
            value.Brand.SecondaryColor, value.Brand.Typography), value.MemberProfileIds,
        value.ConfigVersion, value.ChiefAgentId, value.OperationMode, value.CreatedAt,
        value.LastActivityAt, value.Version);
}
