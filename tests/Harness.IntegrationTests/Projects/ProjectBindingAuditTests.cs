using System.Globalization;
using Harness.Host.Profiles;
using Harness.Host.Projects;
using Harness.Host.Workers;
using Harness.Host.Workflows;
using Harness.IntegrationTests.Persistence;
using Harness.Modules.Governance.Context;
using Harness.Modules.Projects.Application;
using Harness.Modules.Projects.Contracts;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;

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
            var organizations = new SqliteOrganizationStore(dispatcher);

            var tenantId = "01ARZ3NDEKTSV4RRFFQ69G5FQ0";
            var projectId = UlidValue.New(DateTimeOffset.UtcNow).ToString();
            var orgId = UlidValue.New(DateTimeOffset.UtcNow).ToString();
            var chiefId = UlidValue.New(DateTimeOffset.UtcNow).ToString();
            var ownerId = UlidValue.New(DateTimeOffset.UtcNow).ToString();
            var now = DateTimeOffset.UtcNow;
            var targetDeadline = now.AddDays(30);

            // A cadeia de posse e real: tenant -> organizacao -> projeto. Sem os
            // dois primeiros o store devolve OrganizationNotFound e a auditoria
            // de binding nem comeca.
            await InsertTenantAsync(dispatcher, tenantId, now, timeout.Token);
            await InsertProfileAsync(dispatcher, tenantId, ownerId, now, timeout.Token);
            var organizationResult = await organizations.CreateAsync(
                new OrganizationCreateCommand(
                    tenantId, orgId, "Organizacao da auditoria", "org-auditoria", "personal",
                    new OrganizationBrandRecord(null, null, null, null), now),
                timeout.Token);
            Assert.Equal(OrganizationMutationStatus.Applied, organizationResult.Status);

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

            var workflowCatalog = new SqliteWorkflowCatalogStore(dispatcher);
            var workflowAuthority = new SqliteWorkflowStore(dispatcher);
            var clock = new FixedClock(now.AddMinutes(1));
            var workflowSeeder = new WorkflowTemplateSeeder(
                workflowAuthority,
                workflowCatalog,
                clock);
            var workflowConvergence = new ProjectWorkflowConvergenceSeeder(
                store,
                workflowCatalog,
                workflowSeeder,
                workflowAuthority,
                clock);
            Assert.Equal(
                1,
                await workflowConvergence.EnsureBoundAsync(
                    tenantId,
                    ownerId,
                    timeout.Token));

            var composer = new ChiefContextComposer(
                new SqliteConversationStore(dispatcher),
                new DefaultContextStrategy(),
                new SqliteChiefContextNoteStore(dispatcher),
                clock,
                new ChiefContextStrategyOptions(
                    false,
                    new ContextStrategyBudget(2_000, 20, 4),
                    200),
                store,
                workflowCatalog);
            var chiefContext = await composer.ComposeProjectAsync(
                tenantId,
                projectId,
                timeout.Token);

            Assert.NotNull(chiefContext);
            Assert.Equal("Project Audit Test", chiefContext.Title);
            Assert.Equal("Auditing all persisted project fields.", chiefContext.Objective);
            Assert.Equal(
                targetDeadline.ToUniversalTime().ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture),
                chiefContext.TargetDeadline);
            Assert.Equal("https://example.com/logo.png", chiefContext.Brand.LogoUrl);
            Assert.Equal(["C#", "SQLite"], chiefContext.Technologies);
            Assert.NotNull(chiefContext.WorkflowTemplateId);
            Assert.False(string.IsNullOrWhiteSpace(chiefContext.WorkflowName));
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                try { Directory.Delete(artifactRoot, true); } catch { }
            }
        }
    }

    private static async Task InsertTenantAsync(
        SqliteWriteDispatcher dispatcher, string tenantId, DateTimeOffset now, CancellationToken token) =>
        await dispatcher.ExecuteAsync<int>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO tenants(id,name,version,created_at) VALUES($id,$name,0,$at);";
            command.Parameters.AddWithValue("$id", tenantId);
            command.Parameters.AddWithValue("$name", "Tenant " + tenantId);
            command.Parameters.AddWithValue("$at", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct);
            return 0;
        }, token);

    private static async Task InsertProfileAsync(
        SqliteWriteDispatcher dispatcher,
        string tenantId,
        string profileId,
        DateTimeOffset now,
        CancellationToken token) =>
        await dispatcher.ExecuteAsync<int>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO local_users(id,tenant_id,display_name,version,created_at) " +
                "VALUES($id,$tenant,$name,0,$at);";
            command.Parameters.AddWithValue("$id", profileId);
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$name", "Owner");
            command.Parameters.AddWithValue(
                "$at",
                now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct);
            return 0;
        }, token);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        private long _tick;
        public DateTimeOffset UtcNow => now.AddTicks(Interlocked.Increment(ref _tick));
    }
}
