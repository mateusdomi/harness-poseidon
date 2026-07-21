using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.Providers;
using Harness.SharedKernel.Time;

namespace Harness.Host.Demo;

/// <summary>
/// Provisiona os dados de demonstração. Registrado somente sob `Harness:Demo:Enabled`
/// (ADR-018): é o único caminho que semeia contas, modelos, cotas e roteamento simulados.
/// O pacote de homologação normal nunca executa este serviço e permanece fail-closed.
/// </summary>
public sealed class DemoDataHostedService(
    ILocalProfileStore profiles,
    IOrganizationStore organizations,
    IProjectStore projects,
    IProviderCatalogStore providers,
    IClock clock) : IHostedService
{
    internal const string TenantId = "01J00000000000000000000001";
    internal const string ProfileId = "01J00000000000000000000002";
    internal const string OrganizationId = "01J00000000000000000000003";
    internal const string ProjectId = "01J00000000000000000000004";
    internal const string ChiefAgentId = "01J00000000000000000000005";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if ((await profiles.ListAsync(cancellationToken)).Count != 0) return;
        var now = clock.UtcNow;
        var profile = await profiles.CreateAsync(new LocalProfileCreateCommand(
            TenantId, "Poseidon Demo", ProfileId, "Pessoa de homologação", null, null, "pt-BR", now), cancellationToken);
        if (profile.Status != LocalProfileMutationStatus.Applied) return;
        // Catálogo simulado de provider/modelo/cota: existe apenas no modo demo e é sempre
        // reportado como `Simulated` pelo read model de prontidão (ADR-017/ADR-018). Semeado
        // depois do perfil porque é o perfil que provisiona o tenant referenciado pelas FKs, e
        // antes do projeto para que o Chief da demo nasça com modelo resolvível.
        await providers.SeedSimulatedCatalogAsync(TenantId, cancellationToken);
        var organization = await organizations.CreateAsync(new OrganizationCreateCommand(
            TenantId, OrganizationId, "Poseidon Demo", "poseidon-demo", "personal",
            new OrganizationBrandRecord(null, null, null, null), now), cancellationToken);
        if (organization.Status != OrganizationMutationStatus.Applied)
            throw new InvalidOperationException("Demo organization could not be provisioned.");
        var project = await projects.CreateAsync(new ProjectCreateCommand(TenantId,
            new ProjectRecord(TenantId, ProjectId, OrganizationId, "Projeto de homologação", "DEMO",
                "Dados opcionais, locais e sem segredos.", "active", "low", null, "local", "develop", [],
                new ProjectBrandRecord(null, null, null, null), [ProfileId], 1, ChiefAgentId, "manual", now, now, 0), now),
            cancellationToken);
        if (project.Status != ProjectMutationStatus.Applied)
            throw new InvalidOperationException("Demo project could not be provisioned.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
