using System.Xml.Linq;

namespace Harness.ArchitectureTests;

public sealed class SolutionStructureTests
{
    private static readonly string[] ExpectedSourceProjects =
    [
        "src/Harness.Host/Harness.Host.csproj",
        "src/Harness.Runner/Harness.Runner.csproj",
        "src/Harness.Launcher/Harness.Launcher.csproj",
        "src/Harness.SharedKernel/Harness.SharedKernel.csproj",
        "src/Harness.Persistence.Abstractions/Harness.Persistence.Abstractions.csproj",
        "src/Harness.Persistence.Sqlite/Harness.Persistence.Sqlite.csproj",
        "src/Harness.Persistence.Postgres/Harness.Persistence.Postgres.csproj",
        "src/Modules/Harness.Modules.Identity/Harness.Modules.Identity.csproj",
        "src/Modules/Harness.Modules.Organizations/Harness.Modules.Organizations.csproj",
        "src/Modules/Harness.Modules.Projects/Harness.Modules.Projects.csproj",
        "src/Modules/Harness.Modules.Conversations/Harness.Modules.Conversations.csproj",
        "src/Modules/Harness.Modules.Coordination/Harness.Modules.Coordination.csproj",
        "src/Modules/Harness.Modules.Workflows/Harness.Modules.Workflows.csproj",
        "src/Modules/Harness.Modules.Execution/Harness.Modules.Execution.csproj",
        "src/Modules/Harness.Modules.Agents/Harness.Modules.Agents.csproj",
        "src/Modules/Harness.Modules.Providers/Harness.Modules.Providers.csproj",
        "src/Modules/Harness.Modules.Documents/Harness.Modules.Documents.csproj",
        "src/Modules/Harness.Modules.Governance/Harness.Modules.Governance.csproj",
        "src/Modules/Harness.Modules.Notifications/Harness.Modules.Notifications.csproj",
        "src/Modules/Harness.Modules.Prototyping/Harness.Modules.Prototyping.csproj",
        "src/Modules/Harness.Modules.Tools/Harness.Modules.Tools.csproj",
        "src/Modules/Harness.Modules.Licensing/Harness.Modules.Licensing.csproj",
    ];

    private static readonly string[] ExpectedTestProjects =
    [
        "tests/Harness.ArchitectureTests/Harness.ArchitectureTests.csproj",
        "tests/Harness.UnitTests/Harness.UnitTests.csproj",
        "tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj",
        "tests/Harness.ContractTests/Harness.ContractTests.csproj",
        "tests/Harness.RecoveryTests/Harness.RecoveryTests.csproj",
        "tests/Harness.ConcurrencyTests/Harness.ConcurrencyTests.csproj",
    ];

    [Fact]
    public void FrozenSolutionTopologyIsPresent()
    {
        var root = FindRepositoryRoot();
        var missing = ExpectedSourceProjects
            .Concat(ExpectedTestProjects)
            .Where(relativePath => !File.Exists(Path.Combine(root, relativePath)))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void ModulesDoNotDeclareForbiddenDirectDependencies()
    {
        var root = FindRepositoryRoot();
        var moduleProjects = ExpectedSourceProjects
            .Where(path => path.StartsWith("src/Modules/", StringComparison.Ordinal));

        foreach (var relativePath in moduleProjects)
        {
            var document = XDocument.Load(Path.Combine(root, relativePath));
            var forbiddenReferences = document
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => include is not null &&
                    !include.EndsWith("Harness.SharedKernel.csproj", StringComparison.Ordinal))
                .ToArray();

            Assert.True(
                forbiddenReferences.Length == 0,
                $"{relativePath} contém dependências diretas proibidas: {string.Join(", ", forbiddenReferences)}");
        }
    }

    [Fact]
    public void ProductionProjectsDoNotReferenceTestProjects()
    {
        var root = FindRepositoryRoot();

        foreach (var relativePath in ExpectedSourceProjects)
        {
            var document = XDocument.Load(Path.Combine(root, relativePath));
            var testReferences = document
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => include?.Contains("tests/", StringComparison.OrdinalIgnoreCase) is true)
                .ToArray();

            Assert.Empty(testReferences);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Não foi possível localizar a raiz contendo Harness.sln.");
    }
}
