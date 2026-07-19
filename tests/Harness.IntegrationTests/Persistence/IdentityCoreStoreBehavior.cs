using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Persistence;

/// <summary>
/// Cenário provider-neutro do núcleo de identidade/tenancy do modo servidor:
/// perfil (tenant), organização, projeto e auditoria devem se comportar de forma
/// idêntica em SQLite e PostgreSQL.
/// </summary>
public static class IdentityCoreStoreBehavior
{
    public static async Task AssertAsync(
        ILocalProfileStore profiles,
        IOrganizationStore organizations,
        IProjectStore projects,
        IAuditEventStore audit,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Perfil: o guard do modo pessoal permite UM perfil por instalação; em banco
        // recém-criado a criação aplica, em banco já povoado ela é recusada — nos dois
        // casos a duplicata é AlreadyExists e o update usa OCC.
        var created = await profiles.CreateAsync(
            new LocalProfileCreateCommand(
                UlidValue.New(now).ToString(), "Personal",
                UlidValue.New(now.AddMilliseconds(1)).ToString(),
                "Mateus", null, null, "pt-BR", now),
            cancellationToken);
        LocalProfileRecord profile;
        if (created.Status == LocalProfileMutationStatus.Applied)
        {
            profile = created.Profile!;
            var duplicate = await profiles.CreateAsync(
                new LocalProfileCreateCommand(
                    UlidValue.New(now.AddMilliseconds(2)).ToString(), "Personal",
                    UlidValue.New(now.AddMilliseconds(2)).ToString(),
                    "Outro", null, null, "pt-BR", now),
                cancellationToken);
            Assert.Equal(LocalProfileMutationStatus.AlreadyExists, duplicate.Status);
        }
        else
        {
            Assert.Equal(LocalProfileMutationStatus.AlreadyExists, created.Status);
            profile = (await profiles.ListAsync(cancellationToken))[0];
        }

        var tenantId = profile.TenantId;
        var profileId = profile.Id;
        var fetched = await profiles.GetAsync(profileId, cancellationToken);
        Assert.NotNull(fetched);
        var updated = await profiles.UpdateAsync(
            new LocalProfileUpdateCommand(
                profileId, "Mateus Arquiteto", "m@example.com", null, "pt-BR",
                fetched!.Version, now.AddMilliseconds(3)),
            cancellationToken);
        Assert.Equal(LocalProfileMutationStatus.Applied, updated.Status);
        var stale = await profiles.UpdateAsync(
            new LocalProfileUpdateCommand(
                profileId, "Stale", null, null, "pt-BR", fetched.Version, now.AddMilliseconds(4)),
            cancellationToken);
        Assert.Equal(LocalProfileMutationStatus.VersionConflict, stale.Status);

        // Organização: criação, leitura e slug único por tenant.
        var organizationId = UlidValue.New(now.AddMilliseconds(5)).ToString();
        var organization = await organizations.CreateAsync(
            new OrganizationCreateCommand(
                tenantId, organizationId, $"Poseidon {organizationId[^6..]}", $"poseidon-{organizationId[^6..].ToLowerInvariant()}", "personal",
                new OrganizationBrandRecord(null, null, null, null),
                now.AddMilliseconds(5)),
            cancellationToken);
        Assert.Equal(OrganizationMutationStatus.Applied, organization.Status);
        var slugTaken = await organizations.CreateAsync(
            new OrganizationCreateCommand(
                tenantId, UlidValue.New(now.AddMilliseconds(6)).ToString(),
                $"Poseidon 2 {organizationId[^6..]}", $"poseidon-{organizationId[^6..].ToLowerInvariant()}", "personal",
                new OrganizationBrandRecord(null, null, null, null),
                now.AddMilliseconds(6)),
            cancellationToken);
        Assert.NotEqual(OrganizationMutationStatus.Applied, slugTaken.Status);
        Assert.Contains(
            await organizations.ListAsync(tenantId, null, 50, cancellationToken),
            value => value.Id == organizationId);

        // Projeto: criação exige organização do tenant; soft delete respeita OCC.
        var projectId = UlidValue.New(now.AddMilliseconds(7)).ToString();
        var chiefAgentId = UlidValue.New(now.AddMilliseconds(8)).ToString();
        ProjectRecord Project(string organization) => new(
            tenantId, projectId, organization, "Poseidon", "POSEIDON", "Backend",
            "active", "medium", null, "local", "main",
            [], new ProjectBrandRecord(null, null, null, null), [profileId],
            1, chiefAgentId, "manual", now.AddMilliseconds(7), now.AddMilliseconds(7), 0);
        var missingOrganization = await projects.CreateAsync(
            new ProjectCreateCommand(
                tenantId,
                Project(UlidValue.New(now.AddMilliseconds(9)).ToString()),
                now.AddMilliseconds(9)),
            cancellationToken);
        Assert.Equal(ProjectMutationStatus.OrganizationNotFound, missingOrganization.Status);
        var project = await projects.CreateAsync(
            new ProjectCreateCommand(tenantId, Project(organizationId), now.AddMilliseconds(10)),
            cancellationToken);
        Assert.Equal(ProjectMutationStatus.Applied, project.Status);
        var read = await projects.GetAsync(tenantId, projectId, cancellationToken);
        Assert.Equal("POSEIDON", read!.Key);
        Assert.Contains(
            await projects.ListAsync(tenantId, null, 50, cancellationToken),
            value => value.Id == projectId);
        var deleted = await projects.DeleteAsync(
            tenantId, projectId, read.Version, now.AddMilliseconds(11), cancellationToken);
        Assert.Equal(ProjectMutationStatus.Applied, deleted.Status);
        Assert.DoesNotContain(
            await projects.ListAsync(tenantId, null, 50, cancellationToken),
            value => value.Id == projectId);

        // Auditoria: append tipado, filtro e integridade da cadeia de hashes.
        await audit.AppendAsync(
            new AuditEventAppendCommand(
                tenantId, "user", profileId, "server.parityChecked", "tenant", tenantId,
                "Paridade dual-provider do núcleo verificada.", now.AddMilliseconds(12)),
            cancellationToken);
        await audit.AppendAsync(
            new AuditEventAppendCommand(
                tenantId, "system", null, "server.parityChecked", "tenant", tenantId,
                "Segundo elo da cadeia.", now.AddMilliseconds(13)),
            cancellationToken);
        var events = await audit.ListAsync(
            new AuditEventQuery(tenantId, null, 50, Action: "server.parityChecked"),
            cancellationToken);
        Assert.Equal(2, events.Count);
        var integrity = await audit.VerifyIntegrityAsync(tenantId, cancellationToken);
        Assert.True(integrity.Valid);
        Assert.True(integrity.EntryCount >= 2);
    }
}
