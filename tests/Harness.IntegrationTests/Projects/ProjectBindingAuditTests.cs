using System.Globalization;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.IntegrationTests.Persistence;
using Harness.Modules.Projects.Application;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Projects;

public sealed class ProjectBindingAuditTests
{
    [Fact]
    public async Task PersistedProjectFieldsReachRecordAndContractAuditBindings()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var artifactRoot = Path.Combine(
            Path.GetTempPath(),
            "harness-tests",
            $"project-binding-audit-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(artifactRoot, "audit.db");
        Directory.CreateDirectory(artifactRoot);

        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            var store = new SqliteProjectStore(dispatcher);

            var tenantId = "01ARZ3NDEKTSV4RRFFQ69G5FQ0";
            var projectId = UlidValue.New().ToString();
            var orgId = UlidValue.New().ToString();
            var chiefId = UlidValue.New().ToString();
            var ownerId = UlidValue.New().ToString();
            var now = DateTimeOffset.UtcNow;
            var targetDeadline = now.AddDays(30);

            var createReq = new CreateProjectRequest
            {
                OrganizationId = orgId,
                Name = "Project Audit Test",
                Key = "AUDIT-PROJ",
                Description = "Auditing all persisted project fields.",
                Criticality = "high",
                RepositoryUrl = "local://repositories/audit-proj",
                RepositoryProvider = "local",
                DefaultBranch = "main",
                Technologies = ["C#", "SQLite"],
                Brand = new ProjectBrandContract("https://example.com/logo.png", "#123456", "#654321", "Inter"),
                MemberProfileIds = [ownerId],
                TargetDeadline = targetDeadline,
            };

            var contract = ProjectApplicationService.Create(projectId, orgId, chiefId, ownerId, createReq, now);
            var record = new ProjectRecord(
                tenantId, contract.Id, contract.OrganizationId, contract.Name, contract.Key, contract.Description,
                contract.State, contract.Criticality, contract.RepositoryUrl, contract.RepositoryProvider, contract.DefaultBranch,
                contract.Technologies, new(contract.Brand.LogoUrl, contract.Brand.PrimaryColor, contract.Brand.SecondaryColor, contract.Brand.Typography),
                contract.MemberProfileIds, contract.ConfigVersion, contract.ChiefAgentId, contract.OperationMode, contract.CreatedAt,
                contract.LastActivityAt, contract.Version)
            {
                TargetDeadline = contract.TargetDeadline
            };

            var result = await store.CreateAsync(new(tenantId, record, now), timeout.Token);
            Assert.Equal(ProjectMutationStatus.Applied, result.Status);

            var fetched = await store.GetAsync(tenantId, projectId, timeout.Token);
            Assert.NotNull(fetched);
            Assert.Equal("Project Audit Test", fetched.Name);
            Assert.Equal("AUDIT-PROJ", fetched.Key);
            Assert.Equal("Auditing all persisted project fields.", fetched.Description);
            Assert.Equal("high", fetched.Criticality);
            Assert.Equal("local://repositories/audit-proj", fetched.RepositoryUrl);
            Assert.Equal("local", fetched.RepositoryProvider);
            Assert.Equal("main", fetched.DefaultBranch);
            Assert.Equal(["C#", "SQLite"], fetched.Technologies);
            Assert.Equal("https://example.com/logo.png", fetched.Brand.LogoUrl);
            Assert.NotNull(fetched.TargetDeadline);
            Assert.Equal(targetDeadline.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                         fetched.TargetDeadline.Value.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                try { Directory.Delete(artifactRoot, true); } catch { }
            }
        }
    }
}
