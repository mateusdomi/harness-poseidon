using Harness.Modules.Projects.Application;
using Harness.Modules.Projects.Contracts;

namespace Harness.UnitTests.Projects;

public sealed class ProjectTests
{
    private static readonly DateTimeOffset Initial =
        new(2026, 7, 18, 20, 30, 0, TimeSpan.Zero);

    [Fact]
    public void CreateNormalizesContractAndAppliesSafeDefaults()
    {
        var project = ProjectApplicationService.Create(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            "01ARZ3NDEKTSV4RRFFQ69G5FAX",
            new CreateProjectRequest
            {
                OrganizationId = "01ARZ3NDEKTSV4RRFFQ69G5FAY",
                Name = "  Poseidon  ",
                Key = "poseidon",
                Description = " Backend principal ",
                Technologies = [".NET", ".NET", "SQLite"],
            },
            Initial);

        Assert.Equal("Poseidon", project.Name);
        Assert.Equal("POSEIDON", project.Key);
        Assert.Equal("active", project.State);
        Assert.Equal("medium", project.Criticality);
        Assert.Equal("local", project.RepositoryProvider);
        Assert.Equal("main", project.DefaultBranch);
        Assert.Equal([".NET", "SQLite"], project.Technologies);
        Assert.Equal(["01ARZ3NDEKTSV4RRFFQ69G5FAX"], project.MemberProfileIds);
        Assert.Equal(1, project.ConfigVersion);
        Assert.Equal("manual", project.OperationMode);
    }

    [Fact]
    public void PatchOnlyAdvancesConfigVersionForVersionedConfiguration()
    {
        var project = ProjectApplicationService.Create(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            "01ARZ3NDEKTSV4RRFFQ69G5FAX",
            new CreateProjectRequest
            {
                OrganizationId = "01ARZ3NDEKTSV4RRFFQ69G5FAY",
                Name = "Poseidon",
                Key = "POSEIDON",
                Description = "Backend principal",
            },
            Initial);
        var renamed = ProjectApplicationService.Patch(
            project,
            new UpdateProjectRequest { Name = "Poseidon Labs" },
            Initial.AddMinutes(1));
        var configured = ProjectApplicationService.Patch(
            renamed,
            new UpdateProjectRequest
            {
                RepositoryUrl = "https://github.com/example/poseidon",
                RepositoryProvider = "github",
                Technologies = [".NET", "React"],
            },
            Initial.AddMinutes(2));

        Assert.Equal(1, renamed.ConfigVersion);
        Assert.Equal(2, configured.ConfigVersion);
        Assert.Equal(3, configured.Version);
        Assert.Throws<ArgumentException>(() => ProjectApplicationService.Patch(
            project,
            new UpdateProjectRequest(),
            Initial.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() => ProjectApplicationService.Patch(
            project,
            new UpdateProjectRequest { State = "unknown" },
            Initial.AddMinutes(1)));
    }
}
